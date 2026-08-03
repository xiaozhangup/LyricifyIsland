using System.Collections.Immutable;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LyricifyIsland;

internal sealed class SpotifyService
{
    private const string RedirectUri = "http://127.0.0.1:43821/callback";
    private const string Scope = "user-read-currently-playing user-read-playback-state";
    private const string CurrentlyPlayingUrl = "https://api.spotify.com/v1/me/player/currently-playing?additional_types=track";
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(750);
    private static readonly HttpClient Http = new();

    private readonly PlaybackStore _store;
    private readonly Credentials _credentials;
    private readonly SemaphoreSlim _pollWake = new(0);
    private TokenState? _tokens;
    private TrackInfo? _track;
    private TrackInfo? _loadingTrack;
    private CancellationTokenSource? _trackLoad;
    private long _lastPlaybackStateTimestamp;
    private int _positionSyncVersion;
    private int _positionSyncedVersion;

    public SpotifyService(PlaybackStore store, string clientId, string clientSecret)
    {
        _store = store;
        _credentials = new Credentials(clientId, clientSecret);
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            _tokens = await LoadTokenAsync(cancellationToken)
                ?? await AuthorizeAsync(cancellationToken);
            if (_tokens.ExpiresAt <= DateTimeOffset.UtcNow.AddSeconds(30))
                _tokens = await RefreshOrAuthorizeAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            SetStatus($"Spotify 配置/授权失败：{Friendly(exception)}");
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            var delay = PollInterval;
            try
            {
                if (_tokens!.ExpiresAt <= DateTimeOffset.UtcNow.AddSeconds(30))
                    _tokens = await RefreshOrAuthorizeAsync(cancellationToken);
                delay = await PollAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                SetStatus($"Spotify 连接失败：{Friendly(exception)}");
                delay = TimeSpan.FromSeconds(2);
            }

            try
            {
                if (delay == PollInterval)
                {
                    _ = await _pollWake.WaitAsync(delay, cancellationToken);
                    while (_pollWake.Wait(0)) { }
                }
                else
                {
                    await Task.Delay(delay, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
        CancelTrackLoad();
    }

    private async Task<TimeSpan> PollAsync(CancellationToken cancellationToken)
    {
        var positionSyncVersion = Volatile.Read(ref _positionSyncVersion);
        var forcePositionSync = positionSyncVersion != Volatile.Read(ref _positionSyncedVersion);
        if (forcePositionSync)
            _pollWake.Wait(0);
        var response = await SendCurrentTrackAsync(cancellationToken);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            response.Dispose();
            _tokens = await RefreshOrAuthorizeAsync(cancellationToken);
            response = await SendCurrentTrackAsync(cancellationToken);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NoContent)
            {
                ClearTrack();
                _store.Update(new PlaybackSnapshot(
                    null, 0, Stopwatch.GetTimestamp(), false, "Spotify 当前没有播放"));
                return PollInterval;
            }

            if ((int)response.StatusCode == 429)
            {
                var retry = RetryAfter(response);
                SetStatus($"Spotify 限流，{Math.Ceiling(retry.TotalSeconds):0} 秒后重试");
                return retry;
            }

            if (!response.IsSuccessStatusCode)
            {
                SetStatus($"Spotify HTTP {(int)response.StatusCode} {response.ReasonPhrase}".TrimEnd());
                return TimeSpan.FromSeconds(2);
            }

            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var json = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
            var root = json.RootElement;
            var reportedAt = Stopwatch.GetTimestamp();
            var reportedPosition = GetNullableInt64(root, "progress_ms");
            var isPlaying = GetBoolean(root, "is_playing");
            var playbackStateTimestamp = GetInt64(root, "timestamp");
            var samePlaybackState = !forcePositionSync && playbackStateTimestamp != 0
                && playbackStateTimestamp == _lastPlaybackStateTimestamp;

            if (!root.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object)
            {
                ClearTrack();
                _store.Update(new PlaybackSnapshot(
                    null, 0, reportedAt, false, "Spotify 当前没有播放"));
                return PollInterval;
            }

            var spotifyTrack = ParseTrack(item);
            if (spotifyTrack is null)
            {
                ClearTrack();
                _store.Update(new PlaybackSnapshot(
                    null, 0, reportedAt, false, "Spotify 返回的不是曲目"));
                return PollInterval;
            }

            var position = StabilizePosition(
                _store.Snapshot,
                spotifyTrack.Id,
                reportedPosition,
                reportedAt,
                isPlaying,
                samePlaybackState);
            if (forcePositionSync && reportedPosition.HasValue)
                Volatile.Write(ref _positionSyncedVersion, positionSyncVersion);
            if (reportedPosition.HasValue && playbackStateTimestamp != 0)
                _lastPlaybackStateTimestamp = playbackStateTimestamp;

            var currentTrack = Volatile.Read(ref _track);
            if (currentTrack?.Id != spotifyTrack.Id)
            {
                CancelTrackLoad();
                currentTrack = TrackCache.Load(spotifyTrack.Id) ?? new TrackInfo(
                    spotifyTrack.Id,
                    spotifyTrack.Title,
                    spotifyTrack.Artists,
                    spotifyTrack.Album,
                    spotifyTrack.DurationMs,
                    [],
                    []);
                Volatile.Write(ref _track, currentTrack);
                Volatile.Write(ref _loadingTrack, currentTrack);
                _store.Update(new PlaybackSnapshot(currentTrack, position, reportedAt, isPlaying,
                    currentTrack.Lyrics.IsDefaultOrEmpty
                        ? "正在获取歌词…"
                        : string.Join(", ", currentTrack.Artists)));
                _trackLoad = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _ = CompleteTrackAsync(spotifyTrack, currentTrack, _trackLoad.Token);
            }

            currentTrack = Volatile.Read(ref _track)!;
            var status = currentTrack.Lyrics.IsDefaultOrEmpty
                ? ReferenceEquals(currentTrack, Volatile.Read(ref _loadingTrack))
                    ? "正在获取歌词…"
                    : $"未找到歌词 · {string.Join(", ", currentTrack.Artists)}"
                : string.Join(", ", currentTrack.Artists);
            _store.Update(new PlaybackSnapshot(currentTrack, position, reportedAt, isPlaying, status));
            return PollInterval;
        }
    }

    private async Task CompleteTrackAsync(
        SpotifyTrack spotifyTrack,
        TrackInfo placeholder,
        CancellationToken cancellationToken)
    {
        var published = placeholder;
        try
        {
            var lyricsTask = LoadLyricsSafeAsync(spotifyTrack, cancellationToken);
            var albumTask = LoadImageSafeAsync(spotifyTrack.AlbumArtUrl, cancellationToken);
            var artistsTask = placeholder.ArtistImageBytes.IsDefaultOrEmpty
                ? LoadArtistImagesSafeAsync(spotifyTrack.ArtistIds, cancellationToken)
                : Task.FromResult(placeholder.ArtistImageBytes);
            var detailsTask = Task.WhenAll(lyricsTask, albumTask);

            bool Publish(TrackInfo next)
            {
                next = next with
                {
                    Title = spotifyTrack.Title,
                    Artists = spotifyTrack.Artists,
                    Album = spotifyTrack.Album,
                    DurationMs = spotifyTrack.DurationMs
                };
                if (next == published
                    || Interlocked.CompareExchange(ref _track, next, published) != published)
                    return false;
                Interlocked.CompareExchange(ref _loadingTrack, next, published);
                published = next;
                TrackCache.SaveIfChanged(next);
                return true;
            }

            void PublishArtists(ImmutableArray<ImmutableArray<byte>> artists)
            {
                if (!artists.IsDefaultOrEmpty && !artists.Equals(published.ArtistImageBytes))
                    Publish(published with { ArtistImageBytes = artists });
            }

            void PublishDetails(ImmutableArray<LyricLine> lyrics, ImmutableArray<byte> album)
            {
                var loadedLyrics = published.Lyrics.IsDefaultOrEmpty && !lyrics.IsDefaultOrEmpty;
                var didPublish = Publish(published with
                {
                    AlbumArtBytes = album.IsEmpty ? published.AlbumArtBytes : album,
                    Lyrics = lyrics.IsEmpty ? published.Lyrics : lyrics
                });
                Interlocked.CompareExchange(ref _loadingTrack, null, published);
                if (loadedLyrics && didPublish)
                {
                    Interlocked.Increment(ref _positionSyncVersion);
                    _pollWake.Release();
                }
            }

            if (await Task.WhenAny(artistsTask, detailsTask) == artistsTask)
            {
                PublishArtists(await artistsTask);
                await detailsTask;
                PublishDetails(await lyricsTask, await albumTask);
            }
            else
            {
                await detailsTask;
                PublishDetails(await lyricsTask, await albumTask);
                PublishArtists(await artistsTask);
            }
            Interlocked.CompareExchange(ref _loadingTrack, null, published);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[track] load failed: {Friendly(exception)}");
            Interlocked.CompareExchange(ref _loadingTrack, null, published);
        }
    }

    private void ClearTrack()
    {
        CancelTrackLoad();
        Volatile.Write(ref _track, null);
    }

    internal static long StabilizePosition(
        PlaybackSnapshot previous,
        string trackId,
        long? reportedPosition,
        long reportedAt,
        bool isPlaying,
        bool samePlaybackState)
    {
        var sameTrack = previous.Track?.Id == trackId;
        var elapsed = sameTrack && previous.IsPlaying
            ? Math.Max(0L, (long)Stopwatch.GetElapsedTime(previous.ReportedAtTimestamp, reportedAt).TotalMilliseconds)
            : 0L;
        var predicted = Math.Max(0L, previous.ReportedPositionMs + elapsed);
        if (reportedPosition is null)
            return sameTrack ? predicted : 0L;

        var reported = Math.Max(0L, reportedPosition.Value);
        if (!samePlaybackState || !sameTrack || !isPlaying || !previous.IsPlaying)
            return reported;

        return reported < predicted ? predicted : reported;
    }

    private void CancelTrackLoad()
    {
        Volatile.Write(ref _loadingTrack, null);
        _trackLoad?.Cancel();
        _trackLoad?.Dispose();
        _trackLoad = null;
    }

    private async Task<HttpResponseMessage> SendCurrentTrackAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, CurrentlyPlayingUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _tokens!.AccessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private async Task<TokenState> RefreshOrAuthorizeAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await RefreshAsync(cancellationToken);
        }
        catch (TokenEndpointException exception) when (
            exception.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
        {
            return await AuthorizeAsync(cancellationToken);
        }
    }

    private async Task<TokenState> AuthorizeAsync(CancellationToken cancellationToken)
    {
        SetStatus("正在等待 Spotify 授权…");
        var state = Base64Url(RandomNumberGenerator.GetBytes(32));
        var url = "https://accounts.spotify.com/authorize"
            + $"?client_id={Uri.EscapeDataString(_credentials.ClientId)}"
            + "&response_type=code"
            + $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}"
            + $"&scope={Uri.EscapeDataString(Scope)}"
            + $"&state={Uri.EscapeDataString(state)}";

        var listener = new TcpListener(IPAddress.Loopback, 43821);
        listener.Start();
        try
        {
            OpenBrowser(url);
            while (true)
            {
                using var client = await listener.AcceptTcpClientAsync(cancellationToken);
                await using var stream = client.GetStream();
                using var reader = new StreamReader(
                    stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                var requestLine = await reader.ReadLineAsync(cancellationToken);
                for (var i = 0; i < 100; i++)
                {
                    var header = await reader.ReadLineAsync(cancellationToken);
                    if (string.IsNullOrEmpty(header))
                        break;
                }

                if (!TryReadCallback(requestLine, out var query))
                {
                    await ReplyAsync(stream, 404, "Not Found", "请返回 Spotify 授权页重试。", cancellationToken);
                    continue;
                }

                if (!query.TryGetValue("state", out var returnedState)
                    || !FixedTimeEquals(state, returnedState))
                {
                    await ReplyAsync(stream, 400, "Bad Request", "Spotify 授权 state 校验失败。", cancellationToken);
                    throw new InvalidOperationException("Spotify 授权 state 校验失败");
                }

                if (query.TryGetValue("error", out var error))
                {
                    await ReplyAsync(stream, 400, "Bad Request", "Spotify 授权已取消，可以关闭此页。", cancellationToken);
                    throw new InvalidOperationException($"Spotify 授权失败：{error}");
                }

                if (!query.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code))
                {
                    await ReplyAsync(stream, 400, "Bad Request", "Spotify 未返回授权码。", cancellationToken);
                    throw new InvalidOperationException("Spotify 未返回授权码");
                }

                await ReplyAsync(stream, 200, "OK", "Spotify 授权成功，可以关闭此页。", cancellationToken);
                var tokens = await RequestTokenAsync(
                    new Dictionary<string, string>
                    {
                        ["grant_type"] = "authorization_code",
                        ["code"] = code,
                        ["redirect_uri"] = RedirectUri
                    },
                    null,
                    cancellationToken);
                await SaveTokenAsync(tokens, cancellationToken);
                return tokens;
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    private async Task<TokenState> RefreshAsync(CancellationToken cancellationToken)
    {
        var tokens = await RequestTokenAsync(
            new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = _tokens!.RefreshToken
            },
            _tokens.RefreshToken,
            cancellationToken);
        await SaveTokenAsync(tokens, cancellationToken);
        return tokens;
    }

    private async Task<TokenState> RequestTokenAsync(
        IReadOnlyDictionary<string, string> fields,
        string? existingRefreshToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://accounts.spotify.com/api/token");
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes(
            $"{_credentials.ClientId}:{_credentials.ClientSecret}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        request.Content = new FormUrlEncodedContent(fields);

        using var response = await Http.SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var description = TryGetJsonString(text, "error_description");
            throw new TokenEndpointException(
                response.StatusCode,
                $"Spotify token HTTP {(int)response.StatusCode}"
                    + (description is null ? string.Empty : $"：{description}"));
        }

        using var json = JsonDocument.Parse(text);
        var root = json.RootElement;
        var accessToken = GetString(root, "access_token")
            ?? throw new InvalidOperationException("Spotify token 响应缺少 access_token");
        var refreshToken = GetString(root, "refresh_token") ?? existingRefreshToken;
        if (string.IsNullOrWhiteSpace(refreshToken))
            throw new InvalidOperationException("Spotify token 响应缺少 refresh_token");
        var expiresIn = Math.Max(30, GetInt64(root, "expires_in"));
        return new TokenState(
            accessToken,
            refreshToken,
            DateTimeOffset.UtcNow.AddSeconds(expiresIn),
            _credentials.ClientId);
    }

    private async Task<TokenState?> LoadTokenAsync(CancellationToken cancellationToken)
    {
        var path = TokenPath();
        if (!File.Exists(path))
            return null;

        try
        {
            Secure(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            var json = await File.ReadAllTextAsync(path, cancellationToken);
            var token = JsonSerializer.Deserialize<TokenState>(json);
            return token is { AccessToken.Length: > 0, RefreshToken.Length: > 0 }
                   && (string.IsNullOrEmpty(token.ClientId)
                       || token.ClientId == _credentials.ClientId)
                ? token
                : null;
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return null;
        }
    }

    private static async Task SaveTokenAsync(TokenState tokens, CancellationToken cancellationToken)
    {
        var path = TokenPath();
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        Secure(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                Secure(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                await JsonSerializer.SerializeAsync(stream, tokens, cancellationToken: cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            File.Move(temporary, path, overwrite: true);
            Secure(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static SpotifyTrack? ParseTrack(JsonElement item)
    {
        var title = GetString(item, "name");
        if (string.IsNullOrWhiteSpace(title))
            return null;

        var duration = GetInt64(item, "duration_ms");
        var artists = Names(item, "artists");
        var artistIds = Values(item, "artists", "id");
        var albumName = string.Empty;
        var albumArtists = ImmutableArray<string>.Empty;
        string? albumArtUrl = null;
        if (item.TryGetProperty("album", out var album) && album.ValueKind == JsonValueKind.Object)
        {
            albumName = GetString(album, "name") ?? string.Empty;
            albumArtists = Names(album, "artists");
            albumArtUrl = SmallestImage(album);
        }

        string? isrc = null;
        if (item.TryGetProperty("external_ids", out var externalIds)
            && externalIds.ValueKind == JsonValueKind.Object)
            isrc = GetString(externalIds, "isrc");

        var id = GetString(item, "id") ?? GetString(item, "uri")
            ?? $"local:{title}:{duration}:{string.Join(',', artists)}";
        return new SpotifyTrack(
            id, title, artists, artistIds, albumArtists, albumName, duration, isrc, albumArtUrl);
    }

    private static ImmutableArray<string> Names(JsonElement parent, string property)
        => Values(parent, property, "name");

    private static ImmutableArray<string> Values(JsonElement parent, string property, string field)
    {
        if (!parent.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array)
            return ImmutableArray<string>.Empty;
        return array.EnumerateArray()
            .Select(value => GetString(value, field))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToImmutableArray();
    }

    private static string? SmallestImage(JsonElement parent)
    {
        if (!parent.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Array)
            return null;

        string? smallest = null;
        var smallestArea = long.MaxValue;
        foreach (var image in images.EnumerateArray())
        {
            var url = GetString(image, "url");
            if (url is null)
                continue;
            var width = GetInt64(image, "width");
            var height = GetInt64(image, "height");
            var area = width > 0 && height > 0 ? width * height : long.MaxValue - 1;
            if (area < smallestArea)
            {
                smallest = url;
                smallestArea = area;
            }
        }
        return smallest;
    }

    private static async Task<ImmutableArray<LyricLine>> LoadLyricsSafeAsync(
        SpotifyTrack track,
        CancellationToken cancellationToken)
    {
        try
        {
            return await LyricsProvider.LoadAsync(track, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return ImmutableArray<LyricLine>.Empty;
        }
    }

    private async Task<ImmutableArray<ImmutableArray<byte>>> LoadArtistImagesSafeAsync(
        ImmutableArray<string> artistIds,
        CancellationToken cancellationToken)
    {
        if (artistIds.IsDefaultOrEmpty)
            return [];
        var images = await Task.WhenAll(artistIds.Select(id =>
            LoadArtistImageSafeAsync(id, cancellationToken)));
        return images.Where(image => !image.IsDefaultOrEmpty).ToImmutableArray();
    }

    private async Task<ImmutableArray<byte>> LoadArtistImageSafeAsync(
        string artistId,
        CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"https://api.spotify.com/v1/artists/{Uri.EscapeDataString(artistId)}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _tokens!.AccessToken);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await Http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
                return ImmutableArray<byte>.Empty;

            await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var json = await JsonDocument.ParseAsync(body, cancellationToken: timeout.Token);
            return await LoadImageSafeAsync(SmallestImage(json.RootElement), timeout.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return ImmutableArray<byte>.Empty;
        }
    }

    private static async Task<ImmutableArray<byte>> LoadImageSafeAsync(
        string? url,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
            return ImmutableArray<byte>.Empty;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            return (await Http.GetByteArrayAsync(uri, timeout.Token)).ToImmutableArray();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return ImmutableArray<byte>.Empty;
        }
    }

    private static TimeSpan RetryAfter(HttpResponseMessage response)
    {
        var retry = response.Headers.RetryAfter?.Delta
            ?? response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow
            ?? TimeSpan.FromSeconds(2);
        return retry <= TimeSpan.Zero ? TimeSpan.FromSeconds(1) : retry;
    }

    private static bool TryReadCallback(string? requestLine, out Dictionary<string, string> query)
    {
        query = new Dictionary<string, string>(StringComparer.Ordinal);
        var parts = requestLine?.Split(' ', 3);
        if (parts is not { Length: >= 2 }
            || !Uri.TryCreate(new Uri("http://127.0.0.1"), parts[1], out var uri)
            || uri.AbsolutePath != "/callback")
            return false;

        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            var key = Decode(separator < 0 ? pair : pair[..separator]);
            var value = Decode(separator < 0 ? string.Empty : pair[(separator + 1)..]);
            query[key] = value;
        }
        return true;
    }

    private static async Task ReplyAsync(
        NetworkStream stream,
        int code,
        string reason,
        string message,
        CancellationToken cancellationToken)
    {
        var body = Encoding.UTF8.GetBytes(
            $"<!doctype html><meta charset=utf-8><title>Lyricify Island</title><p>{WebUtility.HtmlEncode(message)}</p>");
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {code} {reason}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(body, cancellationToken);
    }

    private static void OpenBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception first)
        {
            try
            {
                Process.Start(new ProcessStartInfo("xdg-open", url) { UseShellExecute = false });
            }
            catch
            {
                throw new InvalidOperationException("无法打开浏览器完成 Spotify 授权", first);
            }
        }
    }

    private void SetStatus(string status)
    {
        var snapshot = _store.Snapshot;
        _store.Update(snapshot with { Status = status });
    }

    private static string TokenPath()
    {
        var stateHome = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        if (string.IsNullOrWhiteSpace(stateHome) || !Path.IsPathRooted(stateHome))
            stateHome = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state");
        return Path.Combine(stateHome, "lyricify-island", "spotify-token.json");
    }

    private static void Secure(string path, UnixFileMode mode)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, mode);
    }

    private static string Decode(string value) =>
        Uri.UnescapeDataString(value.Replace('+', ' '));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool FixedTimeEquals(string expected, string actual)
    {
        var left = Encoding.UTF8.GetBytes(expected);
        var right = Encoding.UTF8.GetBytes(actual);
        return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
    }

    private static string? GetString(JsonElement parent, string property) =>
        parent.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long GetInt64(JsonElement parent, string property) =>
        parent.TryGetProperty(property, out var value) && value.TryGetInt64(out var number)
            ? number
            : 0;

    private static long? GetNullableInt64(JsonElement parent, string property) =>
        parent.TryGetProperty(property, out var value) && value.TryGetInt64(out var number)
            ? number
            : null;

    private static bool GetBoolean(JsonElement parent, string property) =>
        parent.TryGetProperty(property, out var value)
        && value.ValueKind is JsonValueKind.True or JsonValueKind.False
        && value.GetBoolean();

    private static string? TryGetJsonString(string json, string property)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return GetString(document.RootElement, property);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Friendly(Exception exception)
    {
        var message = exception.Message.Split('\n', '\r')[0].Trim();
        return message.Length <= 160 ? message : message[..160];
    }

    private sealed record Credentials(string ClientId, string ClientSecret);

    private sealed record TokenState(
        string AccessToken,
        string RefreshToken,
        DateTimeOffset ExpiresAt,
        string? ClientId = null);

    private sealed class TokenEndpointException(HttpStatusCode statusCode, string message)
        : Exception(message)
    {
        public HttpStatusCode StatusCode { get; } = statusCode;
    }
}
