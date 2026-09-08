using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Tmds.DBus.Protocol;

namespace LyricifyIsland;

internal sealed class MprisService : IPlaybackSource
{
    private const string ServicePrefix = "org.mpris.MediaPlayer2.";
    private const string PlayerPath = "/org/mpris/MediaPlayer2";
    private const string PlayerInterface = "org.mpris.MediaPlayer2.Player";
    private const string PropertiesInterface = "org.freedesktop.DBus.Properties";
    private const long PositionJitterToleranceMs = 1_000;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan PlayerTimeout = TimeSpan.FromSeconds(2);

    private readonly PlaybackStore _store;
    private readonly SemaphoreSlim _pollWake = new(0);
    private TrackInfo? _track;
    private TrackInfo? _loadingTrack;
    private CancellationTokenSource? _trackLoad;
    private string? _selectedService;
    private long _crossfadeOffsetMs;
    private int _lyricsSource;
    private int _refreshCurrentTrack;

    public MprisService(PlaybackStore store, LyricsSourcePreference lyricsSource)
    {
        _store = store;
        _lyricsSource = (int)lyricsSource;
    }

    public void SetLyricsSource(LyricsSourcePreference source) =>
        Volatile.Write(ref _lyricsSource, (int)source);

    public bool RequestCurrentTrackRefresh()
    {
        if (_store.Snapshot.Track is null)
            return false;
        if (Interlocked.Exchange(ref _refreshCurrentTrack, 1) == 0)
            _pollWake.Release();
        return true;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var delay = PollInterval;
            try
            {
                await PollAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                SetStatus($"MPRIS 连接失败：{Friendly(exception)}");
                delay = TimeSpan.FromSeconds(2);
            }

            try
            {
                _ = await _pollWake.WaitAsync(delay, cancellationToken);
                while (_pollWake.Wait(0)) { }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
        CancelTrackLoad();
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        var services = (await DBusConnection.Session.ListServicesAsync().WaitAsync(cancellationToken))
            .Where(name => name.StartsWith(ServicePrefix, StringComparison.Ordinal))
            .ToArray();
        var players = (await Task.WhenAll(services.Select(service =>
                ReadPlayerSafeAsync(service, cancellationToken))))
            .OfType<Player>()
            .ToArray();
        var player = SelectPlayer(players, _selectedService);
        if (player is null)
        {
            ClearTrack();
            _store.Update(new PlaybackSnapshot(
                null,
                0,
                Stopwatch.GetTimestamp(),
                false,
                PlaybackStore.NoActiveMprisPlaybackStatus));
            return;
        }

        var reportedAt = Stopwatch.GetTimestamp();
        var previous = _store.Snapshot;
        player = player with { PositionMs = ApplyCrossfadeOffset(player, reportedAt) };
        player = player with
        {
            PositionMs = StabilizePosition(
                previous, player, reportedAt, _selectedService == player.Service)
        };
        _selectedService = player.Service;
        var forceRefresh = Interlocked.Exchange(ref _refreshCurrentTrack, 0) != 0;
        var currentTrack = Volatile.Read(ref _track);
        if (forceRefresh || !SameMetadata(currentTrack, player.Track)
            || currentTrack?.AlbumArtUrl != player.Track.AlbumArtUrl)
        {
            CancelTrackLoad();
            var reusableTrack = currentTrack?.Id == player.Track.Id
                ? currentTrack
                : TrackCache.Load(player.Track.Id);
            currentTrack = SameMetadata(reusableTrack, player.Track)
                ? reusableTrack!
                : new TrackInfo(
                player.Track.Id,
                player.Track.Title,
                player.Track.Artists,
                player.Track.Album,
                player.Track.DurationMs,
                [],
                []);
            currentTrack = currentTrack with { AlbumArtUrl = player.Track.AlbumArtUrl };
            Volatile.Write(ref _track, currentTrack);
            Volatile.Write(ref _loadingTrack, currentTrack);
            _store.Update(Snapshot(player, currentTrack, reportedAt));
            _trackLoad = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _ = CompleteTrackAsync(player.Track, currentTrack, _trackLoad.Token);
        }

        currentTrack = Volatile.Read(ref _track)!;
        _store.Update(Snapshot(player, currentTrack, reportedAt));
    }

    private PlaybackSnapshot Snapshot(Player player, TrackInfo track, long reportedAt)
    {
        var status = track.Lyrics.IsDefaultOrEmpty
            ? ReferenceEquals(track, Volatile.Read(ref _loadingTrack))
                ? "正在获取歌词…"
                : $"未找到歌词 · {string.Join(", ", track.Artists)}"
            : string.Join(", ", track.Artists);
        return new PlaybackSnapshot(
            track, player.PositionMs, reportedAt, player.IsPlaying, status, player.PlaybackRate);
    }

    private async Task CompleteTrackAsync(
        SourceTrack source,
        TrackInfo placeholder,
        CancellationToken cancellationToken)
    {
        try
        {
            var lyricsTask = LoadLyricsSafeAsync(source, cancellationToken);
            var albumTask = SpotifyService.LoadImageSafeAsync(source.AlbumArtUrl, cancellationToken);
            await Task.WhenAll(lyricsTask, albumTask);
            var lyrics = await lyricsTask;
            var album = await albumTask;
            var next = placeholder with
            {
                Title = source.Title,
                Artists = source.Artists,
                Album = source.Album,
                DurationMs = source.DurationMs,
                AlbumArtBytes = TrackCache.PreferLargerImage(placeholder.AlbumArtBytes, album),
                Lyrics = lyrics.IsEmpty ? placeholder.Lyrics : lyrics
            };
            if (Interlocked.CompareExchange(ref _track, next, placeholder) == placeholder)
                TrackCache.SaveIfChanged(next);
            Interlocked.CompareExchange(ref _loadingTrack, null, placeholder);
            _pollWake.Release();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[track] load failed: {Friendly(exception)}");
            Interlocked.CompareExchange(ref _loadingTrack, null, placeholder);
            _pollWake.Release();
        }
    }

    private async Task<ImmutableArray<LyricLine>> LoadLyricsSafeAsync(
        SourceTrack track,
        CancellationToken cancellationToken)
    {
        try
        {
            var source = (LyricsSourcePreference)Volatile.Read(ref _lyricsSource);
            return await LyricsProvider.LoadAsync(track, source, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return [];
        }
    }

    private static bool SameMetadata(TrackInfo? current, SourceTrack source) =>
        current?.Id == source.Id
        && current.Title == source.Title
        && current.Artists.SequenceEqual(source.Artists)
        && current.Album == source.Album
        && current.DurationMs == source.DurationMs;

    internal static long StabilizePosition(
        PlaybackSnapshot previous,
        Player player,
        long reportedAt,
        bool sameService)
    {
        if (!sameService
            || previous.Track?.Id != player.Track.Id
            || !previous.IsPlaying
            || !player.IsPlaying
            || previous.PlaybackRate != player.PlaybackRate)
            return player.PositionMs;

        var predicted = SpotifyService.StabilizePosition(
            previous, player.Track.Id, null, reportedAt, true, true);
        // ponytail: this deadband smooths whole-second players; subscribe to Seeked for sub-second seeks.
        return Math.Abs(player.PositionMs - predicted) <= PositionJitterToleranceMs
            ? predicted
            : player.PositionMs;
    }

    private long ApplyCrossfadeOffset(Player player, long reportedAt)
    {
        var previous = _store.Snapshot;
        if (previous.Track?.Id != player.Track.Id)
        {
            var crossfadeMs = _selectedService == player.Service
                && player.Service.StartsWith(ServicePrefix + "spotify", StringComparison.OrdinalIgnoreCase)
                    ? SpotifyService.ReadSpotifyCrossfadeMs()
                    : 0;
            _crossfadeOffsetMs = SpotifyService.IsAutomaticCrossfadeTransition(
                previous, reportedAt, player.IsPlaying, crossfadeMs)
                    ? crossfadeMs
                    : 0;
        }
        else if (!player.IsPlaying || !previous.IsPlaying)
        {
            _crossfadeOffsetMs = 0;
        }
        return Math.Max(0, player.PositionMs - _crossfadeOffsetMs);
    }

    private async Task<Player?> ReadPlayerSafeAsync(string service, CancellationToken cancellationToken)
    {
        try
        {
            var properties = await GetPlayerPropertiesAsync(service)
                .WaitAsync(PlayerTimeout, cancellationToken);
            return ParsePlayer(service, properties);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static Task<Dictionary<string, VariantValue>> GetPlayerPropertiesAsync(string service)
    {
        var connection = DBusConnection.Session;
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(
            destination: service,
            path: PlayerPath,
            @interface: PropertiesInterface,
            member: "GetAll",
            signature: "s");
        writer.WriteString(PlayerInterface);
        return connection.CallMethodAsync(
            writer.CreateMessage(),
            static (reply, _) => reply.GetBodyReader().ReadDictionaryOfStringToVariantValue());
    }

    internal static Player? ParsePlayer(
        string service,
        IReadOnlyDictionary<string, VariantValue> properties)
    {
        var playbackStatus = String(properties, "PlaybackStatus");
        if (playbackStatus is not ("Playing" or "Paused")
            || !properties.TryGetValue("Metadata", out var metadataValue))
            return null;

        metadataValue = Unwrap(metadataValue);
        if (metadataValue.Type != VariantValueType.Dictionary)
            return null;
        var metadata = metadataValue.GetDictionary<string, VariantValue>();
        var title = String(metadata, "xesam:title")?.Trim();
        if (string.IsNullOrEmpty(title))
            return null;

        var artists = Strings(metadata, "xesam:artist")
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .ToImmutableArray();
        var albumArtists = Strings(metadata, "xesam:albumArtist")
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .ToImmutableArray();
        if (albumArtists.IsDefaultOrEmpty)
            albumArtists = artists;
        var album = String(metadata, "xesam:album")?.Trim() ?? string.Empty;
        var durationMs = MicrosecondsToMilliseconds(Int64(metadata, "mpris:length"));
        var positionMs = MicrosecondsToMilliseconds(Int64(properties, "Position"));
        var playbackRate = Double(properties, "Rate");
        if (!double.IsFinite(playbackRate) || playbackRate <= 0)
            playbackRate = 1d;
        var url = String(metadata, "xesam:url");
        var trackPath = ObjectPath(metadata, "mpris:trackid");
        var id = BuildTrackId(service, url, trackPath, title, artists, album, durationMs);
        var track = new SourceTrack(
            id,
            title,
            artists,
            [],
            albumArtists,
            album,
            durationMs,
            null,
            String(metadata, "mpris:artUrl"));
        return new Player(service, track, positionMs, playbackStatus, playbackRate);
    }

    internal static Player? SelectPlayer(IReadOnlyList<Player> players, string? selectedService) =>
        players
            .OrderBy(player => player.IsPlaying ? 0 : 1)
            .ThenBy(player => player.Service == selectedService ? 0 : 1)
            .ThenBy(player => player.Service, StringComparer.Ordinal)
            .FirstOrDefault();

    internal static string BuildTrackId(
        string service,
        string? url,
        string? trackPath,
        string title,
        IEnumerable<string> artists,
        string album,
        long durationMs)
    {
        var identity = !string.IsNullOrWhiteSpace(url)
            ? url.Trim()
            : string.Join('\n', service, trackPath, title, string.Join('\u001f', artists), album, durationMs);
        return $"mpris:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant()}";
    }

    private static VariantValue Unwrap(VariantValue value) =>
        value.Type == VariantValueType.Variant ? value.GetVariantValue() : value;

    private static string? String(IReadOnlyDictionary<string, VariantValue> values, string key)
    {
        if (!values.TryGetValue(key, out var value))
            return null;
        value = Unwrap(value);
        return value.Type == VariantValueType.String ? value.GetString() : null;
    }

    private static string? ObjectPath(IReadOnlyDictionary<string, VariantValue> values, string key)
    {
        if (!values.TryGetValue(key, out var value))
            return null;
        value = Unwrap(value);
        return value.Type == VariantValueType.ObjectPath ? value.GetObjectPathAsString() : null;
    }

    private static string[] Strings(IReadOnlyDictionary<string, VariantValue> values, string key)
    {
        if (!values.TryGetValue(key, out var value))
            return [];
        value = Unwrap(value);
        if (value.Type == VariantValueType.String)
            return [value.GetString()];
        return value.Type == VariantValueType.Array && value.ItemType == VariantValueType.String
            ? value.GetArray<string>()
            : [];
    }

    private static long Int64(IReadOnlyDictionary<string, VariantValue> values, string key)
    {
        if (!values.TryGetValue(key, out var value))
            return 0;
        value = Unwrap(value);
        return value.Type switch
        {
            VariantValueType.Int64 => value.GetInt64(),
            VariantValueType.UInt64 => (long)Math.Min(value.GetUInt64(), long.MaxValue),
            _ => 0
        };
    }

    private static double Double(IReadOnlyDictionary<string, VariantValue> values, string key)
    {
        if (!values.TryGetValue(key, out var value))
            return 1d;
        value = Unwrap(value);
        return value.Type == VariantValueType.Double ? value.GetDouble() : 1d;
    }

    private static long MicrosecondsToMilliseconds(long value) => Math.Max(0, value / 1_000);

    private void ClearTrack()
    {
        CancelTrackLoad();
        Volatile.Write(ref _track, null);
        _selectedService = null;
        _crossfadeOffsetMs = 0;
    }

    private void CancelTrackLoad()
    {
        var trackLoad = Interlocked.Exchange(ref _trackLoad, null);
        if (trackLoad is not null)
        {
            trackLoad.Cancel();
            trackLoad.Dispose();
        }
        Volatile.Write(ref _loadingTrack, null);
    }

    private void SetStatus(string status)
    {
        var snapshot = _store.Snapshot;
        _store.Update(snapshot with { Status = status });
    }

    private static string Friendly(Exception exception) =>
        exception.GetBaseException().Message.Replace('\n', ' ').Replace('\r', ' ');

    internal sealed record Player(
        string Service,
        SourceTrack Track,
        long PositionMs,
        string PlaybackStatus,
        double PlaybackRate = 1d)
    {
        public bool IsPlaying => PlaybackStatus == "Playing";
    }
}
