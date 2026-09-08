using System.Collections.Immutable;

namespace LyricifyIsland;

public sealed record LyricSyllable(string Text, long StartMs, long EndMs);

public sealed record LyricLine(
    string Text,
    string? Translation,
    long StartMs,
    long EndMs,
    ImmutableArray<LyricSyllable> Syllables);

public sealed record TrackInfo(
    string Id,
    string Title,
    ImmutableArray<string> Artists,
    string Album,
    long DurationMs,
    ImmutableArray<byte> AlbumArtBytes,
    ImmutableArray<LyricLine> Lyrics)
{
    public ImmutableArray<ImmutableArray<byte>> ArtistImageBytes { get; init; } = [];
    public string? AlbumArtUrl { get; init; }
}

public sealed record PlaybackSnapshot(
    TrackInfo? Track,
    long ReportedPositionMs,
    long ReportedAtTimestamp,
    bool IsPlaying,
    string Status,
    double PlaybackRate = 1d);

internal sealed record SourceTrack(
    string Id,
    string Title,
    ImmutableArray<string> Artists,
    ImmutableArray<string> ArtistIds,
    ImmutableArray<string> AlbumArtists,
    string Album,
    long DurationMs,
    string? Isrc,
    string? AlbumArtUrl);

internal interface IPlaybackSource
{
    Task RunAsync(CancellationToken cancellationToken);
    void SetLyricsSource(LyricsSourcePreference source);
    bool RequestCurrentTrackRefresh();
}

public sealed class PlaybackStore
{
    public const string MissingSpotifyCredentialsStatus = "请到设置内配置 Spotify 参数";
    public const string NoActiveSpotifyPlaybackStatus = "Spotify 当前没有播放";
    public const string NoActiveMprisPlaybackStatus = "MPRIS 当前没有播放";

    internal static bool IsNoActivePlayback(string status) =>
        status is NoActiveSpotifyPlaybackStatus or NoActiveMprisPlaybackStatus;

    internal static bool RequiresAttention(string status) =>
        status == MissingSpotifyCredentialsStatus
        || status.StartsWith("正在连接 Spotify", StringComparison.Ordinal)
        || status.StartsWith("正在等待 Spotify 授权", StringComparison.Ordinal)
        || status.StartsWith("正在连接 MPRIS", StringComparison.Ordinal)
        || !IsNoActivePlayback(status)
            && (status.StartsWith("Spotify ", StringComparison.Ordinal)
                || status.StartsWith("MPRIS ", StringComparison.Ordinal));

    private PlaybackSnapshot _snapshot = new(
        null, 0, System.Diagnostics.Stopwatch.GetTimestamp(), false, "正在连接播放信息源…");

    public PlaybackSnapshot Snapshot => Volatile.Read(ref _snapshot);

    public void Update(PlaybackSnapshot snapshot) => Volatile.Write(ref _snapshot, snapshot);
}
