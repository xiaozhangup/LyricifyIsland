using System.Collections.Immutable;
using Lyricify.Lyrics.Helpers;
using Lyricify.Lyrics.Helpers.Optimization;
using Lyricify.Lyrics.Models;
using Lyricify.Lyrics.Searchers;
using Lyricify.Lyrics.Searchers.Helpers;
using KugouApi = Lyricify.Lyrics.Providers.Web.Kugou.Api;
using LrclibApi = Lyricify.Lyrics.Providers.Web.LRCLIB.Api;
using NeteaseApi = Lyricify.Lyrics.Providers.Web.Netease.Api;

namespace LyricifyIsland;

internal static class LyricsProvider
{
    public static async Task<ImmutableArray<LyricLine>> LoadAsync(SpotifyTrack track, CancellationToken ct)
    {
        var lineFallback = ImmutableArray<LyricLine>.Empty;
        var metadata = new TrackMultiArtistMetadata
        {
            Title = track.Title,
            Artists = track.Artists.ToList(),
            Album = track.Album,
            AlbumArtists = track.AlbumArtists.ToList(),
            DurationMs = (int)Math.Clamp(track.DurationMs, 0, int.MaxValue),
            Isrc = track.Isrc,
        };

        try
        {
            ct.ThrowIfCancellationRequested();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var providerCt = timeout.Token;
            if (await SearchHelper.Search(metadata, Searchers.Netease, CompareHelper.MatchType.Medium)
                    .WaitAsync(providerCt) is NeteaseSearchResult match)
            {
                var response = await new NeteaseApi().GetLyricNew(match.Id).WaitAsync(providerCt);
                var yrc = response?.Yrc?.Lyric;
                var raw = yrc;
                if (string.IsNullOrWhiteSpace(raw))
                {
                    raw = response?.Lrc?.Lyric;
                }

                if (!string.IsNullOrWhiteSpace(raw))
                {
                    var translations = ParseTranslation(response?.Ytlrc?.Lyric, response?.Tlyric?.Lyric);
                    var type = string.IsNullOrWhiteSpace(yrc) ? LyricsRawTypes.Lrc : LyricsRawTypes.Yrc;
                    var result = Convert(ParseHelper.ParseLyrics(raw, type), metadata, track.DurationMs, translations);
                    if (!result.IsEmpty)
                    {
                        if (type == LyricsRawTypes.Yrc)
                        {
                            Console.Error.WriteLine("[lyrics] provider=netease-yrc");
                            return result;
                        }
                        lineFallback = result;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[lyrics] netease failed: {exception.Message}");
        }

        try
        {
            ct.ThrowIfCancellationRequested();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var providerCt = timeout.Token;
            if (await SearchHelper.Search(metadata, Searchers.Kugou, CompareHelper.MatchType.High)
                    .WaitAsync(providerCt) is KugouSearchResult match)
            {
                var response = await new KugouApi()
                    .GetSearchLyrics(hash: match.Hash, duration: metadata.DurationMs)
                    .WaitAsync(providerCt);
                var candidate = response?.Candidates?
                    .MinBy(item => Math.Abs((long)item.Duration - track.DurationMs));
                if (candidate is not null)
                {
                    var raw = await Lyricify.Lyrics.Decrypter.Krc.Helper
                        .GetLyricsAsync(candidate.Id, candidate.AccessKey)
                        .WaitAsync(providerCt);
                    if (!string.IsNullOrWhiteSpace(raw))
                    {
                        var result = Convert(
                            ParseHelper.ParseLyrics(raw, LyricsRawTypes.Krc),
                            metadata,
                            track.DurationMs);
                        if (!result.IsEmpty)
                        {
                            Console.Error.WriteLine("[lyrics] provider=kugou-krc");
                            return result;
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[lyrics] kugou failed: {exception.Message}");
        }

        if (!lineFallback.IsEmpty)
        {
            Console.Error.WriteLine("[lyrics] provider=netease-lrc");
            return lineFallback;
        }

        try
        {
            ct.ThrowIfCancellationRequested();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var providerCt = timeout.Token;
            var response = await new LrclibApi().Get(
                    track.Title,
                    string.Join(", ", track.Artists),
                    track.Album,
                    track.DurationMs / 1000d)
                .WaitAsync(providerCt);
            if (!string.IsNullOrWhiteSpace(response?.SyncedLyrics))
            {
                var result = Convert(
                    ParseHelper.ParseLyrics(response.SyncedLyrics, LyricsRawTypes.Lrc),
                    metadata,
                    track.DurationMs);
                if (!result.IsEmpty)
                    Console.Error.WriteLine("[lyrics] provider=lrclib-lrc");
                return result;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[lyrics] lrclib failed: {exception.Message}");
        }

        ct.ThrowIfCancellationRequested();
        return [];
    }

    private static List<ILineInfo>? ParseTranslation(string? preferred, string? fallback)
    {
        if (!string.IsNullOrWhiteSpace(preferred))
        {
            var lines = ParseHelper.ParseLyrics(preferred, LyricsRawTypes.Lrc)?.Lines;
            if (lines is { Count: > 0 }) return lines;
        }

        return string.IsNullOrWhiteSpace(fallback)
            ? null
            : ParseHelper.ParseLyrics(fallback, LyricsRawTypes.Lrc)?.Lines;
    }

    private static ImmutableArray<LyricLine> Convert(
        LyricsData? data,
        ITrackMetadata metadata,
        long durationMs,
        List<ILineInfo>? translations = null)
    {
        if (data?.Lines is not { Count: > 0 }) return [];

        var lines = data.Lines
            .Where(line => line.StartTime.HasValue && !string.IsNullOrWhiteSpace(line.Text))
            .OrderBy(line => line.StartTime)
            .ToList();
        var infoLines = InfoLines.CheckInfoLines(lines, metadata);
        lines = lines.Where((_, index) => !infoLines[index]).ToList();
        if (lines.Count == 0) return [];

        var translated = translations?
            .Where(line => line.StartTime.HasValue && !string.IsNullOrWhiteSpace(line.Text))
            .OrderBy(line => line.StartTime)
            .ToList();
        var result = ImmutableArray.CreateBuilder<LyricLine>(lines.Count);

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var start = (long)line.StartTime!.Value;
            var end = line.EndTime is int ownEnd
                ? ownEnd
                : i + 1 < lines.Count
                    ? lines[i + 1].StartTime!.Value
                    : durationMs;
            var translation = (line as IFullLineInfo)?.ChineseTranslation;
            if (string.IsNullOrWhiteSpace(translation) && translated is { Count: > 0 })
            {
                var nearest = translated.MinBy(item => Math.Abs((long)item.StartTime!.Value - start));
                if (nearest is not null && Math.Abs((long)nearest.StartTime!.Value - start) <= 1200)
                    translation = nearest.Text;
            }

            var syllables = line is SyllableLineInfo syllableLine
                ? ConvertSyllables(syllableLine)
                : ImmutableArray<LyricSyllable>.Empty;
            var text = syllables.IsEmpty
                ? line.Text.Trim()
                : string.Concat(syllables.Select(syllable => syllable.Text));

            result.Add(new LyricLine(
                text,
                string.IsNullOrWhiteSpace(translation) ? null : translation.Trim(),
                start,
                Math.Max(start, end),
                syllables));
        }

        return result.MoveToImmutable();
    }

    private static ImmutableArray<LyricSyllable> ConvertSyllables(SyllableLineInfo line)
    {
        var first = line.Syllables.FindIndex(syllable => !string.IsNullOrWhiteSpace(syllable.Text));
        if (first < 0) return [];
        var last = line.Syllables.FindLastIndex(syllable => !string.IsNullOrWhiteSpace(syllable.Text));
        var result = ImmutableArray.CreateBuilder<LyricSyllable>(last - first + 1);

        for (var i = first; i <= last; i++)
        {
            var syllable = line.Syllables[i];
            var text = syllable.Text;
            if (i == first) text = text.TrimStart();
            if (i == last) text = text.TrimEnd();
            if (text.Length > 0)
                result.Add(new LyricSyllable(text, syllable.StartTime, syllable.EndTime));
        }

        return result.MoveToImmutable();
    }
}
