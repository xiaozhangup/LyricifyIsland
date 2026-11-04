using System.Collections.Immutable;

namespace LyricifyIsland;

// Shares the on-disk cache with foreground loading, without publishing playback state.
internal sealed class TrackPrefetcher
{
    private CancellationTokenSource? _cancellation;
    private Task _worker = Task.CompletedTask;
    private string? _context;
    private DateTimeOffset _nextRefresh;
    private readonly HashSet<(string Id, LyricsSourcePreference Source)> _attempted = [];

    public void Update(
        string context,
        string currentId,
        LyricsSourcePreference lyricsSource,
        Task foregroundLoad,
        Func<CancellationToken, Task<IReadOnlyList<SourceTrack>>> getNeighbors,
        Func<SourceTrack, CancellationToken, Task<ImmutableArray<ImmutableArray<byte>>>>? loadArtists,
        CancellationToken cancellationToken)
    {
        var key = $"{context}:{lyricsSource}";
        if (_context != key)
        {
            Stop();
            _context = key;
        }
        if ((!_worker.IsCompleted && _cancellation is not null) || DateTimeOffset.UtcNow < _nextRefresh)
            return;

        _cancellation?.Dispose();
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _cancellation.Token;
        var previous = _worker;
        _nextRefresh = DateTimeOffset.UtcNow.AddSeconds(30);
        _worker = Task.Run(async () =>
        {
            try
            {
                await previous;
                await foregroundLoad.WaitAsync(token);
                var tracks = (await getNeighbors(token)).Where(track => track.Id != currentId)
                    .DistinctBy(track => track.Id).Take(10).ToArray();
                _attempted.IntersectWith(tracks.Select(track => (track.Id, lyricsSource)));
                foreach (var source in tracks)
                {
                    token.ThrowIfCancellationRequested();
                    var cached = TrackCache.Load(source.Id);
                    if (cached is not null && _attempted.Contains((source.Id, lyricsSource)))
                        continue;
                    try
                    {
                        var track = cached ?? new TrackInfo(source.Id, source.Title, source.Artists,
                            source.Album, source.DurationMs, [], []);
                        // Load one neighboring track at a time so current playback keeps priority.
                        var albumTask = track.AlbumArtBytes.IsDefaultOrEmpty || track.AlbumArtUrl != source.AlbumArtUrl
                            ? SpotifyService.LoadImageSafeAsync(source.AlbumArtUrl, token)
                            : Task.FromResult(track.AlbumArtBytes);
                        var artistsTask = loadArtists is not null && track.ArtistImageBytes.IsDefaultOrEmpty
                            ? loadArtists(source, token)
                            : Task.FromResult(track.ArtistImageBytes);
                        var lyricsTask = track.Lyrics.IsDefaultOrEmpty || track.LyricsSource != lyricsSource
                            ? LoadLyricsAsync(source, lyricsSource, token)
                            : Task.FromResult(track.Lyrics);
                        await Task.WhenAll(albumTask, artistsTask, lyricsTask);
                        token.ThrowIfCancellationRequested();
                        var lyrics = await lyricsTask;
                        TrackCache.SaveIfChanged(track with
                        {
                            AlbumArtUrl = source.AlbumArtUrl,
                            AlbumArtBytes = TrackCache.PreferLargerImage(track.AlbumArtBytes, await albumTask),
                            ArtistImageBytes = await artistsTask,
                            Lyrics = lyrics.IsDefaultOrEmpty ? track.Lyrics : lyrics,
                            LyricsSource = lyrics.IsDefaultOrEmpty ? track.LyricsSource : lyricsSource
                        });
                        _attempted.Add((source.Id, lyricsSource));
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception exception)
                    {
                        Console.Error.WriteLine($"[prefetch] track load failed: {exception.Message}");
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"[prefetch] queue load failed: {exception.Message}");
            }
        }, CancellationToken.None);
    }

    public void Stop()
    {
        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _cancellation = null;
        _context = null;
        _nextRefresh = default;
    }

    private static async Task<ImmutableArray<LyricLine>> LoadLyricsAsync(
        SourceTrack track, LyricsSourcePreference source, CancellationToken cancellationToken)
    {
        try { return await LyricsProvider.LoadAsync(track, source, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[prefetch] lyrics load failed: {exception.Message}");
            return [];
        }
    }
}
