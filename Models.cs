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
    ImmutableArray<LyricLine> Lyrics);

public sealed record PlaybackSnapshot(
    TrackInfo? Track,
    long ReportedPositionMs,
    long ReportedAtTimestamp,
    bool IsPlaying,
    string Status);

internal sealed record SpotifyTrack(
    string Id,
    string Title,
    ImmutableArray<string> Artists,
    ImmutableArray<string> AlbumArtists,
    string Album,
    long DurationMs,
    string? Isrc,
    string? AlbumArtUrl);

public sealed class PlaybackStore
{
    public const string MissingSpotifyCredentialsStatus = "请到设置内配置 Spotify 参数";

    private PlaybackSnapshot _snapshot = new(null, 0, System.Diagnostics.Stopwatch.GetTimestamp(), false, "正在连接 Spotify…");

    public PlaybackSnapshot Snapshot => Volatile.Read(ref _snapshot);

    public void Update(PlaybackSnapshot snapshot) => Volatile.Write(ref _snapshot, snapshot);
}
