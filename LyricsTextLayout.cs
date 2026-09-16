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
    }

    public List<Glyph> Glyphs { get; } = [];
    public float Height { get; private set; }
    public float Width { get; private set; }
    public float FontSize { get; }
    public SKRect InkBounds { get; }

    public LyricsTextLayout(string text, float size, float maxWidth, LyricsFonts fonts,
        bool bold = true, LyricLine? timing = null, int maxRows = int.MaxValue)
    {
        FontSize = size;
        maxWidth = Math.Max(size, maxWidth);
        var x = 0f;
        var row = 0;
        var textOffset = 0;
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
            foreach (var part in parts)
            {
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
                        x, size + row * size * 1.28f, part.Width, part.Offset));
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
        if (timing is not null) AssignTiming(timing, text);
    }

    private void AssignTiming(LyricLine line, string text)
    {
        var syllables = line.Syllables;
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
    }

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
