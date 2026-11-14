using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SkiaSharp;

namespace LyricifyIsland;

// Layout is cached per song/width. Drawing never measures or rebuilds glyph paths per frame.
internal sealed class LyricsTextLayout : IDisposable
{
    internal sealed record Glyph(SKPath Path, float X, float Baseline, float Width, int TextOffset)
    {
        public double StartMs { get; set; }
        public double EndMs { get; set; }
        public double SyllableDurationMs { get; set; }
        public float EffectOffset { get; set; }
        public float EffectWidth { get; set; } = Width;
        public int WordGroup { get; init; } = -1;
    }

    public List<Glyph> Glyphs { get; } = [];
    public float Height { get; private set; }
    public float Width { get; private set; }
    public float FontSize { get; }
    public SKRect InkBounds { get; }

    public LyricsTextLayout(string text, float size, float maxWidth, LyricsFonts fonts,
        bool bold = true, LyricLine? timing = null, int maxRows = int.MaxValue, bool wordEffects = false)
    {
        FontSize = size;
        maxWidth = Math.Max(size, maxWidth);
        var x = 0f;
        var row = 0;
        var textOffset = 0;
        var nextWordGroup = 0;
        var japanese = text.EnumerateRunes().Any(r => r.Value is >= 0x3040 and <= 0x30ff);
        // Keep Latin words intact; CJK, long words and emoji may wrap at a grapheme boundary.
        foreach (Match token in Regex.Matches(text, @"[^\S\r\n]+|[\r\n]+|[\p{IsCJKUnifiedIdeographs}\p{IsHiragana}\p{IsKatakana}]|[^\s\p{IsCJKUnifiedIdeographs}\p{IsHiragana}\p{IsKatakana}]+"))
        {
            var parts = new List<(string Text, SKFont Font, float Width, int Offset)>();
            var elements = StringInfo.GetTextElementEnumerator(token.Value);
            var tokenWidth = 0f;
            while (elements.MoveNext())
            {
                var element = elements.GetTextElement();
                var font = fonts.Get(element, size, bold, japanese);
                var advance = font.MeasureText(element);
                parts.Add((element, font, advance, textOffset));
                tokenWidth += advance;
                textOffset += element.Length;
            }
            if (token.Value.Contains('\n') || token.Value.Contains('\r'))
            {
                row++;
                x = 0;
                if (row >= maxRows) break;
                continue;
            }
            if (!string.IsNullOrWhiteSpace(token.Value) && x > 0 && x + tokenWidth > maxWidth && tokenWidth <= maxWidth)
            {
                row++;
                x = 0;
            }
            if (row >= maxRows) break;
            var partGroups = new int[parts.Count];
            Array.Fill(partGroups, -1);
            if (wordEffects)
            {
                for (var i = 0; i < parts.Count;)
                {
                    if (!IsLatinWordElement(parts[i].Text))
                    {
                        i++;
                        continue;
                    }
                    var end = i + 1;
                    var hasLatin = ContainsLatinLetter(parts[i].Text);
                    while (end < parts.Count && IsLatinWordElement(parts[end].Text))
                    {
                        hasLatin |= ContainsLatinLetter(parts[end].Text);
                        end++;
                    }
                    if (hasLatin)
                    {
                        var group = nextWordGroup++;
                        for (var j = i; j < end; j++)
                            partGroups[j] = group;
                    }
                    i = end;
                }
            }
            for (var partIndex = 0; partIndex < parts.Count; partIndex++)
            {
                var part = parts[partIndex];
                var whitespace = string.IsNullOrWhiteSpace(part.Text);
                if (x + part.Width > maxWidth && x > 0 && !whitespace)
                {
                    row++;
                    x = 0;
                }
                if (row >= maxRows) break;
                if (whitespace && x == 0) continue;
                if (!whitespace)
                    Glyphs.Add(new Glyph(part.Font.GetTextPath(part.Text, SKPoint.Empty),
                        x, size + row * size * 1.28f, part.Width, part.Offset)
                    { WordGroup = partGroups[partIndex] });
                x += part.Width;
                Width = Math.Max(Width, Math.Min(maxWidth, x));
            }
        }
        Height = (Math.Min(row, maxRows - 1) + 1) * size * 1.28f;
        if (row >= maxRows && Glyphs.Count > 0)
        {
            var font = fonts.Get("…", size, bold);
            var ellipsisWidth = font.MeasureText("…");
            var baseline = size + (maxRows - 1) * size * 1.28f;
            while (Glyphs.Count > 0 && Glyphs[^1].Baseline == baseline
                && Glyphs[^1].X + Glyphs[^1].Width + ellipsisWidth > maxWidth)
            {
                Glyphs[^1].Path.Dispose();
                Glyphs.RemoveAt(Glyphs.Count - 1);
            }
            var end = Glyphs.Count > 0 && Glyphs[^1].Baseline == baseline
                ? Glyphs[^1].X + Glyphs[^1].Width : 0;
            Glyphs.Add(new Glyph(font.GetTextPath("…", SKPoint.Empty), end, baseline, ellipsisWidth, text.Length));
        }
        var inkBounds = SKRect.Empty;
        foreach (var glyph in Glyphs)
        {
            var bounds = glyph.Path.TightBounds;
            if (bounds.IsEmpty) continue;
            bounds.Offset(glyph.X, glyph.Baseline);
            inkBounds = inkBounds.IsEmpty ? bounds : new SKRect(
                Math.Min(inkBounds.Left, bounds.Left), Math.Min(inkBounds.Top, bounds.Top),
                Math.Max(inkBounds.Right, bounds.Right), Math.Max(inkBounds.Bottom, bounds.Bottom));
        }
        InkBounds = inkBounds;
        if (timing is not null) AssignTiming(timing, text, wordEffects);
    }

    private void AssignTiming(LyricLine line, string text, bool wordEffects)
    {
        var syllables = EffectiveSyllables(line, wordEffects);
        var hasSyllables = !syllables.IsDefaultOrEmpty
            && string.Concat(syllables.Select(s => s.Text)) == text;
        if (!hasSyllables)
        {
            // Line-synced lyrics light the entire line; do not invent word timestamps.
            foreach (var glyph in Glyphs)
            {
                glyph.StartMs = line.StartMs;
                glyph.EndMs = line.StartMs;
            }
            return;
        }
        var first = 0;
        var offset = 0;
        foreach (var syllable in syllables)
        {
            var endOffset = offset + syllable.Text.Length;
            var end = first;
            while (end < Glyphs.Count && Glyphs[end].TextOffset < endOffset) end++;
            var width = Glyphs.Skip(first).Take(end - first).Sum(g => g.Width);
            var duration = Math.Max(1, syllable.EndMs - syllable.StartMs);
            var advance = 0f;
            for (var i = first; i < end; i++)
            {
                var glyph = Glyphs[i];
                glyph.StartMs = syllable.StartMs + duration * advance / Math.Max(1, width);
                advance += glyph.Width;
                glyph.EndMs = syllable.StartMs + duration * advance / Math.Max(1, width);
                glyph.SyllableDurationMs = duration;
            }
            first = end;
            offset = endOffset;
        }
        if (!wordEffects)
            return;

        for (var start = 0; start < Glyphs.Count;)
        {
            var group = Glyphs[start].WordGroup;
            if (group < 0)
            {
                start++;
                continue;
            }
            var baseline = Glyphs[start].Baseline;
            var end = start + 1;
            while (end < Glyphs.Count && Glyphs[end].WordGroup == group && Glyphs[end].Baseline == baseline)
                end++;
            var startMs = Glyphs[start].StartMs;
            var endMs = Glyphs[end - 1].EndMs;
            var durationMs = Math.Max(1, endMs - startMs);
            var startX = Glyphs[start].X;
            var width = Math.Max(1f, Glyphs[end - 1].X + Glyphs[end - 1].Width - startX);
            for (var i = start; i < end; i++)
            {
                var glyph = Glyphs[i];
                glyph.StartMs = startMs;
                glyph.EndMs = endMs;
                glyph.SyllableDurationMs = durationMs;
                glyph.EffectOffset = glyph.X - startX;
                glyph.EffectWidth = width;
            }
            start = end;
        }
    }

    internal static ImmutableArray<LyricSyllable> EffectiveSyllables(LyricLine line, bool englishWordEffects)
    {
        var syllables = line.Syllables;
        if (!englishWordEffects || syllables.IsDefaultOrEmpty || syllables.Length <= 1)
            return syllables;

        List<LyricSyllable>? merged = null;
        var index = 0;
        while (index < syllables.Length)
        {
            var current = syllables[index];
            var end = index + 1;
            while (end < syllables.Length
                   && EndsWithLatinWordRune(current.Text)
                   && StartsWithLatinWordRune(syllables[end].Text)
                   && (ContainsLatinLetter(current.Text) || ContainsLatinLetter(syllables[end].Text)))
            {
                merged ??= syllables.Take(index).ToList();
                var next = syllables[end];
                current = new LyricSyllable(
                    current.Text + next.Text,
                    current.StartMs,
                    Math.Max(current.EndMs, next.EndMs));
                end++;
            }
            merged?.Add(current);
            index = end;
        }
        return merged is null ? syllables : merged.ToImmutableArray();
    }

    internal static bool IsLatinWordElement(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty)
            return false;
        foreach (var rune in text.EnumerateRunes())
        {
            if (!IsLatinWordRune(rune))
                return false;
        }
        return true;
    }

    internal static bool ContainsLatinLetter(ReadOnlySpan<char> text)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            if (IsLatinLetter(rune))
                return true;
        }
        return false;
    }

    internal static bool IsLatinWordRune(Rune rune) =>
        !Rune.IsWhiteSpace(rune) && !IsCjk(rune);

    internal static bool IsLatinLetter(Rune rune) =>
        rune.Value is (>= 'A' and <= 'Z')
            or (>= 'a' and <= 'z')
            or (>= 0x00C0 and <= 0x024F and not 0x00D7 and not 0x00F7)
            or (>= 0x1E00 and <= 0x1EFF);

    internal static bool IsCjk(Rune rune) =>
        rune.Value is >= 0x2E80 and <= 0x9FFF
            or >= 0xAC00 and <= 0xD7AF
            or >= 0xF900 and <= 0xFAFF
            or >= 0xFE10 and <= 0xFE4F
            or >= 0xFF00 and <= 0xFFEF
            or >= 0x20000 and <= 0x323AF;

    private static bool StartsWithLatinWordRune(ReadOnlySpan<char> text) =>
        Rune.DecodeFromUtf16(text, out var rune, out _) == System.Buffers.OperationStatus.Done
        && IsLatinWordRune(rune);

    private static bool EndsWithLatinWordRune(ReadOnlySpan<char> text) =>
        Rune.DecodeLastFromUtf16(text, out var rune, out _) == System.Buffers.OperationStatus.Done
        && IsLatinWordRune(rune);

    public void Draw(SKCanvas canvas, float x, float y, SKPaint paint)
    {
        foreach (var glyph in Glyphs)
        {
            canvas.Save();
            canvas.Translate(x + glyph.X, y + glyph.Baseline);
            canvas.DrawPath(glyph.Path, paint);
            canvas.Restore();
        }
    }

    public void Dispose()
    {
        foreach (var glyph in Glyphs) glyph.Path.Dispose();
        Glyphs.Clear();
    }
}

internal sealed class LyricsFonts : IDisposable
{
    private readonly Dictionary<(string, float, bool), SKFont> _fonts = [];
    private readonly Dictionary<(string, bool), SKTypeface> _faces = [];

    public SKFont Get(string text, float size, bool bold, bool japanese = false)
    {
        size = MathF.Round(size * 2) / 2;
        var rune = text.EnumerateRunes().FirstOrDefault();
        var family = rune.Value >= 0x2e80
            ? japanese ? "Noto Sans CJK JP" : "Noto Sans CJK SC"
            : "Montserrat";
        var key = (family, size, bold);
        if (_fonts.TryGetValue(key, out var font)) return font;
        var faceKey = (family, bold);
        if (!_faces.TryGetValue(faceKey, out var face))
            _faces[faceKey] = face = SKTypeface.FromFamilyName(family, bold ? SKFontStyle.Bold : SKFontStyle.Normal);
        font = new SKFont(face, size)
        {
            Edging = SKFontEdging.Antialias,
            Hinting = SKFontHinting.None,
            Subpixel = true,
            LinearMetrics = true
        };
        _fonts[key] = font;
        return font;
    }

    public void Dispose()
    {
        foreach (var font in _fonts.Values) font.Dispose();
        foreach (var face in _faces.Values) face.Dispose();
        _fonts.Clear();
        _faces.Clear();
    }
}
