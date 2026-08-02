using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Threading;
using SkiaSharp;

namespace LyricifyIsland;

public sealed class OverlayWindow : Window
{
    private const double BaseHeight = 116d;
    private readonly IslandControl _island;
    private IslandSettings _settings;

    internal OverlayWindow(PlaybackStore store, IslandSettings settings)
    {
        _settings = SettingsStore.Normalize(settings);
        Width = 960;
        Height = CalculateLogicalHeight(_settings.ScalePercent);
        Background = Brushes.Transparent;
        WindowDecorations = Avalonia.Controls.WindowDecorations.None;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        CanResize = false;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        ExtendClientAreaToDecorationsHint = true;

        _island = new IslandControl(store);
        _island.SetScale(_settings.ScalePercent / 100d);
        Content = _island;

        Opened += (_, _) =>
        {
            ApplyGeometry();
            Screens.Changed += ScreensChanged;
            NativeOverlay.TryConfigure(
                this,
                Environment.GetEnvironmentVariable("LYRICIFY_CLICK_THROUGH") != "0");
        };
        Closed += (_, _) =>
        {
            Screens.Changed -= ScreensChanged;
            _island.Dispose();
        };
    }

    internal void ApplySettings(IslandSettings settings)
    {
        _settings = SettingsStore.Normalize(settings);
        Height = CalculateLogicalHeight(_settings.ScalePercent);
        _island.SetScale(_settings.ScalePercent / 100d);
        if (IsVisible)
            ApplyGeometry();
    }

    internal static double CalculateLogicalWidth(int physicalWidth, double scaling, double percent)
    {
        if (!double.IsFinite(scaling) || scaling <= 0d)
            scaling = 1d;
        return physicalWidth * SettingsStore.NormalizeWidthPercent(percent) / 100d / scaling;
    }

    internal static double CalculateLogicalHeight(double scalePercent) =>
        BaseHeight * SettingsStore.NormalizeScalePercent(scalePercent) / 100d;

    private void ScreensChanged(object? sender, EventArgs args) => ApplyGeometry();

    private void ApplyGeometry()
    {
        var screen = Screens.Primary ?? Screens.All.First();
        var area = screen.WorkingArea;
        var physicalWidth = (int)Math.Round(
            area.Width * _settings.WidthPercent / 100d);
        Width = CalculateLogicalWidth(area.Width, screen.Scaling, _settings.WidthPercent);
        var y = int.TryParse(Environment.GetEnvironmentVariable("LYRICIFY_Y"), out var configuredY)
            ? configuredY
            : 58;
        Position = new PixelPoint(
            area.X + (area.Width - physicalWidth) / 2,
            screen.Bounds.Y + (int)Math.Round(y * screen.Scaling));
    }
}

public sealed class IslandControl : Control, IDisposable
{
    private readonly PlaybackStore _store;
    private readonly IslandRenderer _renderer = new();
    private readonly DispatcherTimer _timer;
    private TrackInfo? _track;
    private int _lineIndex = int.MinValue;
    private LyricLine? _line;
    private LyricLine? _outgoing;
    private long _transitionAt;
    private double _scale = 1d;

    public IslandControl(PlaybackStore store)
    {
        _store = store;
        IsHitTestVisible = false;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(1000d / 60d), DispatcherPriority.Render,
            (_, _) => InvalidateVisual());
        _timer.Start();
    }

    public void SetScale(double scale)
    {
        _scale = Math.Clamp(scale, 0.5d, 2d);
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var snapshot = _store.Snapshot;
        var position = CurrentPosition(snapshot);
        var track = snapshot.Track;

        if (!ReferenceEquals(track, _track))
        {
            _track = track;
            _lineIndex = int.MinValue;
            _line = null;
            _outgoing = null;
        }

        var nextIndex = FindLine(track?.Lyrics, position);
        if (nextIndex != _lineIndex)
        {
            var next = nextIndex >= 0 ? track!.Lyrics[nextIndex] : null;
            if (_lineIndex != int.MinValue && _line is not null && next is not null)
            {
                _outgoing = _line;
                _transitionAt = Stopwatch.GetTimestamp();
            }
            else
            {
                _outgoing = null;
                _transitionAt = 0;
            }

            _lineIndex = nextIndex;
            _line = next;
        }

        var transitionSeconds = _transitionAt == 0
            ? 99d
            : Stopwatch.GetElapsedTime(_transitionAt).TotalSeconds;
        if (transitionSeconds > IslandRenderer.TransitionDuration)
            _outgoing = null;

        var frame = new IslandFrame(
            track,
            _line,
            _outgoing,
            position,
            snapshot.IsPlaying,
            snapshot.Status,
            transitionSeconds,
            _scale);
        context.Custom(new IslandDrawOperation(new Rect(Bounds.Size), _renderer, frame));
    }

    private static long CurrentPosition(PlaybackSnapshot snapshot)
    {
        var elapsed = snapshot.IsPlaying
            ? (long)Stopwatch.GetElapsedTime(snapshot.ReportedAtTimestamp).TotalMilliseconds
            : 0L;
        var offset = long.TryParse(Environment.GetEnvironmentVariable("LYRICIFY_OFFSET_MS"), out var value) ? value : 0L;
        return Math.Clamp(snapshot.ReportedPositionMs + elapsed + offset, 0, snapshot.Track?.DurationMs ?? long.MaxValue);
    }

    internal static int FindLine(IReadOnlyList<LyricLine>? lines, long positionMs)
    {
        if (lines is null || lines.Count == 0)
            return -1;

        var low = 0;
        var high = lines.Count - 1;
        var found = -1;
        while (low <= high)
        {
            var middle = low + (high - low) / 2;
            if (lines[middle].StartMs <= positionMs)
            {
                found = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }
        return found;
    }

    public void Dispose()
    {
        _timer.Stop();
        // Render operations may still be queued on Avalonia's compositor thread.
        // The process owns this renderer; releasing it here can race the final frame.
    }
}

internal sealed class IslandDrawOperation(Rect bounds, IslandRenderer renderer, IslandFrame frame)
    : ICustomDrawOperation
{
    public Rect Bounds { get; } = bounds;

    public void Render(ImmediateDrawingContext context)
    {
        var feature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
        if (feature is null)
            return;

        using var lease = feature.Lease();
        RendererDiagnostics.Observe(lease.GrContext is not null);
        renderer.Draw(lease.SkCanvas, (float)Bounds.Width, (float)Bounds.Height, frame);
    }

    public bool HitTest(Point p) => false;
    public bool Equals(ICustomDrawOperation? other) => false;
    public void Dispose() { }
}

internal sealed record IslandFrame(
    TrackInfo? Track,
    LyricLine? Line,
    LyricLine? Outgoing,
    long PositionMs,
    bool IsPlaying,
    string? Status,
    double TransitionSeconds,
    double Scale);

internal sealed class IslandRenderer : IDisposable
{
    public const double TransitionDuration = 0.9;
    private const float DoubleLinePillHeight = 90f;
    private const float SingleLinePillHeight = 68f;
    private const float MainSize = 32f;
    private const float TranslationSize = 21f;
    private const float CollapsedWidth = 450f;
    private const float DoubleLineOriginOffset = 2f;

    private readonly SKTypeface _latin = SKTypeface.FromFamilyName(
        "Montserrat", SKFontStyleWeight.Medium, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);
    private readonly SKTypeface _cjk = SKTypeface.FromFamilyName(
        "Noto Sans CJK SC", SKFontStyleWeight.Medium, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);
    private readonly SKTypeface _japanese = SKTypeface.FromFamilyName(
        "Noto Sans CJK JP", SKFontStyleWeight.Medium, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);
    private readonly SKBitmap? _icon;
    private TrackInfo? _albumTrack;
    private SKImage? _album;

    public IslandRenderer()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("LyricifyIsland.icon.png");
        if (stream is not null)
            _icon = SKBitmap.Decode(stream);
    }

    public void Draw(SKCanvas canvas, float width, float height, IslandFrame frame)
    {
        var visualScale = (float)Math.Clamp(frame.Scale, 0.5d, 2d);
        canvas.Save();
        canvas.Clear(SKColors.Transparent);
        canvas.Scale(visualScale);
        width /= visualScale;
        height /= visualScale;

        using var latinFont = CreateFont(_latin, MainSize);
        using var cjkFont = CreateFont(_cjk, MainSize);
        using var japaneseFont = CreateFont(_japanese, MainSize);
        using var translationFont = CreateFont(_cjk, TranslationSize);
        var mainFonts = new MainFontSet(latinFont, cjkFont, japaneseFont);

        var settledWidth = DesiredWidth(frame.Track, frame.Line, frame.Status, mainFonts, translationFont);
        var outgoingWidth = DesiredWidth(frame.Track, frame.Outgoing, frame.Status, mainFonts, translationFont);
        var pillWidth = Math.Min(width - 16f, AnimatedWidth(outgoingWidth, settledWidth, frame.TransitionSeconds));
        var pillHeight = AnimatedHeight(
            DesiredPillHeight(frame.Outgoing ?? frame.Line),
            DesiredPillHeight(frame.Line),
            frame.TransitionSeconds);
        var pillLeft = (width - pillWidth) / 2f;
        var pillTop = (height - pillHeight) / 2f - 2f;
        var pillRect = new SKRect(pillLeft, pillTop, pillLeft + pillWidth, pillTop + pillHeight);

        DrawPill(canvas, pillRect);
        DrawSides(canvas, frame, pillRect);

        var verticalInset = pillHeight < DoubleLinePillHeight ? 4f : 7f;
        var clip = new SKRect(
            pillLeft + 67f, pillTop + verticalInset,
            pillRect.Right - 66f, pillRect.Bottom - verticalInset);
        canvas.Save();
        canvas.ClipRect(clip);

        if (frame.Outgoing is not null && frame.TransitionSeconds < 0.12)
        {
            var p = Math.Clamp(frame.TransitionSeconds / 0.11, 0d, 1d);
            DrawLineBlock(canvas, frame.Outgoing, frame.PositionMs, clip, mainFonts, translationFont,
                1f - 0.25f * (float)EaseOutCubic(p), (float)(1d - EaseOutCubic(p)));
        }

        var incomingStart = frame.Outgoing is null ? 0d : 0.30d;
        if (frame.Line is not null && frame.TransitionSeconds >= incomingStart)
        {
            var p = frame.Outgoing is null
                ? 1d
                : Math.Clamp((frame.TransitionSeconds - incomingStart) / 0.60d, 0d, 1d);
            var scale = frame.Outgoing is null ? 1f : (float)(0.75d + 0.25d * Spring(p));
            DrawLineBlock(canvas, frame.Line, frame.PositionMs, clip, mainFonts, translationFont,
                scale, (float)EaseOutCubic(p));
        }
        else if (frame.Line is null)
        {
            DrawStatus(canvas, frame, clip, mainFonts, translationFont);
        }

        canvas.Restore();
        canvas.Restore();
    }

    private void DrawPill(SKCanvas canvas, SKRect rect)
    {
        using var shadow = new SKPaint
        {
            IsAntialias = true,
            Color = new SKColor(0, 0, 0, 115),
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 7f)
        };
        var shadowRect = rect;
        shadowRect.Offset(0, 3f);
        canvas.DrawRoundRect(shadowRect, rect.Height / 2f, rect.Height / 2f, shadow);

        using var fill = new SKPaint { IsAntialias = true, Color = new SKColor(3, 5, 7, 170) };
        canvas.DrawRoundRect(rect, rect.Height / 2f, rect.Height / 2f, fill);

        using var rim = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1f,
            Color = new SKColor(255, 255, 255, 10)
        };
        canvas.DrawRoundRect(rect, rect.Height / 2f, rect.Height / 2f, rim);
    }

    private void DrawSides(SKCanvas canvas, IslandFrame frame, SKRect pill)
    {
        var iconRect = SKRect.Create(pill.Left + 18f, pill.MidY - 21f, 42f, 42f);

        if (_icon is not null)
        {
            using var glow = new SKPaint
            {
                Color = new SKColor(35, 255, 110, 95),
                MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 4f)
            };
            canvas.DrawBitmap(_icon, iconRect, glow);
            canvas.DrawBitmap(_icon, iconRect);
        }

        EnsureAlbum(frame.Track);
        var albumRect = SKRect.Create(pill.Right - 62f, pill.MidY - 22f, 44f, 44f);
        canvas.Save();
        canvas.ClipRoundRect(new SKRoundRect(albumRect, 5f), SKClipOperation.Intersect, true);
        if (_album is not null)
        {
            using var imagePaint = new SKPaint { IsAntialias = true };
            canvas.DrawImage(_album, albumRect,
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), imagePaint);
        }
        else
        {
            using var placeholder = new SKPaint { Color = new SKColor(38, 42, 46) };
            canvas.DrawRect(albumRect, placeholder);
        }
        canvas.Restore();
    }

    private void DrawLineBlock(
        SKCanvas canvas,
        LyricLine line,
        long positionMs,
        SKRect clip,
        MainFontSet mainFonts,
        SKFont translationFont,
        float scale,
        float opacity)
    {
        if (opacity <= 0.001f || scale <= 0.001f)
            return;

        var layout = LayoutMainText(line.Text, mainFonts);
        var mainWidth = layout.Width;
        var translation = line.Translation ?? string.Empty;
        var hasTranslation = !string.IsNullOrWhiteSpace(translation);
        var translationWidth = translationFont.MeasureText(translation);
        var centerX = clip.MidX;
        var originY = clip.MidY + (hasTranslation ? DoubleLineOriginOffset : 1f);

        canvas.Save();
        canvas.Translate(centerX, originY);
        canvas.Scale(scale, 1f);
        canvas.Translate(-centerX, -originY);

        var mainX = centerX - mainWidth / 2f;
        var mainBaseline = hasTranslation ? originY - 5f : CenteredBaseline(originY, layout.Runs, mainFonts.Latin) - 2f;
        DrawKaraokeText(canvas, line, positionMs, mainX, mainBaseline, layout.Runs, mainFonts, opacity);

        if (hasTranslation)
        {
            using var translationPaint = new SKPaint
            {
                IsAntialias = true,
                Color = new SKColor(160, 161, 166, (byte)(220 * opacity))
            };
            canvas.DrawText(translation, centerX - translationWidth / 2f, originY + 24f, translationFont, translationPaint);
        }
        canvas.Restore();
    }

    private static void DrawKaraokeText(
        SKCanvas canvas,
        LyricLine line,
        long positionMs,
        float x,
        float baseline,
        IReadOnlyList<MainTextRun> runs,
        MainFontSet fonts,
        float opacity)
    {
        using var idle = new SKPaint
        {
            IsAntialias = true,
            Color = new SKColor(108, 109, 114, (byte)(225 * opacity))
        };
        DrawMainText(canvas, runs, x, baseline, idle);

        var activeWidth = ActiveWidth(line, positionMs, fonts);
        if (activeWidth <= 0f)
            return;

        var activeRight = x + activeWidth;
        canvas.Save();
        canvas.ClipRect(new SKRect(x - 10f, baseline - 38f, activeRight + 5f, baseline + 11f));

        using var wideGlow = new SKPaint
        {
            IsAntialias = true,
            Color = new SKColor(255, 255, 255, (byte)(42 * opacity)),
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 1.65f)
        };
        DrawMainText(canvas, runs, x, baseline, wideGlow);

        using var tightGlow = new SKPaint
        {
            IsAntialias = true,
            Color = new SKColor(255, 255, 255, (byte)(70 * opacity)),
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 0.85f)
        };
        DrawMainText(canvas, runs, x, baseline, tightGlow);

        using var core = new SKPaint
        {
            IsAntialias = true,
            Color = new SKColor(247, 248, 249, (byte)(255 * opacity))
        };
        DrawMainText(canvas, runs, x, baseline, core);
        canvas.Restore();

        // A brighter, wider pass only around the moving edge produces the soft bloom in the reference.
        canvas.Save();
        canvas.ClipRect(new SKRect(activeRight - 10f, baseline - 39f, activeRight + 8f, baseline + 12f));
        using var edgeBloom = new SKPaint
        {
            IsAntialias = true,
            Color = new SKColor(255, 255, 255, (byte)(78 * opacity)),
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 3.8f)
        };
        DrawMainText(canvas, runs, x, baseline, edgeBloom);
        canvas.Restore();
    }

    internal static float ActiveWidth(LyricLine line, long positionMs, SKFont font) =>
        ActiveWidth(line, positionMs, text => font.MeasureText(text));

    private static float ActiveWidth(LyricLine line, long positionMs, MainFontSet fonts)
    {
        var japanese = line.Text.EnumerateRunes().Any(IsKana);
        return ActiveWidth(line, positionMs, text => MeasureMainText(text, fonts, japanese));
    }

    private static float ActiveWidth(LyricLine line, long positionMs, Func<string, float> measure)
    {
        if (positionMs <= line.StartMs)
            return 0f;

        if (line.Syllables.Length == 0)
        {
            var duration = Math.Max(1, line.EndMs - line.StartMs);
            var p = Math.Clamp((positionMs - line.StartMs) / (double)duration, 0d, 1d);
            return measure(line.Text) * (float)SmoothStep(p);
        }

        var prefix = string.Empty;
        foreach (var syllable in line.Syllables)
        {
            if (positionMs < syllable.StartMs)
                return measure(prefix);

            if (positionMs <= syllable.EndMs)
            {
                var duration = Math.Max(1, syllable.EndMs - syllable.StartMs);
                var p = Math.Clamp((positionMs - syllable.StartMs) / (double)duration, 0d, 1d);
                return measure(prefix) + measure(syllable.Text) * (float)SmoothStep(p);
            }

            prefix += syllable.Text;
        }
        return measure(line.Text);
    }

    private void DrawStatus(
        SKCanvas canvas,
        IslandFrame frame,
        SKRect clip,
        MainFontSet mainFonts,
        SKFont translationFont)
    {
        var title = frame.Track?.Title ?? frame.Status ?? "等待 Spotify";
        var subtitle = StatusSubtitle(frame.Track, frame.Status);
        using var titlePaint = new SKPaint { IsAntialias = true, Color = new SKColor(242, 243, 244) };
        using var subPaint = new SKPaint { IsAntialias = true, Color = new SKColor(145, 146, 151) };
        var titleLayout = LayoutMainText(title, mainFonts);
        var subtitleWidth = translationFont.MeasureText(subtitle);
        var originY = clip.MidY + DoubleLineOriginOffset;
        DrawMainText(canvas, titleLayout.Runs, clip.MidX - titleLayout.Width / 2f, originY - 5f, titlePaint);
        canvas.DrawText(subtitle, clip.MidX - subtitleWidth / 2f, originY + 24f, translationFont, subPaint);
    }

    private static float DesiredWidth(
        TrackInfo? track,
        LyricLine? line,
        string? status,
        MainFontSet mainFonts,
        SKFont translationFont)
    {
        var main = line?.Text ?? track?.Title ?? status ?? "等待 Spotify";
        var sub = line is null
            ? StatusSubtitle(track, status)
            : line.Translation ?? string.Empty;
        var content = Math.Max(MeasureMainText(main, mainFonts), translationFont.MeasureText(sub));
        return Math.Max(content + 146f, 390f);
    }

    private static string StatusSubtitle(TrackInfo? track, string? status) => track is null
        ? status == PlaybackStore.MissingSpotifyCredentialsStatus
            ? "托盘菜单 → 设置 → Spotify"
            : "首次启动会打开浏览器授权"
        : status ?? string.Join(", ", track.Artists);

    internal static float DesiredPillHeight(LyricLine? line) =>
        line is not null && string.IsNullOrWhiteSpace(line.Translation)
            ? SingleLinePillHeight
            : DoubleLinePillHeight;

    private static float AnimatedHeight(float oldHeight, float newHeight, double seconds) =>
        seconds >= TransitionDuration || oldHeight == newHeight
            ? newHeight
            : Lerp(oldHeight, newHeight,
                (float)EaseOutCubic(Math.Clamp(seconds / TransitionDuration, 0d, 1d)));

    private static float AnimatedWidth(float oldWidth, float newWidth, double seconds)
    {
        if (seconds >= TransitionDuration)
            return newWidth;
        const float minimumWidth = CollapsedWidth - 100f;
        if (seconds < 0.11d)
            return Lerp(oldWidth, minimumWidth, (float)EaseOutCubic(seconds / 0.11d));
        if (seconds < 0.26d)
            return Lerp(minimumWidth, CollapsedWidth, (float)EaseOutCubic((seconds - 0.11d) / 0.15d));
        if (seconds < 0.30d)
            return CollapsedWidth;
        var p = Math.Clamp((seconds - 0.30d) / 0.60d, 0d, 1d);
        return Lerp(CollapsedWidth, newWidth, (float)Spring(p));
    }

    private static SKFont CreateFont(SKTypeface typeface, float size) => new(typeface, size)
    {
        Edging = SKFontEdging.Antialias,
        Subpixel = true
    };

    private static (List<MainTextRun> Runs, float Width) LayoutMainText(string text, MainFontSet fonts)
    {
        var runs = new List<MainTextRun>();
        if (text.Length == 0)
            return (runs, 0f);

        var japanese = text.EnumerateRunes().Any(IsKana);
        var offset = 0f;
        var runStart = 0;
        var index = 0;
        bool? cjk = null;
        foreach (var rune in text.EnumerateRunes())
        {
            var nextCjk = IsCjk(rune);
            if (cjk is not null && nextCjk != cjk)
            {
                AddMainTextRun(text[runStart..index], cjk.Value, japanese, fonts, runs, ref offset);
                runStart = index;
            }
            cjk = nextCjk;
            index += rune.Utf16SequenceLength;
        }
        AddMainTextRun(text[runStart..], cjk ?? false, japanese, fonts, runs, ref offset);
        return (runs, offset);
    }

    private static float MeasureMainText(string text, MainFontSet fonts) =>
        MeasureMainText(text, fonts, text.EnumerateRunes().Any(IsKana));

    private static float CenteredBaseline(float centerY, IReadOnlyList<MainTextRun> runs, SKFont fallback)
    {
        var metrics = runs.Count == 0 ? fallback.Metrics : runs[0].Font.Metrics;
        var ascent = metrics.Ascent;
        var descent = metrics.Descent;
        foreach (var run in runs)
        {
            ascent = Math.Min(ascent, run.Font.Metrics.Ascent);
            descent = Math.Max(descent, run.Font.Metrics.Descent);
        }
        return centerY - (ascent + descent) / 2f;
    }

    private static float MeasureMainText(string text, MainFontSet fonts, bool japanese)
    {
        if (text.Length == 0)
            return 0f;

        var width = 0f;
        var runStart = 0;
        var index = 0;
        bool? cjk = null;
        foreach (var rune in text.EnumerateRunes())
        {
            var nextCjk = IsCjk(rune);
            if (cjk is not null && nextCjk != cjk)
            {
                width += FontFor(cjk.Value, japanese, fonts).MeasureText(text.AsSpan(runStart, index - runStart));
                runStart = index;
            }
            cjk = nextCjk;
            index += rune.Utf16SequenceLength;
        }
        return width + FontFor(cjk ?? false, japanese, fonts).MeasureText(text.AsSpan(runStart));
    }

    private static void AddMainTextRun(
        string text,
        bool cjk,
        bool japanese,
        MainFontSet fonts,
        List<MainTextRun> runs,
        ref float offset)
    {
        var font = FontFor(cjk, japanese, fonts);
        runs.Add(new MainTextRun(text, font, offset));
        offset += font.MeasureText(text);
    }

    private static SKFont FontFor(bool cjk, bool japanese, MainFontSet fonts) =>
        cjk ? japanese ? fonts.Japanese : fonts.Cjk : fonts.Latin;

    private static void DrawMainText(
        SKCanvas canvas,
        IReadOnlyList<MainTextRun> runs,
        float x,
        float baseline,
        SKPaint paint)
    {
        foreach (var run in runs)
            canvas.DrawText(run.Text, x + run.Offset, baseline, run.Font, paint);
    }

    private static bool IsKana(System.Text.Rune rune) => rune.Value is
        >= 0x3040 and <= 0x30FF or >= 0x31F0 and <= 0x31FF or >= 0xFF65 and <= 0xFF9F;

    private static bool IsCjk(System.Text.Rune rune)
    {
        var value = rune.Value;
        return value is
            >= 0x2E80 and <= 0x9FFF
            or >= 0xAC00 and <= 0xD7AF
            or >= 0xF900 and <= 0xFAFF
            or >= 0xFE10 and <= 0xFE4F
            or >= 0xFF00 and <= 0xFFEF
            or >= 0x20000 and <= 0x323AF;
    }

    private void EnsureAlbum(TrackInfo? track)
    {
        if (ReferenceEquals(_albumTrack, track))
            return;
        _albumTrack = track;
        _album?.Dispose();
        _album = track is { AlbumArtBytes.Length: > 0 }
            ? SKImage.FromEncodedData(track.AlbumArtBytes.AsSpan().ToArray())
            : null;
    }

    internal static double Spring(double p)
    {
        p = Math.Clamp(p, 0d, 1d);
        if (p >= 1d)
            return 1d;
        var seconds = p * 0.70d;
        const double naturalFrequency = 11.4455d; // sqrt(131 / 1)
        const double dampingRatio = 0.4718d;      // 10.8 / (2 * sqrt(131))
        var dampedFrequency = naturalFrequency * Math.Sqrt(1d - dampingRatio * dampingRatio);
        return 1d - Math.Exp(-dampingRatio * naturalFrequency * seconds)
            * (Math.Cos(dampedFrequency * seconds)
               + dampingRatio / Math.Sqrt(1d - dampingRatio * dampingRatio) * Math.Sin(dampedFrequency * seconds));
    }

    private static double SmoothStep(double p) => p * p * (3d - 2d * p);
    private static double EaseInCubic(double p) => p * p * p;
    private static double EaseOutCubic(double p) => 1d - Math.Pow(1d - p, 3d);
    private static float Lerp(float a, float b, float p) => a + (b - a) * p;

    public void Dispose()
    {
        _icon?.Dispose();
        _album?.Dispose();
        _latin.Dispose();
        _cjk.Dispose();
        _japanese.Dispose();
    }

    private readonly record struct MainFontSet(SKFont Latin, SKFont Cjk, SKFont Japanese);
    private readonly record struct MainTextRun(string Text, SKFont Font, float Offset);
}

internal static class RendererDiagnostics
{
    private static int _reported;
    public static void Observe(bool gpu)
    {
        if (Interlocked.Exchange(ref _reported, 1) == 0)
            Console.Error.WriteLine(gpu ? "[render] Skia GPU acceleration active" : "[render] WARNING: Skia fell back to CPU");
    }
}

internal static class NativeOverlay
{
    private const int ClientMessage = 33;
    private const long SubstructureNotifyMask = 1L << 19;
    private const long SubstructureRedirectMask = 1L << 20;
    private const int ShapeInput = 2;

    public static void TryConfigure(Window window, bool clickThrough)
    {
        if (!OperatingSystem.IsLinux())
            return;
        var handle = window.TryGetPlatformHandle();
        if (handle?.HandleDescriptor != "XID")
            return;

        var display = IntPtr.Zero;
        try
        {
            display = XOpenDisplay(IntPtr.Zero);
            if (display == IntPtr.Zero)
                return;

            ShowOnAllWorkspaces(display, handle.Handle);
            XFlush(display);

            if (clickThrough)
            {
                var empty = XFixesCreateRegion(display, IntPtr.Zero, 0);
                if (empty != IntPtr.Zero)
                {
                    XFixesSetWindowShapeRegion(display, handle.Handle, ShapeInput, 0, 0, empty);
                    XFixesDestroyRegion(display, empty);
                    XFlush(display);
                }
            }
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            // The overlay remains usable; it just won't be click-through.
        }
        finally
        {
            if (display != IntPtr.Zero)
                XCloseDisplay(display);
        }
    }

    private static void ShowOnAllWorkspaces(IntPtr display, IntPtr window)
    {
        var root = XDefaultRootWindow(display);
        var wmDesktop = XInternAtom(display, "_NET_WM_DESKTOP", 0);
        var wmState = XInternAtom(display, "_NET_WM_STATE", 0);
        var sticky = XInternAtom(display, "_NET_WM_STATE_STICKY", 0);
        if (root == IntPtr.Zero || wmDesktop == IntPtr.Zero || wmState == IntPtr.Zero || sticky == IntPtr.Zero)
            return;

        SendClientMessage(display, root, window, wmDesktop, uint.MaxValue, 1, 0, 0, 0);
        SendClientMessage(display, root, window, wmState, 1, sticky.ToInt64(), 0, 1, 0);
    }

    private static void SendClientMessage(
        IntPtr display,
        IntPtr root,
        IntPtr window,
        IntPtr messageType,
        long data0,
        long data1,
        long data2,
        long data3,
        long data4)
    {
        var message = new XClientMessageEvent
        {
            Type = ClientMessage,
            SendEvent = 1,
            Display = display,
            Window = window,
            MessageType = messageType,
            Format = 32,
            Data0 = data0,
            Data1 = data1,
            Data2 = data2,
            Data3 = data3,
            Data4 = data4
        };
        XSendEvent(
            display,
            root,
            0,
            SubstructureNotifyMask | SubstructureRedirectMask,
            ref message);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XClientMessageEvent
    {
        public int Type;
        public nuint Serial;
        public int SendEvent;
        public IntPtr Display;
        public IntPtr Window;
        public IntPtr MessageType;
        public int Format;
        public long Data0;
        public long Data1;
        public long Data2;
        public long Data3;
        public long Data4;
    }

    [DllImport("libX11.so.6")]
    private static extern IntPtr XOpenDisplay(IntPtr displayName);
    [DllImport("libX11.so.6")]
    private static extern int XCloseDisplay(IntPtr display);
    [DllImport("libX11.so.6")]
    private static extern int XFlush(IntPtr display);
    [DllImport("libX11.so.6")]
    private static extern IntPtr XDefaultRootWindow(IntPtr display);
    [DllImport("libX11.so.6")]
    private static extern IntPtr XInternAtom(IntPtr display, string atomName, int onlyIfExists);
    [DllImport("libX11.so.6")]
    private static extern int XSendEvent(
        IntPtr display,
        IntPtr window,
        int propagate,
        long eventMask,
        ref XClientMessageEvent eventSend);
    [DllImport("libXfixes.so.3")]
    private static extern IntPtr XFixesCreateRegion(IntPtr display, IntPtr rectangles, int count);
    [DllImport("libXfixes.so.3")]
    private static extern void XFixesSetWindowShapeRegion(IntPtr display, IntPtr window, int shapeKind, int x, int y, IntPtr region);
    [DllImport("libXfixes.so.3")]
    private static extern void XFixesDestroyRegion(IntPtr display, IntPtr region);
}
