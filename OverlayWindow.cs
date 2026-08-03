using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.VisualTree;
using SkiaSharp;

namespace LyricifyIsland;

public sealed class OverlayWindow : Window
{
    private const double BaseHeight = 116d;
    private readonly IslandControl _island;
    private IslandSettings _settings;
    private NativeOverlay? _nativeOverlay;
    private bool _opened;
    private bool _closed;
    private bool _temporarilyHidden;

    internal OverlayWindow(PlaybackStore store, IslandSettings settings, Action exit)
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
        _island.PillBoundsChanged += bounds =>
            _nativeOverlay?.SetInputRegion(bounds, RenderScaling, _temporarilyHidden);
        _island.PointerPressed += (_, args) =>
        {
            if (args.GetCurrentPoint(_island).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed)
                return;
            if (args.ClickCount == 2)
            {
                args.Handled = true;
                _ = HideForTwoSecondsAsync();
            }
            else
            {
                BeginMoveDrag(args);
            }
        };

        var hide = new MenuItem { Header = "隐藏2s" };
        hide.Click += (_, _) => _ = HideForTwoSecondsAsync();
        var center = new MenuItem { Header = "居中" };
        center.Click += (_, _) => CenterHorizontally();
        var exitItem = new MenuItem { Header = "退出" };
        exitItem.Click += (_, _) => exit();
        _island.ContextMenu = new ContextMenu { ItemsSource = new[] { hide, center, exitItem } };
        Content = _island;

        Opened += (_, _) =>
        {
            if (!_opened)
            {
                _opened = true;
                ApplyGeometry();
                Screens.Changed += ScreensChanged;
                _nativeOverlay = NativeOverlay.TryCreate(
                    this,
                    Environment.GetEnvironmentVariable("LYRICIFY_CLICK_THROUGH") == "1");
            }
            _nativeOverlay?.RestoreWindowState();
            _nativeOverlay?.SetInputRegion(_island.PillBounds, RenderScaling, _temporarilyHidden);
        };
        Closed += (_, _) =>
        {
            _closed = true;
            Screens.Changed -= ScreensChanged;
            _nativeOverlay?.Dispose();
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

    private async Task HideForTwoSecondsAsync()
    {
        if (_temporarilyHidden)
            return;
        _temporarilyHidden = true;
        Opacity = 0;
        _nativeOverlay?.SetInputRegion(_island.PillBounds, RenderScaling, transparent: true);
        await Task.Delay(TimeSpan.FromSeconds(2));
        if (!_closed)
        {
            _temporarilyHidden = false;
            _nativeOverlay?.SetInputRegion(_island.PillBounds, RenderScaling);
            Opacity = 1;
        }
    }

    private void CenterHorizontally()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary ?? Screens.All.First();
        var physicalWidth = (int)Math.Round(Bounds.Width * RenderScaling);
        Position = CenteredHorizontally(Position, screen.WorkingArea, physicalWidth);
    }

    internal static PixelPoint CenteredHorizontally(PixelPoint position, PixelRect area, int width) =>
        new(area.X + (area.Width - width) / 2, position.Y);

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
    private TrackInfo? _track;
    private int _lineIndex = int.MinValue;
    private LyricLine? _line;
    private LyricLine? _outgoing;
    private long _transitionAt;
    private double _capsuleProgress = 1d;
    private double _capsuleFrom = 1d;
    private bool _capsuleShown = true;
    private long _capsuleTransitionAt;
    private bool _capsuleTransitioning;
    private double _scale = 1d;
    private Rect _pillBounds;
    private PlaybackSnapshot? _renderedSnapshot;
    private bool _animationFrameQueued;
    private bool _disposed;

    internal Rect PillBounds => _pillBounds;
    internal event Action<Rect>? PillBoundsChanged;

    public IslandControl(PlaybackStore store)
    {
        _store = store;
        AttachedToVisualTree += (_, _) => QueueAnimationFrame();
    }

    private void QueueAnimationFrame()
    {
        if (_disposed || _animationFrameQueued || TopLevel.GetTopLevel(this) is not { } topLevel)
            return;
        _animationFrameQueued = true;
        topLevel.RequestAnimationFrame(OnAnimationFrame);
    }

    private void OnAnimationFrame(TimeSpan _)
    {
        _animationFrameQueued = false;
        if (_disposed || !this.IsAttachedToVisualTree())
            return;

        var snapshot = _store.Snapshot;
        if (snapshot.IsPlaying || _outgoing is not null || _capsuleTransitioning
            || !ReferenceEquals(snapshot, _renderedSnapshot))
            InvalidateVisual();
        QueueAnimationFrame();
    }

    public void SetScale(double scale)
    {
        _scale = Math.Clamp(scale, 0.5d, 2d);
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var snapshot = _store.Snapshot;
        _renderedSnapshot = snapshot;
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

        var capsuleShown = track is null || snapshot.IsPlaying;
        var now = Stopwatch.GetTimestamp();
        if (capsuleShown != _capsuleShown)
        {
            _capsuleShown = capsuleShown;
            _capsuleFrom = _capsuleProgress;
            _capsuleTransitionAt = now;
        }
        var capsuleSeconds = _capsuleTransitionAt == 0
            ? IslandRenderer.CapsuleTransitionDuration
            : Stopwatch.GetElapsedTime(_capsuleTransitionAt, now).TotalSeconds;
        _capsuleProgress = IslandRenderer.AnimatedCapsuleProgress(
            _capsuleFrom, capsuleShown ? 1d : 0d, capsuleSeconds);
        _capsuleTransitioning = capsuleSeconds < IslandRenderer.CapsuleTransitionDuration;

        var frame = new IslandFrame(
            track,
            _line,
            _outgoing,
            position,
            snapshot.IsPlaying,
            snapshot.Status,
            transitionSeconds,
            _scale,
            _capsuleProgress);
        var fullPillBounds = _renderer.CalculatePillBounds((float)Bounds.Width, (float)Bounds.Height, frame);
        var pillBounds = IslandRenderer.ScalePillBounds(fullPillBounds, _capsuleProgress);
        if (pillBounds != _pillBounds)
        {
            _pillBounds = pillBounds;
            PillBoundsChanged?.Invoke(pillBounds);
        }
        context.Custom(new IslandDrawOperation(
            new Rect(Bounds.Size), _renderer, frame, fullPillBounds, pillBounds));
    }

    private static double CurrentPosition(PlaybackSnapshot snapshot)
    {
        var elapsed = snapshot.IsPlaying
            ? Stopwatch.GetElapsedTime(snapshot.ReportedAtTimestamp).TotalMilliseconds
            : 0d;
        var offset = double.TryParse(
            Environment.GetEnvironmentVariable("LYRICIFY_OFFSET_MS"),
            CultureInfo.InvariantCulture,
            out var value)
            ? value
            : 0d;
        return Math.Clamp(snapshot.ReportedPositionMs + elapsed + offset, 0d, snapshot.Track?.DurationMs ?? double.MaxValue);
    }

    internal static int FindLine(IReadOnlyList<LyricLine>? lines, double positionMs)
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
        _disposed = true;
        // Render operations may still be queued on Avalonia's compositor thread.
        // The process owns this renderer; releasing it here can race the final frame.
    }
}

internal sealed class IslandDrawOperation(
    Rect bounds,
    IslandRenderer renderer,
    IslandFrame frame,
    Rect fullPillBounds,
    Rect pillBounds)
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
        renderer.Draw(lease.SkCanvas, (float)Bounds.Width, (float)Bounds.Height, frame, fullPillBounds);
    }

    public bool HitTest(Point p) => IslandRenderer.HitTest(pillBounds, p);
    public bool Equals(ICustomDrawOperation? other) => false;
    public void Dispose() { }
}

internal sealed record IslandFrame(
    TrackInfo? Track,
    LyricLine? Line,
    LyricLine? Outgoing,
    double PositionMs,
    bool IsPlaying,
    string? Status,
    double TransitionSeconds,
    double Scale,
    double CapsuleProgress = 1d);

internal sealed class IslandRenderer : IDisposable
{
    public const double TransitionDuration = 0.9;
    public const double CapsuleTransitionDuration = 0.38d;
    private const double ArtistAvatarDelay = 1.05d;
    private const double ArtistAvatarSlot = 3d;
    private const double ArtistAvatarFadeOut = 0.16d;
    private const double ArtistAvatarGap = 0.04d;
    private const double ArtistAvatarFadeIn = 0.22d;
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
    private readonly SKFont _latinFont;
    private readonly SKFont _cjkFont;
    private readonly SKFont _japaneseFont;
    private readonly SKFont _translationFont;
    private readonly SKPaint _shadow = Paint(new SKColor(0, 0, 0, 115), 7f);
    private readonly SKPaint _fill = Paint(new SKColor(3, 5, 7, 170));
    private readonly SKPaint _rim = new()
    {
        IsAntialias = true,
        Style = SKPaintStyle.Stroke,
        StrokeWidth = 1f,
        Color = new SKColor(255, 255, 255, 10)
    };
    private readonly SKPaint _iconGlow = Paint(new SKColor(35, 255, 110, 95), 4f);
    private readonly SKPaint _imagePaint = Paint(SKColors.White);
    private readonly SKPaint _placeholderPaint = Paint(new SKColor(38, 42, 46));
    private readonly SKPaint _translationPaint = Paint(new SKColor(160, 161, 166, 220));
    private readonly SKPaint _idlePaint = Paint(new SKColor(108, 109, 114, 225));
    private readonly SKPaint _wideGlow = Paint(new SKColor(255, 255, 255, 42), 1.65f);
    private readonly SKPaint _tightGlow = Paint(new SKColor(255, 255, 255, 70), 0.85f);
    private readonly SKPaint _corePaint = Paint(new SKColor(247, 248, 249));
    private readonly SKPaint _edgeBloom = Paint(new SKColor(255, 255, 255, 78), 3.8f);
    private readonly SKPaint _titlePaint = Paint(new SKColor(242, 243, 244));
    private readonly SKPaint _subtitlePaint = Paint(new SKColor(145, 146, 151));
    private readonly SKBitmap? _icon;
    private TrackInfo? _albumTrack;
    private SKImage? _album;
    private readonly List<SKImage> _artistImages = [];
    private TrackInfo? _artistTrack;
    private string? _artistTrackId;
    private long _artistTrackAt;
    private LyricLine? _layoutLine1;
    private KaraokeLayout? _karaokeLayout1;
    private LyricLine? _layoutLine2;
    private KaraokeLayout? _karaokeLayout2;

    public IslandRenderer()
    {
        _latinFont = CreateFont(_latin, MainSize);
        _cjkFont = CreateFont(_cjk, MainSize);
        _japaneseFont = CreateFont(_japanese, MainSize);
        _translationFont = CreateFont(_cjk, TranslationSize);
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("LyricifyIsland.icon.png");
        if (stream is not null)
            _icon = SKBitmap.Decode(stream);
    }

    public void Draw(SKCanvas canvas, float width, float height, IslandFrame frame, Rect? calculatedPillBounds = null)
    {
        var visualScale = (float)Math.Clamp(frame.Scale, 0.5d, 2d);
        canvas.Save();
        canvas.Clear(SKColors.Transparent);
        canvas.Scale(visualScale);
        width /= visualScale;
        height /= visualScale;

        var mainFonts = new MainFontSet(_latinFont, _cjkFont, _japaneseFont);

        var pillRect = calculatedPillBounds is { } bounds
            ? new SKRect(
                (float)(bounds.Left / visualScale),
                (float)(bounds.Top / visualScale),
                (float)(bounds.Right / visualScale),
                (float)(bounds.Bottom / visualScale))
            : PillRect(width, height, frame, mainFonts, _translationFont);
        var capsuleProgress = (float)Math.Clamp(frame.CapsuleProgress, 0d, 1d);
        if (capsuleProgress <= 0f)
        {
            canvas.Restore();
            return;
        }
        canvas.Translate(pillRect.MidX, pillRect.MidY);
        canvas.Scale(capsuleProgress);
        canvas.Translate(-pillRect.MidX, -pillRect.MidY);
        var pillHeight = pillRect.Height;
        var pillLeft = pillRect.Left;
        var pillTop = pillRect.Top;

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
            DrawLineBlock(canvas, frame.Outgoing, frame.PositionMs, clip, mainFonts, _translationFont,
                1f - 0.25f * (float)EaseOutCubic(p), (float)(1d - EaseOutCubic(p)));
        }

        var incomingStart = frame.Outgoing is null ? 0d : 0.30d;
        if (frame.Line is not null && frame.TransitionSeconds >= incomingStart)
        {
            var p = frame.Outgoing is null
                ? 1d
                : Math.Clamp((frame.TransitionSeconds - incomingStart) / 0.60d, 0d, 1d);
            var scale = frame.Outgoing is null ? 1f : (float)(0.75d + 0.25d * Spring(p));
            DrawLineBlock(canvas, frame.Line, frame.PositionMs, clip, mainFonts, _translationFont,
                scale, (float)EaseOutCubic(p));
        }
        else if (frame.Line is null)
        {
            DrawStatus(canvas, frame, clip, mainFonts, _translationFont);
        }

        canvas.Restore();
        canvas.Restore();
    }

    internal Rect CalculatePillBounds(float width, float height, IslandFrame frame)
    {
        var visualScale = (float)Math.Clamp(frame.Scale, 0.5d, 2d);
        var pill = PillRect(
            width / visualScale,
            height / visualScale,
            frame,
            new MainFontSet(_latinFont, _cjkFont, _japaneseFont),
            _translationFont);
        return new Rect(
            pill.Left * visualScale,
            pill.Top * visualScale,
            pill.Width * visualScale,
            pill.Height * visualScale);
    }

    internal static bool HitTest(Rect pill, Point point)
    {
        if (pill.Width <= 0d || pill.Height <= 0d || !pill.Contains(point))
            return false;

        var radius = pill.Height / 2d;
        if (point.X >= pill.Left + radius && point.X <= pill.Right - radius)
            return true;
        var centerX = point.X < pill.Center.X ? pill.Left + radius : pill.Right - radius;
        var dx = point.X - centerX;
        var dy = point.Y - pill.Center.Y;
        return dx * dx + dy * dy <= radius * radius;
    }

    internal static double AnimatedCapsuleProgress(double from, double to, double seconds)
    {
        var progress = SmoothStep(Math.Clamp(seconds / CapsuleTransitionDuration, 0d, 1d));
        return from + (to - from) * progress;
    }

    internal static Rect ScalePillBounds(Rect bounds, double progress)
    {
        progress = Math.Clamp(progress, 0d, 1d);
        var width = bounds.Width * progress;
        var height = bounds.Height * progress;
        return new Rect(bounds.Center.X - width / 2d, bounds.Center.Y - height / 2d, width, height);
    }

    private static SKRect PillRect(
        float width,
        float height,
        IslandFrame frame,
        MainFontSet mainFonts,
        SKFont translationFont)
    {
        var settledWidth = DesiredWidth(frame.Track, frame.Line, frame.Status, mainFonts, translationFont);
        var outgoingWidth = DesiredWidth(frame.Track, frame.Outgoing, frame.Status, mainFonts, translationFont);
        var pillWidth = Math.Min(width - 16f, AnimatedWidth(outgoingWidth, settledWidth, frame.TransitionSeconds));
        var pillHeight = AnimatedHeight(
            DesiredPillHeight(frame.Outgoing ?? frame.Line),
            DesiredPillHeight(frame.Line),
            frame.TransitionSeconds);
        var pillLeft = (width - pillWidth) / 2f;
        var pillTop = (height - pillHeight) / 2f - 2f;
        return new SKRect(pillLeft, pillTop, pillLeft + pillWidth, pillTop + pillHeight);
    }

    private void DrawPill(SKCanvas canvas, SKRect rect)
    {
        var shadowRect = rect;
        shadowRect.Offset(0, 3f);
        canvas.DrawRoundRect(shadowRect, rect.Height / 2f, rect.Height / 2f, _shadow);
        canvas.DrawRoundRect(rect, rect.Height / 2f, rect.Height / 2f, _fill);
        canvas.DrawRoundRect(rect, rect.Height / 2f, rect.Height / 2f, _rim);
    }

    private void DrawSides(SKCanvas canvas, IslandFrame frame, SKRect pill)
    {
        EnsureArtists(frame.Track);
        var avatarRect = SKRect.Create(pill.Left + 18f, pill.MidY - 24f, 48f, 48f);
        var avatarAge = _artistTrackAt == 0
            ? 0d
            : Stopwatch.GetElapsedTime(_artistTrackAt).TotalSeconds;
        var avatar = ArtistAvatarState(avatarAge, _artistImages.Count);
        DrawAvatar(canvas, avatarRect, avatar.Index, avatar.Opacity);

        EnsureAlbum(frame.Track);
        var albumRect = SKRect.Create(pill.Right - 62f, pill.MidY - 22f, 44f, 44f);
        canvas.Save();
        canvas.ClipRoundRect(new SKRoundRect(albumRect, 5f), SKClipOperation.Intersect, true);
        if (_album is not null)
        {
            canvas.DrawImage(_album, albumRect,
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), _imagePaint);
        }
        else
        {
            canvas.DrawRect(albumRect, _placeholderPaint);
        }
        canvas.Restore();
    }

    private void DrawAvatar(SKCanvas canvas, SKRect rect, int index, float opacity)
    {
        if (opacity <= 0.001f)
            return;

        var alpha = (byte)Math.Round(255f * opacity);
        _imagePaint.Color = new SKColor(255, 255, 255, alpha);
        if (index < 0)
        {
            if (_icon is not null)
            {
                _iconGlow.Color = new SKColor(35, 255, 110, (byte)Math.Round(95f * opacity));
                canvas.DrawBitmap(_icon, rect, _iconGlow);
                canvas.DrawBitmap(_icon, rect, _imagePaint);
            }
        }
        else if (index < _artistImages.Count)
        {
            canvas.Save();
            canvas.ClipRoundRect(
                new SKRoundRect(rect, rect.Width / 2f), SKClipOperation.Intersect, true);
            canvas.DrawImage(_artistImages[index], rect,
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), _imagePaint);
            canvas.Restore();
        }
        _imagePaint.Color = SKColors.White;
    }

    internal static (int Index, float Opacity) ArtistAvatarState(double trackAge, int artistCount)
    {
        if (artistCount <= 0 || trackAge < ArtistAvatarDelay)
            return (-1, 1f);

        var elapsed = trackAge - ArtistAvatarDelay;
        var slot = (int)(elapsed / ArtistAvatarSlot);
        if (slot > artistCount)
            return (-1, 1f);

        var local = elapsed - slot * ArtistAvatarSlot;
        var from = slot - 1;
        var to = slot < artistCount ? slot : -1;
        if (local < ArtistAvatarFadeOut)
            return (from, 1f - (float)EaseInCubic(local / ArtistAvatarFadeOut));
        if (local < ArtistAvatarFadeOut + ArtistAvatarGap)
            return (-1, 0f);
        if (local < ArtistAvatarFadeOut + ArtistAvatarGap + ArtistAvatarFadeIn)
            return (to, (float)EaseOutCubic(
                (local - ArtistAvatarFadeOut - ArtistAvatarGap) / ArtistAvatarFadeIn));
        return (to, 1f);
    }

    private void DrawLineBlock(
        SKCanvas canvas,
        LyricLine line,
        double positionMs,
        SKRect clip,
        MainFontSet mainFonts,
        SKFont translationFont,
        float scale,
        float opacity)
    {
        if (opacity <= 0.001f || scale <= 0.001f)
            return;

        var layout = LayoutLine(line, mainFonts);
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
        DrawKaraokeText(canvas, line, positionMs, mainX, mainBaseline, layout, mainFonts, opacity);

        if (hasTranslation)
        {
            _translationPaint.Color = new SKColor(160, 161, 166, (byte)(220 * opacity));
            canvas.DrawText(translation, centerX - translationWidth / 2f, originY + 24f, translationFont, _translationPaint);
        }
        canvas.Restore();
    }

    private void DrawKaraokeText(
        SKCanvas canvas,
        LyricLine line,
        double positionMs,
        float x,
        float baseline,
        KaraokeLayout layout,
        MainFontSet fonts,
        float opacity)
    {
        var activeWidth = ActiveWidth(line, positionMs, fonts);
        _idlePaint.Color = new SKColor(108, 109, 114, (byte)(225 * opacity));
        if (activeWidth <= 0f)
        {
            DrawMainText(canvas, layout.Runs, x, baseline, _idlePaint);
            return;
        }

        var activeRight = x + activeWidth;
        foreach (var glyph in layout.Glyphs)
        {
            var visibleWidth = Math.Clamp(activeWidth - glyph.Offset, 0f, glyph.Width);
            var liftProgress = HighlightAgeProgress(positionMs - glyph.StartMs);
            var glowProgress = HighlightAgeProgress(positionMs - glyph.EndMs);
            var glyphX = x + glyph.Offset;
            var glyphBaseline = baseline - 4f * liftProgress;
            if (visibleWidth < glyph.Width)
            {
                canvas.Save();
                canvas.ClipRect(new SKRect(
                    glyphX + visibleWidth, glyphBaseline - 38f,
                    glyphX + glyph.Width + 1f, glyphBaseline + 11f));
                canvas.Translate(glyphX, glyphBaseline);
                canvas.DrawPath(glyph.Path, _idlePaint);
                canvas.Restore();
            }
            if (visibleWidth <= 0f)
                continue;

            canvas.Save();
            canvas.ClipRect(new SKRect(
                glyphX - 10f, glyphBaseline - 39f,
                glyphX + visibleWidth + 5f, glyphBaseline + 12f));
            canvas.Translate(glyphX, glyphBaseline);
            if (glowProgress > 0f)
            {
                _edgeBloom.Color = new SKColor(255, 255, 255, (byte)(51f * glowProgress * opacity));
                canvas.DrawPath(glyph.Path, _edgeBloom);
            }
            _wideGlow.Color = new SKColor(255, 255, 255, (byte)(42 * opacity));
            canvas.DrawPath(glyph.Path, _wideGlow);
            _tightGlow.Color = new SKColor(255, 255, 255, (byte)(70 * opacity));
            canvas.DrawPath(glyph.Path, _tightGlow);
            _corePaint.Color = new SKColor(247, 248, 249, (byte)(255 * opacity));
            canvas.DrawPath(glyph.Path, _corePaint);
            canvas.Restore();

            if (visibleWidth < glyph.Width)
            {
                // The reference keeps a brighter bloom around the moving highlight edge.
                canvas.Save();
                canvas.ClipRect(new SKRect(
                    activeRight - 10f, glyphBaseline - 39f,
                    activeRight + 8f, glyphBaseline + 12f));
                canvas.Translate(glyphX, glyphBaseline);
                _edgeBloom.Color = new SKColor(255, 255, 255, (byte)(78 * opacity));
                canvas.DrawPath(glyph.Path, _edgeBloom);
                canvas.Restore();
            }
        }
    }

    internal static float HighlightAgeProgress(double ageMs) => ageMs <= 0d
        ? 0f
        : 1f - MathF.Exp((float)(-ageMs / 650d));

    internal static float ActiveWidth(LyricLine line, double positionMs, SKFont font) =>
        ActiveWidth(line, positionMs, text => font.MeasureText(text));

    private static float ActiveWidth(LyricLine line, double positionMs, MainFontSet fonts)
    {
        var japanese = line.Text.EnumerateRunes().Any(IsKana);
        return ActiveWidth(line, positionMs, text => MeasureMainText(text, fonts, japanese));
    }

    private static float ActiveWidth(LyricLine line, double positionMs, Func<string, float> measure)
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
        var titleLayout = LayoutMainText(title, mainFonts);
        var subtitleWidth = translationFont.MeasureText(subtitle);
        var originY = clip.MidY + DoubleLineOriginOffset;
        DrawMainText(canvas, titleLayout.Runs, clip.MidX - titleLayout.Width / 2f, originY - 5f, _titlePaint);
        canvas.DrawText(subtitle, clip.MidX - subtitleWidth / 2f, originY + 24f, translationFont, _subtitlePaint);
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
        Hinting = SKFontHinting.None,
        LinearMetrics = true,
        Subpixel = true
    };

    private static SKPaint Paint(SKColor color, float blur = 0f) => new()
    {
        IsAntialias = true,
        Color = color,
        MaskFilter = blur > 0f ? SKMaskFilter.CreateBlur(SKBlurStyle.Normal, blur) : null
    };

    private KaraokeLayout LayoutLine(LyricLine line, MainFontSet fonts)
    {
        if (ReferenceEquals(line, _layoutLine1))
            return _karaokeLayout1!;
        if (ReferenceEquals(line, _layoutLine2))
            return _karaokeLayout2!;

        _karaokeLayout2?.Dispose();
        _layoutLine2 = _layoutLine1;
        _karaokeLayout2 = _karaokeLayout1;
        _layoutLine1 = line;
        var (runs, width) = LayoutMainText(line.Text, fonts);
        return _karaokeLayout1 = new KaraokeLayout(runs, BuildGlyphs(runs, line, width), width);
    }

    private static List<KaraokeGlyph> BuildGlyphs(List<MainTextRun> runs, LyricLine line, float lineWidth)
    {
        var glyphs = new List<KaraokeGlyph>();
        var textOffset = 0;
        foreach (var run in runs)
        {
            var runOffset = 0;
            var previousWidth = 0f;
            var elements = StringInfo.GetTextElementEnumerator(run.Text);
            while (elements.MoveNext())
            {
                var text = elements.GetTextElement();
                runOffset += text.Length;
                var width = run.Font.MeasureText(run.Text.AsSpan(0, runOffset));
                glyphs.Add(new KaraokeGlyph(
                    run.Font.GetTextPath(text, SKPoint.Empty), run.Offset + previousWidth,
                    Math.Max(0.001f, width - previousWidth), textOffset));
                previousWidth = width;
                textOffset += text.Length;
            }
        }

        SetGlyphEndTimes(glyphs, line, lineWidth);
        return glyphs;
    }

    private static void SetGlyphEndTimes(List<KaraokeGlyph> glyphs, LyricLine line, float lineWidth)
    {
        var syllableOffset = 0;
        var glyphIndex = 0;
        if (!line.Syllables.IsDefaultOrEmpty
            && line.Syllables.Sum(syllable => syllable.Text.Length) == line.Text.Length)
        {
            foreach (var syllable in line.Syllables)
            {
                var first = glyphIndex;
                var endOffset = syllableOffset + syllable.Text.Length;
                while (glyphIndex < glyphs.Count && glyphs[glyphIndex].TextOffset < endOffset)
                    glyphIndex++;
                SetGlyphRangeEndTimes(glyphs, first, glyphIndex, syllable.StartMs, syllable.EndMs);
                syllableOffset = endOffset;
            }
            return;
        }

        SetGlyphRangeEndTimes(glyphs, 0, glyphs.Count, line.StartMs, line.EndMs, lineWidth);
    }

    private static void SetGlyphRangeEndTimes(
        List<KaraokeGlyph> glyphs,
        int first,
        int end,
        long startMs,
        long endMs,
        float? knownWidth = null)
    {
        if (first >= end)
            return;
        var visualStart = glyphs[first].Offset;
        var width = Math.Max(knownWidth
            ?? glyphs[end - 1].Offset + glyphs[end - 1].Width - visualStart, 0.001f);
        var duration = Math.Max(1L, endMs - startMs);
        for (var i = first; i < end; i++)
        {
            var left = Math.Clamp((glyphs[i].Offset - visualStart) / width, 0f, 1f);
            var right = Math.Clamp(
                (glyphs[i].Offset + glyphs[i].Width - visualStart) / width, 0f, 1f);
            glyphs[i].StartMs = startMs + duration * InverseSmoothStep(left);
            glyphs[i].EndMs = startMs + duration * InverseSmoothStep(right);
        }
    }

    private static double InverseSmoothStep(double value) =>
        0.5d - Math.Sin(Math.Asin(1d - 2d * Math.Clamp(value, 0d, 1d)) / 3d);

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
            ? SKImage.FromEncodedData(track.AlbumArtBytes.AsSpan())
            : null;
    }

    private void EnsureArtists(TrackInfo? track)
    {
        var now = Stopwatch.GetTimestamp();
        if (track?.Id != _artistTrackId)
        {
            _artistTrackId = track?.Id;
            _artistTrackAt = track is null ? 0 : now;
            _artistTrack = null;
            ClearArtists();
        }
        if (ReferenceEquals(_artistTrack, track))
            return;

        _artistTrack = track;
        var hadImages = _artistImages.Count > 0;
        ClearArtists();
        if (track is not null && !track.ArtistImageBytes.IsDefaultOrEmpty)
        {
            foreach (var bytes in track.ArtistImageBytes)
            {
                if (!bytes.IsDefaultOrEmpty && SKImage.FromEncodedData(bytes.AsSpan()) is { } image)
                    _artistImages.Add(image);
            }
        }

        if (!hadImages && _artistImages.Count > 0 && _artistTrackAt != 0
            && Stopwatch.GetElapsedTime(_artistTrackAt, now).TotalSeconds > ArtistAvatarDelay)
            _artistTrackAt = now - (long)(ArtistAvatarDelay * Stopwatch.Frequency);
    }

    private void ClearArtists()
    {
        foreach (var image in _artistImages)
            image.Dispose();
        _artistImages.Clear();
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
        _karaokeLayout1?.Dispose();
        _karaokeLayout2?.Dispose();
        _icon?.Dispose();
        _album?.Dispose();
        ClearArtists();
        _latinFont.Dispose();
        _cjkFont.Dispose();
        _japaneseFont.Dispose();
        _translationFont.Dispose();
        _shadow.Dispose();
        _fill.Dispose();
        _rim.Dispose();
        _iconGlow.Dispose();
        _imagePaint.Dispose();
        _placeholderPaint.Dispose();
        _translationPaint.Dispose();
        _idlePaint.Dispose();
        _wideGlow.Dispose();
        _tightGlow.Dispose();
        _corePaint.Dispose();
        _edgeBloom.Dispose();
        _titlePaint.Dispose();
        _subtitlePaint.Dispose();
        _latin.Dispose();
        _cjk.Dispose();
        _japanese.Dispose();
    }

    private sealed record KaraokeLayout(List<MainTextRun> Runs, List<KaraokeGlyph> Glyphs, float Width) : IDisposable
    {
        public void Dispose()
        {
            foreach (var glyph in Glyphs)
                glyph.Path.Dispose();
        }
    }

    private sealed record KaraokeGlyph(SKPath Path, float Offset, float Width, int TextOffset)
    {
        public double StartMs { get; set; }
        public double EndMs { get; set; }
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

internal sealed class NativeOverlay : IDisposable
{
    private const int ClientMessage = 33;
    private const long SubstructureNotifyMask = 1L << 19;
    private const long SubstructureRedirectMask = 1L << 20;
    private const int ShapeInput = 2;
    private IntPtr _display;
    private readonly IntPtr _window;
    private readonly bool _clickThrough;
    private PixelRect? _inputBounds;
    private bool _inputConfigured;
    private bool _inputTransparent;
    private bool _shapeAvailable = true;

    private NativeOverlay(IntPtr display, IntPtr window, bool clickThrough)
    {
        _display = display;
        _window = window;
        _clickThrough = clickThrough;
    }

    public static NativeOverlay? TryCreate(Window window, bool clickThrough)
    {
        if (!OperatingSystem.IsLinux())
            return null;
        var handle = window.TryGetPlatformHandle();
        if (handle?.HandleDescriptor != "XID")
            return null;

        var display = IntPtr.Zero;
        try
        {
            display = XOpenDisplay(IntPtr.Zero);
            if (display == IntPtr.Zero)
                return null;
            return new NativeOverlay(display, handle.Handle, clickThrough);
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            if (display != IntPtr.Zero)
                XCloseDisplay(display);
            return null;
        }
    }

    public void SetInputRegion(Rect logicalBounds, double scaling, bool transparent = false)
    {
        if (_display == IntPtr.Zero || !_shapeAvailable)
            return;

        transparent |= _clickThrough || logicalBounds.Width <= 0d || logicalBounds.Height <= 0d;
        var bounds = PhysicalBounds(logicalBounds, scaling);
        if (_inputConfigured && _inputTransparent == transparent && (transparent || _inputBounds == bounds))
            return;

        var region = IntPtr.Zero;
        try
        {
            if (transparent)
            {
                region = XFixesCreateRegion(_display, IntPtr.Zero, 0);
            }
            else
            {
                var rows = CapsuleRows(bounds);
                var rectangles = new XRectangle[rows.Length];
                for (var i = 0; i < rows.Length; i++)
                {
                    rectangles[i] = new XRectangle
                    {
                        X = (short)Math.Clamp(rows[i].X, short.MinValue, short.MaxValue),
                        Y = (short)Math.Clamp(rows[i].Y, short.MinValue, short.MaxValue),
                        Width = (ushort)Math.Clamp(rows[i].Width, 0, ushort.MaxValue),
                        Height = 1
                    };
                }
                region = XFixesCreateRegionRectangles(_display, rectangles, rectangles.Length);
            }

            if (region == IntPtr.Zero)
                return;
            XFixesSetWindowShapeRegion(_display, _window, ShapeInput, 0, 0, region);
            XFlush(_display);
            _inputBounds = bounds;
            _inputConfigured = true;
            _inputTransparent = transparent;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            _shapeAvailable = false;
        }
        finally
        {
            if (region != IntPtr.Zero)
                XFixesDestroyRegion(_display, region);
        }
    }

    public void RestoreWindowState()
    {
        if (_display == IntPtr.Zero)
            return;
        ShowOnAllWorkspaces(_display, _window);
        XFlush(_display);
        _inputConfigured = false;
    }

    internal static PixelRect[] CapsuleRows(PixelRect bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return [];

        var rows = new PixelRect[bounds.Height];
        var radius = Math.Min(bounds.Width, bounds.Height) / 2d;
        for (var row = 0; row < bounds.Height; row++)
        {
            var dy = Math.Abs(row + 0.5d - bounds.Height / 2d);
            var dx = Math.Sqrt(Math.Max(0d, radius * radius - dy * dy));
            var inset = Math.Max(0, (int)Math.Ceiling(radius - dx - 0.5d));
            rows[row] = new PixelRect(
                bounds.X + inset,
                bounds.Y + row,
                Math.Max(1, bounds.Width - inset * 2),
                1);
        }
        return rows;
    }

    private static PixelRect PhysicalBounds(Rect bounds, double scaling)
    {
        if (!double.IsFinite(scaling) || scaling <= 0d)
            scaling = 1d;
        var left = (int)Math.Floor(bounds.Left * scaling);
        var top = (int)Math.Floor(bounds.Top * scaling);
        var right = (int)Math.Ceiling(bounds.Right * scaling);
        var bottom = (int)Math.Ceiling(bounds.Bottom * scaling);
        return new PixelRect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    public void Dispose()
    {
        if (_display == IntPtr.Zero)
            return;
        XCloseDisplay(_display);
        _display = IntPtr.Zero;
    }

    private static void ShowOnAllWorkspaces(IntPtr display, IntPtr window)
    {
        var root = XDefaultRootWindow(display);
        var wmDesktop = XInternAtom(display, "_NET_WM_DESKTOP", 0);
        var wmState = XInternAtom(display, "_NET_WM_STATE", 0);
        var sticky = XInternAtom(display, "_NET_WM_STATE_STICKY", 0);
        var above = XInternAtom(display, "_NET_WM_STATE_ABOVE", 0);
        var skipTaskbar = XInternAtom(display, "_NET_WM_STATE_SKIP_TASKBAR", 0);
        if (root == IntPtr.Zero || wmDesktop == IntPtr.Zero || wmState == IntPtr.Zero
            || sticky == IntPtr.Zero || above == IntPtr.Zero || skipTaskbar == IntPtr.Zero)
            return;

        SendClientMessage(display, root, window, wmDesktop, uint.MaxValue, 1, 0, 0, 0);
        SendClientMessage(display, root, window, wmState, 1, sticky.ToInt64(), above.ToInt64(), 1, 0);
        SendClientMessage(display, root, window, wmState, 1, skipTaskbar.ToInt64(), 0, 1, 0);
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

    [StructLayout(LayoutKind.Sequential)]
    private struct XRectangle
    {
        public short X;
        public short Y;
        public ushort Width;
        public ushort Height;
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
    [DllImport("libXfixes.so.3", EntryPoint = "XFixesCreateRegion")]
    private static extern IntPtr XFixesCreateRegionRectangles(
        IntPtr display,
        [In] XRectangle[] rectangles,
        int count);
    [DllImport("libXfixes.so.3")]
    private static extern void XFixesSetWindowShapeRegion(IntPtr display, IntPtr window, int shapeKind, int x, int y, IntPtr region);
    [DllImport("libXfixes.so.3")]
    private static extern void XFixesDestroyRegion(IntPtr display, IntPtr region);
}
