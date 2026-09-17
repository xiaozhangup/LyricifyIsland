using System.Collections.Immutable;
using Avalonia;
using SkiaSharp;

namespace LyricifyIsland;

internal enum LyricsWindowAction
{
    None, Minimize, Maximize, Close, Pin, FullScreen, Translation, LyricsOnly, Settings,
    Previous, PlayPause, Next, Shuffle, Repeat, Seek, Volume, Lyric, Follow, Island, Favorite
}

internal sealed record LyricsWindowHit(LyricsWindowAction Action, Rect Bounds, bool Enabled = true, int Line = -1);

internal sealed record LyricsWindowFrame(
    PlaybackSnapshot Snapshot, double PositionMs, double LyricsPositionMs, double Seconds,
    bool ShowTranslation, bool LyricsOnly, bool Pinned, bool FullScreen, bool Maximized,
    double? BrowseScroll, double? SeekPreview, string? Toast, Point Pointer, bool Busy)
{
    public double? VolumePreview { get; init; }
    public bool ShowIsland { get; init; } = true;
    public LyricsWindowAction? PressedAction { get; init; }
}

internal sealed record LyricsWindowLayout(Rect Surface, Rect Lyrics, Rect Cover, Rect Progress,
    Rect Transport, Rect Volume, bool Compact, float FontSize)
{
    public float PlayerScale { get; init; } = 1;

    public static LyricsWindowLayout Create(float width, float height, bool lyricsOnly, bool fillWindow = false)
    {
        var compact = width < 840 || height < 580;
        var playerScale = compact ? 1 : Math.Clamp(Math.Min(width / 1120, height / 720), 1, 2);
        var surface = fillWindow ? new Rect(0, 0, width, height) : new Rect(10, 10, width - 20, height - 20);
        if (compact || lyricsOnly)
        {
            var margin = lyricsOnly ? Math.Clamp(width * .14, 48, 180) : 48;
            var lyricHeight = height - (lyricsOnly ? 74 + 140 * playerScale : 320);
            return new(surface,
                new Rect(margin, lyricsOnly ? 74 : 180, width - margin * 2, lyricHeight),
                new Rect(48, 80, 72, 72),
                new Rect(margin, height - 114 * playerScale, width - margin * 2, 20 * playerScale),
                new Rect(width / 2 - 140 * playerScale, height - 80 * playerScale, 280 * playerScale, 48 * playerScale), default, true,
                (float)Math.Clamp(Math.Min(width * .044, lyricHeight * .13), 26, 44)) { PlayerScale = playerScale };
        }
        // Keep the whole player inside the available height while letting the cover grow with the left column.
        var coverSize = Math.Min(Math.Max(width * .30f, 240), height - 104 - 236 * playerScale);
        var left = Math.Max(52, width * .075f);
        var top = Math.Max(82, (height - coverSize - 236 * playerScale) * .5f);
        var lyricX = width * .49f;
        return new(surface, new Rect(lyricX, 76, width - lyricX - 64, height - 138),
            new Rect(left, top, coverSize, coverSize),
            new Rect(left, top + coverSize + 99 * playerScale, coverSize, 20 * playerScale),
            new Rect(left, top + coverSize + 153 * playerScale, coverSize, 48 * playerScale),
            new Rect(left + 38 * playerScale, top + coverSize + 218 * playerScale, coverSize - 76 * playerScale, 20 * playerScale),
            false, Math.Clamp(width * .034f, 30, 44)) { PlayerScale = playerScale };
    }
}

internal sealed class LyricsWindowRenderer : IDisposable
{
    // Match the secondary player buttons, with about 8 px of space around the 20 px icon.
    private const float FavoriteButtonSize = 36;
    private const float LyricHorizontalPadding = 20;
    private const double BackgroundTransitionSeconds = .85;

    private sealed class ButtonMotion
    {
        public double Hover;
        public double Active;
        public double Pressed;
        public double Enabled;
    }
    private sealed record RowLayout(LyricsTextLayout Text, LyricsTextLayout? Translation, float Top) : IDisposable
    {
        public float Height => Text.Height + (Translation is null ? 0 : Translation.Height + 9) + 32;
        public void Dispose() { Text.Dispose(); Translation?.Dispose(); }
    }
    private sealed class Row(RowLayout normal, RowLayout lyricsOnly)
    {
        public RowLayout Normal { get; } = normal;
        public RowLayout LyricsOnly { get; } = lyricsOnly;
        public bool Reflows { get; } = ChangesWrapping(normal.Text, lyricsOnly.Text)
            || normal.Translation is not null && lyricsOnly.Translation is not null
                && ChangesWrapping(normal.Translation, lyricsOnly.Translation);
        public double Top(double progress) => Mix(Normal.Top, LyricsOnly.Top, progress);
        public double Height(double progress) => Mix(Normal.Height, LyricsOnly.Height, progress);
        public double Y;
        public double Velocity;
        public double Target;
        public double Emphasis;
        public double Hover;
        public SKImage? IdleImage;
        public (double Layout, float Blur, float Scale) IdleImageKey;

        private static bool ChangesWrapping(LyricsTextLayout from, LyricsTextLayout to) =>
            from.Glyphs.Where((glyph, i) => Math.Abs(glyph.Baseline / from.FontSize
                - to.Glyphs[i].Baseline / to.FontSize) > .01f).Any();
    }

    private readonly object _gate = new();
    private readonly LyricsFonts _fonts = new();
    private readonly List<Row> _rows = [];
    private readonly Dictionary<(string, float, float, bool, int), LyricsTextLayout> _labels = [];
    private readonly List<LyricsWindowHit> _hits = [];
    private readonly Dictionary<LyricsWindowAction, ButtonMotion> _buttonMotion = [];
    private LyricsWindowHit[] _publishedHits = [];
    private LyricsWindowLayout? _publishedLayout;
    private ImmutableArray<LyricLine> _lyrics;
    private ImmutableArray<byte> _artBytes;
    private SKBitmap? _cover;
    private SKImage? _coverImage;
    private SKImage? _backdrop;
    private SKImage? _previousBackground;
    private SKImage? _windowShadow;
    private SKImage? _coverShadow;
    private (float Width, float Height, float Scale) _windowShadowKey;
    private (float Width, float Height, float Scale) _coverShadowKey;
    private float _renderScale = 1;
    private bool _backgroundInitialized;
    private double _backgroundChangedAt;
    private string? _trackId;
    private float _layoutWidth;
    private float _layoutFont;
    private float _lyricsOnlyWidth;
    private float _lyricsOnlyFont;
    private double _lyricsOnlyProgress;
    private bool _translation;
    private bool _reset = true;
    private int _active = -1;
    private double _changedAt;
    private double _lastSeconds;
    private double _frameDelta;
    private double _followOpacity;
    private double _followHover;
    private double _scrollTarget;
    private double _maxScroll;
    private double _coverScale = 1;
    private SKColor _colorA = new(57, 80, 102);
    private SKColor _colorB = new(76, 51, 75);
    private int _references = 1;

    public double ScrollTarget => Volatile.Read(ref _scrollTarget);
    public double MaxScroll => Volatile.Read(ref _maxScroll);
    public LyricsWindowLayout? Layout => Volatile.Read(ref _publishedLayout);
    public LyricsWindowHit? HitTest(Point point) => Volatile.Read(ref _publishedHits)
        .LastOrDefault(hit => hit.Bounds.Contains(point));

    public void Retain() => Interlocked.Increment(ref _references);
    public void Dispose()
    {
        if (Interlocked.Decrement(ref _references) != 0) return;
        lock (_gate)
        {
            ClearRows();
            ClearLabels();
            _cover?.Dispose();
            _coverImage?.Dispose();
            _backdrop?.Dispose();
            _previousBackground?.Dispose();
            _windowShadow?.Dispose();
            _coverShadow?.Dispose();
            _fonts.Dispose();
        }
    }

    public void Draw(SKCanvas canvas, float width, float height, LyricsWindowFrame frame)
    {
        lock (_gate)
        {
            if (_references <= 0 || width < 100 || height < 100) return;
            var matrix = canvas.TotalMatrix;
            _renderScale = Math.Max(.1f, Math.Max(
                MathF.Sqrt(matrix.ScaleX * matrix.ScaleX + matrix.SkewY * matrix.SkewY),
                MathF.Sqrt(matrix.ScaleY * matrix.ScaleY + matrix.SkewX * matrix.SkewX)));
            var dt = _lastSeconds == 0 ? 1d / 60 : Math.Clamp(frame.Seconds - _lastSeconds, 0, .05);
            _frameDelta = dt;
            _lastSeconds = frame.Seconds;
            var fillWindow = frame.FullScreen || frame.Maximized;
            var normal = LyricsWindowLayout.Create(width, height, false, fillWindow);
            var lyricsOnly = LyricsWindowLayout.Create(width, height, true, fillWindow);
            var target = frame.LyricsOnly ? 1d : 0;
            _lyricsOnlyProgress = _publishedLayout is null ? target
                : Approach(_lyricsOnlyProgress, target, dt, 10);
            if (Math.Abs(_lyricsOnlyProgress - target) < .001) _lyricsOnlyProgress = target;
            var layout = normal with
            {
                Lyrics = Mix(normal.Lyrics, lyricsOnly.Lyrics, _lyricsOnlyProgress),
                Progress = Mix(normal.Progress, lyricsOnly.Progress, _lyricsOnlyProgress),
                Transport = Mix(normal.Transport, lyricsOnly.Transport, _lyricsOnlyProgress),
                FontSize = (float)Mix(normal.FontSize, lyricsOnly.FontSize, _lyricsOnlyProgress)
            };
            EnsureTrack(frame.Snapshot.Track, normal, lyricsOnly, frame.ShowTranslation, frame.Seconds);
            _hits.Clear();
            canvas.Save();
            var rect = ToSk(layout.Surface);
            if (fillWindow)
                canvas.ClipRect(rect);
            else
            {
                DrawWindowShadow(canvas, width, height, rect);
                using var clip = new SKRoundRect(rect, 24);
                canvas.ClipRoundRect(clip, SKClipOperation.Intersect, true);
            }
            DrawBackground(canvas, rect, frame.Seconds);
            DrawHeader(canvas, width, frame);
            DrawPlayer(canvas, layout, frame, dt);
            DrawLyrics(canvas, layout, frame, dt);
            DrawFavorite(canvas, layout, frame);
            if (frame.Toast is { Length: > 0 } toast)
            {
                var toastWidth = Math.Min(width - 80, 660);
                var toastRect = new SKRect((width - toastWidth) / 2, height - 68, (width + toastWidth) / 2, height - 24);
                using var paint = Paint(new SKColor(10, 14, 20, 240));
                canvas.DrawRoundRect(toastRect, 14, 14, paint);
                Label(canvas, toast, toastRect.Left + 16, toastRect.Top + 11, 13,
                    White(235), toastWidth - 32);
            }
            if (!fillWindow)
            {
                using var rim = Paint(White(30));
                rim.Style = SKPaintStyle.Stroke;
                rim.StrokeWidth = 1;
                canvas.DrawRoundRect(rect, 24, 24, rim);
            }
            canvas.Restore();
            Volatile.Write(ref _publishedHits, _hits.ToArray());
            Volatile.Write(ref _publishedLayout, layout with
            {
                Cover = _lyricsOnlyProgress < .99 ? layout.Cover.Translate(MetadataOffset(layout)) : default
            });
        }
    }

    private void DrawWindowShadow(SKCanvas canvas, float width, float height, SKRect rect)
    {
        var key = (width, height, _renderScale);
        if (_windowShadow is null || _windowShadowKey != key)
        {
            _windowShadow?.Dispose();
            using var surface = SKSurface.Create(new SKImageInfo(
                (int)Math.Ceiling(width * _renderScale), (int)Math.Ceiling(height * _renderScale)));
            surface.Canvas.Scale(_renderScale);
            using var filter = SKImageFilter.CreateBlur(7, 7);
            using var paint = Paint(new SKColor(0, 0, 0, 90));
            paint.ImageFilter = filter;
            surface.Canvas.DrawRoundRect(rect, 24, 24, paint);
            _windowShadow = surface.Snapshot();
            _windowShadowKey = key;
        }
        canvas.Save();
        // The opaque background covers the middle; only the shadow around its edges can be seen.
        canvas.ClipRect(new SKRect(rect.Left + 25, rect.Top + 1, rect.Right - 25, rect.Bottom - 1), SKClipOperation.Difference);
        DrawCachedImage(canvas, _windowShadow, 0, 0);
        canvas.Restore();
    }

    private void DrawCoverShadow(SKCanvas canvas, SKRect cover)
    {
        const float padding = 56;
        var key = (cover.Width, cover.Height, _renderScale);
        if (_coverShadow is null || _coverShadowKey != key)
        {
            _coverShadow?.Dispose();
            using var surface = SKSurface.Create(new SKImageInfo(
                (int)Math.Ceiling((cover.Width + padding * 2) * _renderScale),
                (int)Math.Ceiling((cover.Height + padding * 2) * _renderScale)));
            surface.Canvas.Scale(_renderScale);
            using var filter = SKImageFilter.CreateDropShadow(0, 10, 14, 14, new SKColor(0, 0, 0, 100));
            using var paint = Paint(new SKColor(0, 0, 0, 100));
            paint.ImageFilter = filter;
            surface.Canvas.DrawRoundRect(SKRect.Create(padding, padding, cover.Width, cover.Height), 12, 12, paint);
            _coverShadow = surface.Snapshot();
            _coverShadowKey = key;
        }
        DrawCachedImage(canvas, _coverShadow, cover.Left - padding, cover.Top - padding);
    }

    private void DrawCachedImage(SKCanvas canvas, SKImage image, float x, float y) => canvas.DrawImage(image,
        SKRect.Create(x, y, image.Width / _renderScale, image.Height / _renderScale),
        new SKSamplingOptions(SKFilterMode.Linear));

    private void EnsureTrack(TrackInfo? track, LyricsWindowLayout layout, LyricsWindowLayout lyricsOnly,
        bool translation, double seconds)
    {
        if (_trackId != track?.Id)
        {
            _trackId = track?.Id;
            _active = -1;
            _reset = true;
            ClearLabels();
        }
        var art = track?.AlbumArtBytes ?? [];
        if (!_artBytes.Equals(art))
        {
            if (_backgroundInitialized)
            {
                // Capture the currently blended background so a second change during a fade stays continuous.
                const int snapshotWidth = 256;
                var snapshotHeight = Math.Max(1, (int)(snapshotWidth * layout.Surface.Height / layout.Surface.Width));
                using var surface = SKSurface.Create(new SKImageInfo(snapshotWidth, snapshotHeight));
                DrawBackground(surface.Canvas, new SKRect(0, 0, snapshotWidth, snapshotHeight), seconds);
                _previousBackground?.Dispose();
                _previousBackground = surface.Snapshot();
                _backgroundChangedAt = seconds;
            }
            _backgroundInitialized = true;
            _artBytes = art;
            _cover?.Dispose();
            _coverImage?.Dispose();
            _cover = art.IsDefaultOrEmpty ? null : SKBitmap.Decode(art.AsSpan());
            _coverImage = _cover is null ? null : SKImage.FromBitmap(_cover);
            _backdrop?.Dispose();
            _backdrop = null;
            _colorA = new SKColor(57, 80, 102);
            _colorB = new SKColor(76, 51, 75);
            if (_cover is not null)
            {
                using var small = SKSurface.Create(new SKImageInfo(64, 64));
                small.Canvas.DrawBitmap(_cover, new SKRect(0, 0, 64, 64));
                using var image = small.Snapshot();
                using var pixels = SKBitmap.FromImage(image);
                _colorA = Average(pixels, 0, 32);
                _colorB = Average(pixels, 32, 64);
                using var blurred = SKSurface.Create(new SKImageInfo(64, 64));
                using var blur = Paint(SKColors.White);
                using var blurFilter = SKImageFilter.CreateBlur(10, 10, SKShaderTileMode.Clamp);
                blur.ImageFilter = blurFilter;
                blurred.Canvas.DrawImage(image, 0, 0, blur);
                _backdrop = blurred.Snapshot();
            }
        }
        var lyrics = track?.Lyrics ?? [];
        if (_lyrics.Equals(lyrics) && _layoutWidth == (float)layout.Lyrics.Width
            && _layoutFont == layout.FontSize && _lyricsOnlyWidth == (float)lyricsOnly.Lyrics.Width
            && _lyricsOnlyFont == lyricsOnly.FontSize && _translation == translation) return;
        _lyrics = lyrics;
        _layoutWidth = (float)layout.Lyrics.Width;
        _layoutFont = layout.FontSize;
        _lyricsOnlyWidth = (float)lyricsOnly.Lyrics.Width;
        _lyricsOnlyFont = lyricsOnly.FontSize;
        _translation = translation;
        ClearRows();
        var top = 0f;
        var lyricsOnlyTop = 0f;
        foreach (var line in lyrics)
        {
            var normal = CreateRow(line, _layoutWidth, _layoutFont, top);
            var expanded = CreateRow(line, _lyricsOnlyWidth, _lyricsOnlyFont, lyricsOnlyTop);
            _rows.Add(new Row(normal, expanded));
            top += normal.Height;
            lyricsOnlyTop += expanded.Height;
        }
        _reset = true;
        return;

        RowLayout CreateRow(LyricLine line, float width, float font, float rowTop)
        {
            var text = new LyricsTextLayout(line.Text, font, width - LyricHorizontalPadding * 2, _fonts, timing: line);
            var translated = translation && !string.IsNullOrWhiteSpace(line.Translation)
                ? new LyricsTextLayout(line.Translation, font * .53f, width - LyricHorizontalPadding * 2, _fonts, false) : null;
            return new RowLayout(text, translated, rowTop);
        }
    }

    private static SKColor Average(SKBitmap bitmap, int from, int to)
    {
        long r = 0, g = 0, b = 0, n = 0;
        for (var y = from; y < to; y += 4)
            for (var x = 0; x < 64; x += 4)
            {
                var color = bitmap.GetPixel(x, y);
                r += color.Red; g += color.Green; b += color.Blue; n++;
            }
        return new SKColor((byte)(r / n), (byte)(g / n), (byte)(b / n));
    }

    private void DrawBackground(SKCanvas canvas, SKRect rect, double seconds)
    {
        var progress = Math.Clamp((seconds - _backgroundChangedAt) / BackgroundTransitionSeconds, 0, 1);
        if (_previousBackground is not null && progress < 1)
        {
            canvas.DrawImage(_previousBackground, rect, new SKSamplingOptions(SKFilterMode.Linear));
            var eased = progress * progress * (3 - 2 * progress);
            using var blend = Paint(White((byte)Math.Round(eased * 255)));
            canvas.SaveLayer(rect, blend);
            DrawBackdrop(canvas, rect, seconds);
            canvas.Restore();
            return;
        }
        _previousBackground?.Dispose();
        _previousBackground = null;
        DrawBackdrop(canvas, rect, seconds);
    }

    private void DrawBackdrop(SKCanvas canvas, SKRect rect, double seconds)
    {
        using var gradient = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(rect.Right, rect.Bottom),
            [Darken(_colorA, .55f), Darken(_colorB, .34f)], null, SKShaderTileMode.Clamp);
        using var art = _backdrop?.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp,
            new SKSamplingOptions(SKFilterMode.Linear),
            SKMatrix.CreateScaleTranslation(rect.Width / _backdrop.Width, rect.Height / _backdrop.Height, rect.Left, rect.Top));
        using var artAlpha = SKShader.CreateColor(White(120));
        using var fadedArt = art is null ? null : SKShader.CreateCompose(art, artAlpha, SKBlendMode.DstIn);
        using var backdrop = fadedArt is null ? null : SKShader.CreateCompose(gradient, fadedArt);
        var x = rect.Width * (float)(.22 + Math.Sin(seconds * .09) * .13);
        var y = rect.Height * (float)(.75 + Math.Cos(seconds * .07) * .15);
        using var glow = SKShader.CreateRadialGradient(new SKPoint(x, y), rect.Width * .85f,
            [_colorA.WithAlpha(86), _colorB.WithAlpha(0)], null, SKShaderTileMode.Clamp);
        using var glowing = SKShader.CreateCompose(backdrop ?? gradient, glow);
        using var shade = SKShader.CreateColor(new SKColor(5, 10, 16, 45));
        using var composite = SKShader.CreateCompose(glowing, shade);
        using var paint = Paint(SKColors.White);
        paint.Shader = composite;
        // The same background layers blend in one pass instead of repainting the entire window four times.
        canvas.DrawRect(rect, paint);
    }

    private void DrawHeader(SKCanvas canvas, float width, LyricsWindowFrame frame)
    {
        Label(canvas, "Lyricify", 36, 27, 14, White(165), 160, true);
        var x = width - 42;
        HeaderButton(LyricsWindowAction.Close, "close", "关闭", false);
        HeaderButton(LyricsWindowAction.Maximize, frame.Maximized ? "restore" : "maximize", "最大化", false);
        HeaderButton(LyricsWindowAction.Minimize, "minimize", "最小化", false);
        x -= 16;
        HeaderButton(LyricsWindowAction.FullScreen, "fullscreen", "全屏", frame.FullScreen);
        HeaderButton(LyricsWindowAction.Pin, "pin", "置顶", frame.Pinned);
        HeaderButton(LyricsWindowAction.LyricsOnly, "lyrics", "纯歌词", frame.LyricsOnly);
        HeaderButton(LyricsWindowAction.Translation, "translation", "翻译", frame.ShowTranslation);
        HeaderButton(LyricsWindowAction.Island, "island", frame.ShowIsland ? "隐藏歌词灵动岛" : "显示歌词灵动岛", frame.ShowIsland);
        HeaderButton(LyricsWindowAction.Settings, "settings", "设置", false);
        return;

        void HeaderButton(LyricsWindowAction action, string icon, string label, bool active)
        {
            var bounds = new Rect(x - 16, 22, 32, 30);
            Button(canvas, action, bounds, icon, frame, true, active, 15);
            var tooltipOpacity = _buttonMotion[action].Hover;
            if (tooltipOpacity > .01)
            {
                using var paint = Paint(new SKColor(16, 20, 28, (byte)(225 * tooltipOpacity)));
                var tooltipWidth = Math.Max(66, GetLabel(label, 11, width - 40).InkBounds.Width + 24);
                var tooltipLeft = Math.Clamp(x - tooltipWidth / 2, 16, width - tooltipWidth - 16);
                var tooltip = new SKRect(tooltipLeft, 56, tooltipLeft + tooltipWidth, 82);
                canvas.DrawRoundRect(tooltip, 8, 8, paint);
                CenteredLabel(canvas, label, tooltip, 11, White((byte)(210 * tooltipOpacity)));
            }
            x -= 38;
        }
    }

    private void DrawPlayer(SKCanvas canvas, LyricsWindowLayout layout, LyricsWindowFrame frame, double dt)
    {
        var track = frame.Snapshot.Track;
        var playerScale = layout.PlayerScale;
        var cover = ToSk(layout.Cover);
        var metadataOpacity = 1 - _lyricsOnlyProgress;
        if (metadataOpacity > .001)
        {
            var offset = MetadataOffset(layout);
            using var opacity = Paint(White((byte)Math.Round(255 * metadataOpacity)));
            if (metadataOpacity < 1) canvas.SaveLayer(opacity);
            else canvas.Save();
            canvas.Translate((float)offset.X, (float)offset.Y);
            _coverScale += ((frame.Snapshot.IsPlaying ? 1 : .95) - _coverScale) * (1 - Math.Exp(-dt * 7));
            canvas.Save();
            canvas.Translate(cover.MidX, cover.MidY);
            canvas.Scale((float)_coverScale);
            canvas.Translate(-cover.MidX, -cover.MidY);
            DrawCoverShadow(canvas, cover);
            canvas.Save();
            using var clip = new SKRoundRect(cover, layout.Compact ? 9 : 12);
            canvas.ClipRoundRect(clip, SKClipOperation.Intersect, true);
            using var paint = Paint(new SKColor(85, 97, 114));
            canvas.DrawRect(cover, paint);
            if (_coverImage is not null)
            {
                var size = Math.Min(_coverImage.Width, _coverImage.Height);
                var source = SKRect.Create((_coverImage.Width - size) / 2f, (_coverImage.Height - size) / 2f, size, size);
                canvas.DrawImage(_coverImage, source, cover, new SKSamplingOptions(SKFilterMode.Linear));
            }
            else
                Icon(canvas, "note", cover.MidX, cover.MidY, cover.Width * .27f, White(135));
            canvas.Restore();
            canvas.Restore();
            var tx = layout.Compact ? cover.Right + 22 * playerScale : cover.Left;
            var ty = layout.Compact ? cover.Top + 1 : cover.Bottom + 28 * playerScale;
            var title = track?.Title ?? "等待播放";
            var tw = (float)MetadataFavoriteBounds(layout).Left - tx - 12 * playerScale;
            Label(canvas, title, tx, ty, (layout.Compact ? 21 : 23) * playerScale, White(245), tw, true);
            Label(canvas, track is null ? "在播放器中播放一首歌" : string.Join(" / ", track.Artists),
                tx, ty + 33 * playerScale, 15 * playerScale, White(150), tw);
            if (layout.Compact)
                Label(canvas, track?.Album ?? "Lyricify Island", tx, ty + 58 * playerScale, 11 * playerScale, White(94), tw);
            canvas.Restore();
        }
        var controls = frame.Snapshot.Controls;
        var progress = layout.Progress;
        var position = frame.SeekPreview ?? frame.PositionMs;
        var duration = track?.DurationMs ?? 0;
        Slider(canvas, LyricsWindowAction.Seek, progress, duration > 0 ? position / duration : 0,
            controls.CanSeek && !frame.Busy, frame, playerScale);
        Label(canvas, Time(position), (float)progress.X, (float)progress.Bottom + playerScale,
            11 * playerScale, White(110), 60 * playerScale);
        RightAlignedLabel(canvas, "−" + Time(Math.Max(0, duration - position)), (float)progress.Right,
            (float)progress.Bottom + playerScale, 11 * playerScale, White(110), 80 * playerScale);
        var transport = layout.Transport;
        var cy = transport.Center.Y;
        var cx = transport.Center.X;
        var spacing = Math.Max(66 * playerScale, transport.Width * 66 / 336);
        Button(canvas, LyricsWindowAction.Shuffle,
            new Rect(transport.Left - 8 * playerScale, cy - 18 * playerScale, 36 * playerScale, 36 * playerScale), "shuffle",
            frame, controls.Shuffle.HasValue && !frame.Busy, controls.Shuffle == true, 19 * playerScale, playerScale);
        Button(canvas, LyricsWindowAction.Previous,
            new Rect(cx - spacing - 22 * playerScale, cy - 22 * playerScale, 44 * playerScale, 44 * playerScale), "previous",
            frame, controls.CanPrevious && !frame.Busy, false, 25 * playerScale, playerScale);
        Button(canvas, LyricsWindowAction.PlayPause,
            new Rect(cx - 27 * playerScale, cy - 27 * playerScale, 54 * playerScale, 54 * playerScale),
            frame.Snapshot.IsPlaying ? "pause" : "play", frame,
            controls.CanPlayPause && !frame.Busy, false, 29 * playerScale, playerScale);
        Button(canvas, LyricsWindowAction.Next,
            new Rect(cx + spacing - 22 * playerScale, cy - 22 * playerScale, 44 * playerScale, 44 * playerScale), "next",
            frame, controls.CanNext && !frame.Busy, false, 25 * playerScale, playerScale);
        Button(canvas, LyricsWindowAction.Repeat,
            new Rect(transport.Right - 28 * playerScale, cy - 18 * playerScale, 36 * playerScale, 36 * playerScale),
            controls.Repeat == "track" ? "repeatOne" : "repeat", frame,
            controls.Repeat is not null && !frame.Busy, controls.Repeat is "track" or "context", 19 * playerScale, playerScale);
        if (layout.Volume.Width > 0 && controls.Volume is { } volume && metadataOpacity > .001)
        {
            using var opacity = Paint(White((byte)Math.Round(255 * metadataOpacity)));
            if (metadataOpacity < 1) canvas.SaveLayer(opacity);
            else canvas.Save();
            var volumeBounds = layout.Volume.Translate(MetadataOffset(layout));
            Icon(canvas, "volume", (float)volumeBounds.Left - 21 * playerScale,
                (float)volumeBounds.Center.Y, 13 * playerScale, White(100));
            Slider(canvas, LyricsWindowAction.Volume, volumeBounds, frame.VolumePreview ?? volume,
                !frame.Busy && metadataOpacity > .2, frame, playerScale);
            canvas.Restore();
        }
    }

    private static Rect MetadataFavoriteBounds(LyricsWindowLayout layout)
    {
        var scale = layout.PlayerScale;
        var right = layout.Compact ? layout.Surface.Right - 38 * scale : layout.Cover.Right;
        var size = FavoriteButtonSize * scale;
        // The visible icon is 20 px wide; the hover background extends beyond the cover edge.
        var left = right - 10 * scale - size / 2;
        var centerY = layout.Compact ? layout.Cover.Top + 29 * scale : layout.Cover.Bottom + 56 * scale;
        return new Rect(left, centerY - size / 2, size, size);
    }

    private void DrawFavorite(SKCanvas canvas, LyricsWindowLayout layout, LyricsWindowFrame frame)
    {
        var scale = layout.PlayerScale;
        var controls = frame.Snapshot.Controls;
        // Keep the action available beside transport when lyrics-only mode hides the song information.
        var size = FavoriteButtonSize * scale;
        var lyricsOnlyBounds = new Rect(layout.Transport.Right + 16 * scale,
            layout.Transport.Center.Y - size / 2, size, size);
        var favoriteBounds = Mix(MetadataFavoriteBounds(layout).Translate(MetadataOffset(layout)),
            lyricsOnlyBounds, _lyricsOnlyProgress);
        var canFavorite = controls.CanFavorite && !frame.Busy;
        Button(canvas, LyricsWindowAction.Favorite, favoriteBounds, "addCircle",
            frame, canFavorite, controls.IsFavorite == true, 24 * scale, scale);
        var favoriteHover = !canFavorite && favoriteBounds.Contains(frame.Pointer)
            ? 1 : _buttonMotion[LyricsWindowAction.Favorite].Hover;
        if (favoriteHover > .01)
        {
            var label = !controls.CanFavorite ? "当前曲目或音源不支持收藏"
                : frame.Busy ? "操作处理中…"
                : controls.IsFavorite == true ? "取消收藏"
                : controls.IsFavorite == false ? "添加到喜欢的歌曲" : "收藏状态未知，点击添加";
            var fontSize = 11 * scale;
            var tooltipWidth = GetLabel(label, fontSize, (float)layout.Surface.Width - 40).InkBounds.Width + 24 * scale;
            var left = (float)Math.Clamp(favoriteBounds.Center.X - tooltipWidth / 2,
                layout.Surface.Left + 8, layout.Surface.Right - tooltipWidth - 8);
            var bottom = (float)favoriteBounds.Top - 8 * scale;
            var tooltip = new SKRect(left, bottom - 26 * scale, left + tooltipWidth, bottom);
            using var paint = Paint(new SKColor(16, 20, 28, (byte)(225 * favoriteHover)));
            canvas.DrawRoundRect(tooltip, 8 * scale, 8 * scale, paint);
            CenteredLabel(canvas, label, tooltip, fontSize, White((byte)(210 * favoriteHover)));
        }
    }

    private Vector MetadataOffset(LyricsWindowLayout layout) => layout.Compact
        ? new Vector(0, -14 * layout.PlayerScale * _lyricsOnlyProgress)
        : new Vector(-24 * layout.PlayerScale * _lyricsOnlyProgress, 0);

    private void DrawLyrics(SKCanvas canvas, LyricsWindowLayout layout, LyricsWindowFrame frame, double dt)
    {
        var viewport = ToSk(layout.Lyrics);
        if (_rows.Count == 0)
        {
            Icon(canvas, "lyrics", viewport.Left + 26, viewport.MidY - 82, 38, White(75));
            Label(canvas, frame.Snapshot.Track is null ? "让音乐开始吧" : "暂无歌词",
                viewport.Left, viewport.MidY - 28, layout.FontSize * .85f, White(210), viewport.Width, true);
            Label(canvas, frame.Snapshot.Track is null ? frame.Snapshot.Status : frame.Snapshot.Status.StartsWith("正在获取")
                    ? "正在寻找这首歌的歌词…" : "可以在设置中切换歌词源并重新获取",
                viewport.Left, viewport.MidY + 32, 15, White(120), viewport.Width, false, 3);
            return;
        }
        var index = IslandControl.FindLine(_lyrics, frame.LyricsPositionMs);
        if (index != _active)
        {
            _active = index;
            _changedAt = frame.Seconds;
        }
        var anchor = viewport.Top + viewport.Height * .28f;
        var automatic = _rows[Math.Max(0, index)].Top(_lyricsOnlyProgress);
        var scroll = Math.Clamp(frame.BrowseScroll ?? automatic, -viewport.Height * .25,
            _rows[^1].Top(_lyricsOnlyProgress) + viewport.Height * .15);
        Volatile.Write(ref _scrollTarget, scroll);
        Volatile.Write(ref _maxScroll, _rows[^1].Top(_lyricsOnlyProgress) + viewport.Height * .15);
        using var fadeShader = SKShader.CreateLinearGradient(new SKPoint(0, viewport.Top), new SKPoint(0, viewport.Bottom),
            [SKColors.Transparent, SKColors.White, SKColors.White, SKColors.Transparent],
            [0, .11f, .85f, 1], SKShaderTileMode.Clamp);
        var fadeTop = viewport.Top + viewport.Height * .11f;
        var fadeBottom = viewport.Top + viewport.Height * .85f;
        var activeRow = index >= 0 ? _rows[index] : null;
        // Moving/overlapping rows keep the shared mask. At rest, cached rows can include it in their image shader.
        var sharedMask = _reset || _lyricsOnlyProgress is not (0 or 1) || frame.BrowseScroll.HasValue
            || frame.Seconds - _changedAt < 2 || viewport.Contains((float)frame.Pointer.X, (float)frame.Pointer.Y)
            || _rows.Any(row => row.Hover > .0001 || Math.Abs(row.Velocity) > .1
                || Math.Abs(row.Y - (anchor + row.Top(_lyricsOnlyProgress) - scroll)) > .1)
            || activeRow is not null && (activeRow.Y - 20 < fadeTop
                || activeRow.Y + activeRow.Height(_lyricsOnlyProgress) + 20 > fadeBottom);
        canvas.Save();
        canvas.ClipRect(viewport);
        if (sharedMask) canvas.SaveLayer(viewport, null);
        for (var i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];
            var rowHeight = row.Height(_lyricsOnlyProgress);
            var target = anchor + row.Top(_lyricsOnlyProgress) - scroll;
            var delay = frame.BrowseScroll.HasValue ? 0 : Math.Clamp(i - index, 0, 6) * .032;
            if (_reset)
            {
                row.Y = row.Target = target;
                row.Velocity = 0;
                row.Emphasis = i == index ? 1 : 0;
            }
            if (frame.Seconds - _changedAt >= delay || frame.BrowseScroll.HasValue) row.Target = target;
            Spring(row, dt);
            row.Emphasis += ((i == index ? 1 : 0) - row.Emphasis) * (1 - Math.Exp(-dt * 8));
            if (row.Y + rowHeight < viewport.Top - 50 || row.Y > viewport.Bottom + 50)
            {
                row.IdleImage?.Dispose();
                row.IdleImage = null;
                continue;
            }
            var hitRect = new Rect(viewport.Left, row.Y - 10, viewport.Width, rowHeight - 10)
                .Intersect(layout.Lyrics);
            _hits.Add(new LyricsWindowHit(LyricsWindowAction.Lyric, hitRect,
                frame.Snapshot.Controls.CanSeek && !frame.Busy, i));
            var hovered = hitRect.Contains(frame.Pointer);
            row.Hover = Approach(row.Hover, hovered ? 1 : 0, dt);
            if (row.Hover > .01 && frame.Snapshot.Controls.CanSeek)
            {
                using var hover = Paint(White((byte)(14 * row.Hover)));
                canvas.DrawRoundRect(ToSk(hitRect), 10, 10, hover);
            }
            var distance = Math.Abs(i - index);
            var browseEmphasis = frame.BrowseScroll.HasValue ? 1 : row.Hover;
            var blur = (1 - browseEmphasis) * (1 - row.Emphasis) * Math.Min(2.8, .8 + distance * .45);
            canvas.Save();
            canvas.Translate(viewport.Left + LyricHorizontalPadding, (float)row.Y);
            var scale = (float)(.95 + row.Emphasis * .05);
            canvas.Scale(scale);
            if (row.Emphasis < .0001 && browseEmphasis < .0001 && _lyricsOnlyProgress is 0 or 1)
            {
                using var rowFade = !sharedMask && (row.Y - 20 < fadeTop || row.Y + rowHeight + 20 > fadeBottom)
                    ? fadeShader.WithLocalMatrix(SKMatrix.CreateScaleTranslation(1 / scale, 1 / scale,
                        -(viewport.Left + LyricHorizontalPadding) / scale, -(float)row.Y / scale)) : null;
                DrawIdleRow(canvas, row, (float)Math.Min(2.8, .8 + distance * .45), _renderScale * .95f, rowFade);
                canvas.Restore();
                continue;
            }
            using var layer = Paint(SKColors.White);
            using var blurFilter = blur > .05 ? SKImageFilter.CreateBlur((float)blur, (float)blur) : null;
            layer.ImageFilter = blurFilter;
            if (blurFilter is not null)
            {
                // Bound the blur to this row; the viewport-sized layer made every line blur mostly empty pixels.
                var rowBounds = new SKRect(-20, -20, viewport.Width / scale, (float)rowHeight + 20);
                canvas.ClipRect(rowBounds);
                canvas.SaveLayer(rowBounds, layer);
            }
            DrawRowText(canvas, row, frame.LyricsPositionMs, row.Emphasis, browseEmphasis);
            if (blurFilter is not null) canvas.Restore();
            canvas.Restore();
        }
        _reset = false;
        if (sharedMask)
        {
            using var fade = Paint(SKColors.White);
            fade.BlendMode = SKBlendMode.DstIn;
            fade.Shader = fadeShader;
            canvas.DrawRect(viewport, fade);
            canvas.Restore();
        }
        canvas.Restore();
        if (index < 0 || index >= 0 && frame.LyricsPositionMs > _lyrics[index].EndMs + 500
            && index + 1 < _lyrics.Length && _lyrics[index + 1].StartMs - _lyrics[index].EndMs > 3_000)
        {
            var start = index < 0 ? 0 : _lyrics[index].EndMs;
            var end = _lyrics[Math.Max(0, index + 1)].StartMs;
            var fraction = Math.Clamp((frame.LyricsPositionMs - start) / Math.Max(1, end - start), 0, 1);
            for (var i = 0; i < 3; i++)
            {
                using var dot = Paint(White((byte)(fraction * 3 >= i ? 210 : 70)));
                var radius = 3.5f + (float)(Math.Sin(frame.Seconds * 3 - i * .6) * .6);
                canvas.DrawCircle(viewport.Left + LyricHorizontalPadding + 8 + i * 16, viewport.Top + 40, radius, dot);
            }
        }
        _followOpacity = Approach(_followOpacity, frame.BrowseScroll.HasValue ? 1 : 0, dt);
        if (_followOpacity > .01)
        {
            var follow = new Rect(layout.Lyrics.Right - 144, layout.Lyrics.Bottom - 6, 140, 34);
            _hits.Add(new LyricsWindowHit(LyricsWindowAction.Follow, follow));
            _followHover = Approach(_followHover, follow.Contains(frame.Pointer) ? 1 : 0, dt);
            using var paint = Paint(White((byte)((24 + 16 * _followHover) * _followOpacity)));
            canvas.DrawRoundRect(ToSk(follow), 17, 17, paint);
            CenteredLabel(canvas, "回到当前歌词", ToSk(follow), 12, White((byte)(225 * _followOpacity)));
        }
    }

    private void DrawIdleRow(SKCanvas canvas, Row row, float blur, float scale, SKShader? fade)
    {
        // Only resting, visible lines are cached. Animated/highlighted lines keep the vector drawing path.
        var key = (_lyricsOnlyProgress, blur, scale);
        if (row.IdleImage is null || row.IdleImageKey != key)
        {
            row.IdleImage?.Dispose();
            var normalWidth = Math.Max(row.Normal.Text.Width, row.Normal.Translation?.Width ?? 0);
            var expandedWidth = Math.Max(row.LyricsOnly.Text.Width, row.LyricsOnly.Translation?.Width ?? 0);
            var width = (float)Mix(normalWidth, expandedWidth, _lyricsOnlyProgress);
            var height = (float)row.Height(_lyricsOnlyProgress);
            using var surface = SKSurface.Create(new SKImageInfo(
                (int)Math.Ceiling((width + 40) * scale), (int)Math.Ceiling((height + 40) * scale)));
            surface.Canvas.Scale(scale);
            surface.Canvas.Translate(20, 20);
            using var filter = SKImageFilter.CreateBlur(blur, blur);
            using var paint = Paint(SKColors.White);
            paint.ImageFilter = filter;
            surface.Canvas.SaveLayer(new SKRect(-20, -20, width + 20, height + 20), paint);
            DrawRowText(surface.Canvas, row, 0, 0, 0);
            surface.Canvas.Restore();
            row.IdleImage = surface.Snapshot();
            row.IdleImageKey = key;
        }
        var bounds = SKRect.Create(-20, -20, row.IdleImage.Width / scale, row.IdleImage.Height / scale);
        if (fade is null)
            canvas.DrawImage(row.IdleImage, bounds, new SKSamplingOptions(SKFilterMode.Linear));
        else
        {
            using var image = row.IdleImage.ToShader(SKShaderTileMode.Decal, SKShaderTileMode.Decal,
                new SKSamplingOptions(SKFilterMode.Linear), SKMatrix.CreateScaleTranslation(1 / scale, 1 / scale, -20, -20));
            using var shader = SKShader.CreateCompose(image, fade, SKBlendMode.DstIn);
            using var paint = Paint(SKColors.White);
            paint.Shader = shader;
            canvas.DrawRect(bounds, paint);
        }
    }

    private void DrawRowText(SKCanvas canvas, Row row, double position, double emphasis, double browseEmphasis)
    {
        if (row.Reflows && _lyricsOnlyProgress is > 0 and < 1)
        {
            // Crossfade a changed line break instead of making words travel through neighboring glyphs.
            var size = (float)Mix(row.Normal.Text.FontSize, row.LyricsOnly.Text.FontSize, _lyricsOnlyProgress);
            DrawWrappedLayout(row.Normal, 1 - _lyricsOnlyProgress, size);
            DrawWrappedLayout(row.LyricsOnly, _lyricsOnlyProgress, size);
            return;
        }
        DrawLyricText(canvas, row.Normal.Text, row.LyricsOnly.Text, _lyricsOnlyProgress,
            position, emphasis, browseEmphasis);
        if (row.Normal.Translation is not null && row.LyricsOnly.Translation is not null)
        {
            var opacity = 72 + emphasis * 105;
            using var translated = Paint(White((byte)(opacity + (155 - opacity) * browseEmphasis)));
            DrawMorphText(canvas, row.Normal.Translation, row.LyricsOnly.Translation, _lyricsOnlyProgress,
                (float)Mix(row.Normal.Text.Height, row.LyricsOnly.Text.Height, _lyricsOnlyProgress) + 9, translated);
        }
        return;

        void DrawWrappedLayout(RowLayout layout, double opacity, float size)
        {
            var scale = size / layout.Text.FontSize;
            var width = Math.Max(layout.Text.Width, layout.Translation?.Width ?? 0);
            using var blend = Paint(White((byte)Math.Round(opacity * 255)));
            canvas.SaveLayer(new SKRect(-20, -20, width * scale + 20, layout.Height * scale + 20), blend);
            canvas.Scale(scale);
            DrawLyricText(canvas, layout.Text, layout.Text, 0, position, emphasis, browseEmphasis);
            if (layout.Translation is not null)
            {
                var alpha = 72 + emphasis * 105;
                using var translated = Paint(White((byte)(alpha + (155 - alpha) * browseEmphasis)));
                layout.Translation.Draw(canvas, 0, layout.Text.Height + 9, translated);
            }
            canvas.Restore();
        }
    }

    private static void Spring(Row row, double dt)
    {
        // Independent critically damped-ish springs let the lower lines follow with a short delay.
        var steps = Math.Max(1, (int)Math.Ceiling(dt * 120));
        var step = dt / steps;
        for (var i = 0; i < steps; i++)
        {
            row.Velocity += ((row.Target - row.Y) * 145 - row.Velocity * 22) * step;
            row.Y += row.Velocity * step;
        }
    }

    private static double Approach(double value, double target, double dt, double speed = 16) =>
        value + (target - value) * (1 - Math.Exp(-dt * speed));

    private static double Mix(double from, double to, double progress) => from + (to - from) * progress;
    private static Rect Mix(Rect from, Rect to, double progress) => new(
        Mix(from.X, to.X, progress), Mix(from.Y, to.Y, progress),
        Mix(from.Width, to.Width, progress), Mix(from.Height, to.Height, progress));

    private static void DrawMorphText(SKCanvas canvas, LyricsTextLayout text, LyricsTextLayout expanded,
        double progress, float y, SKPaint paint)
    {
        if (progress == 1) { text = expanded; progress = 0; }
        var scale = (float)Mix(1, expanded.FontSize / text.FontSize, progress);
        for (var i = 0; i < text.Glyphs.Count; i++)
        {
            var glyph = text.Glyphs[i];
            var target = expanded.Glyphs[i];
            canvas.Save();
            canvas.Translate((float)Mix(glyph.X, target.X, progress), y + (float)Mix(glyph.Baseline, target.Baseline, progress));
            canvas.Scale(scale);
            canvas.DrawPath(glyph.Path, paint);
            canvas.Restore();
        }
    }

    private static void DrawLyricText(SKCanvas canvas, LyricsTextLayout text, LyricsTextLayout expanded,
        double layoutProgress, double position, double emphasis, double browseEmphasis)
    {
        if (layoutProgress == 1) { text = expanded; layoutProgress = 0; }
        var scale = (float)Mix(1, expanded.FontSize / text.FontSize, layoutProgress);
        var idleOpacity = 67 + emphasis * 26;
        using var idle = Paint(White((byte)(idleOpacity + (155 - idleOpacity) * browseEmphasis)));
        using var bright = Paint(White((byte)(230 * emphasis)));
        using var glow = Paint(White((byte)(60 * emphasis)));
        using var glowFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 2.5f);
        glow.MaskFilter = glowFilter;
        for (var i = 0; i < text.Glyphs.Count; i++)
        {
            var glyph = text.Glyphs[i];
            var target = expanded.Glyphs[i];
            var progress = glyph.EndMs <= glyph.StartMs ? (position >= glyph.StartMs ? 1 : 0)
                : Math.Clamp((position - glyph.StartMs) / (glyph.EndMs - glyph.StartMs), 0, 1);
            var singing = position >= glyph.StartMs && position <= glyph.EndMs;
            var lift = singing ? (float)(Math.Sin(progress * Math.PI) * Math.Min(3.0, glyph.SyllableDurationMs / 650) * emphasis) : 0;
            canvas.Save();
            canvas.Translate((float)Mix(glyph.X, target.X, layoutProgress),
                (float)Mix(glyph.Baseline, target.Baseline, layoutProgress) - lift);
            canvas.Scale(scale);
            canvas.DrawPath(glyph.Path, idle);
            if (progress > 0 && emphasis > .005)
            {
                var edge = (float)progress * glyph.Width;
                if (singing && glyph.SyllableDurationMs > 450) canvas.DrawPath(glyph.Path, glow);
                if (progress < 1)
                {
                    using var shader = SKShader.CreateLinearGradient(new SKPoint(edge - 5, 0), new SKPoint(edge + 3, 0),
                        [White((byte)(240 * emphasis)), SKColors.Transparent], null, SKShaderTileMode.Clamp);
                    bright.Shader = shader;
                    canvas.DrawPath(glyph.Path, bright);
                    bright.Shader = null;
                }
                else canvas.DrawPath(glyph.Path, bright);
            }
            canvas.Restore();
        }
    }

    private ButtonMotion AnimateControl(LyricsWindowAction action, Rect bounds, LyricsWindowFrame frame,
        bool enabled, bool active = false)
    {
        _hits.Add(new LyricsWindowHit(action, bounds, enabled));
        var hovered = bounds.Contains(frame.Pointer);
        if (!_buttonMotion.TryGetValue(action, out var motion))
            _buttonMotion[action] = motion = new ButtonMotion { Active = active ? 1 : 0, Enabled = enabled ? 1 : 0 };
        motion.Enabled = Approach(motion.Enabled, enabled ? 1 : 0, _frameDelta, 12);
        motion.Hover = Approach(motion.Hover, hovered && enabled ? 1 : 0, _frameDelta);
        motion.Active = Approach(motion.Active, active ? 1 : 0, _frameDelta);
        motion.Pressed = Approach(motion.Pressed, frame.PressedAction == action && hovered && enabled ? 1 : 0, _frameDelta);
        return motion;
    }

    private void Button(SKCanvas canvas, LyricsWindowAction action, Rect bounds, string icon,
        LyricsWindowFrame frame, bool enabled, bool active, float size, float scale = 1)
    {
        var motion = AnimateControl(action, bounds, frame, enabled, active);
        var favorite = action == LyricsWindowAction.Favorite;
        var backgroundAlpha = (favorite ? 0 : 26 * motion.Active * (.45 + .55 * motion.Enabled))
            + (favorite ? 22 : 16) * motion.Hover + 20 * motion.Pressed;
        if (backgroundAlpha > .5)
        {
            using var paint = Paint(action == LyricsWindowAction.Close
                ? new SKColor(228, 76, 91, (byte)(175 * motion.Hover)) : White((byte)backgroundAlpha));
            if (favorite)
                canvas.DrawCircle((float)bounds.Center.X, (float)bounds.Center.Y, (float)bounds.Width / 2, paint);
            else
                canvas.DrawRoundRect(ToSk(bounds), 9 * scale, 9 * scale, paint);
        }
        var iconColor = White((byte)Mix(48, 185 + 60 * Math.Max(motion.Hover, motion.Active), motion.Enabled));
        if (favorite)
        {
            var iconSize = size * (float)(1 + .04 * motion.Hover - .08 * motion.Pressed);
            // Keep the icon's right edge aligned while its hover/press animation changes its size.
            var iconX = (float)bounds.Center.X + (size - iconSize) * 10 / 24;
            Icon(canvas, "addCircle", iconX, (float)bounds.Center.Y, iconSize,
                iconColor.WithAlpha((byte)(iconColor.Alpha * (1 - motion.Active))));
            Icon(canvas, "checkCircle", iconX, (float)bounds.Center.Y, iconSize,
                iconColor.WithAlpha((byte)(iconColor.Alpha * motion.Active)));
        }
        else
            Icon(canvas, icon, (float)bounds.Center.X, (float)bounds.Center.Y, size, iconColor);
        if (motion.Active > .01 && action is LyricsWindowAction.Repeat or LyricsWindowAction.Shuffle)
        {
            using var paint = Paint(White((byte)((80 + 150 * motion.Enabled) * motion.Active)));
            canvas.DrawCircle((float)bounds.Center.X, (float)bounds.Bottom + 2 * scale, 2 * scale, paint);
        }
    }

    private void Slider(SKCanvas canvas, LyricsWindowAction action, Rect bounds, double fraction,
        bool enabled, LyricsWindowFrame frame, float scale)
    {
        var motion = AnimateControl(action, bounds, frame, enabled);
        var y = (float)bounds.Center.Y;
        var x = (float)(bounds.Left + bounds.Width * Math.Clamp(fraction, 0, 1));
        using var paint = Paint(White((byte)Mix(20, 36, motion.Enabled)));
        canvas.DrawRoundRect(new SKRect((float)bounds.Left, y - 2 * scale, (float)bounds.Right, y + 2 * scale),
            2 * scale, 2 * scale, paint);
        paint.Color = White((byte)Mix(55, 145, motion.Enabled));
        canvas.DrawRoundRect(new SKRect((float)bounds.Left, y - 2 * scale, x, y + 2 * scale), 2 * scale, 2 * scale, paint);
        if (motion.Hover > .001)
        {
            paint.Color = White((byte)(240 * motion.Hover * motion.Enabled));
            canvas.DrawCircle(x, y, 5 * scale, paint);
        }
    }

    private LyricsTextLayout GetLabel(string text, float size, float width, bool bold = false, int maxRows = 1)
    {
        var key = (text, size, MathF.Round(width), bold, maxRows);
        if (!_labels.TryGetValue(key, out var label))
        {
            if (_labels.Count > 192) ClearLabels();
            _labels[key] = label = new LyricsTextLayout(text, size, width, _fonts, bold, maxRows: maxRows);
        }
        return label;
    }

    private void CenteredLabel(SKCanvas canvas, string text, SKRect bounds, float size, SKColor color)
    {
        var label = GetLabel(text, size, bounds.Width - 16);
        using var paint = Paint(color);
        label.Draw(canvas, bounds.MidX - label.InkBounds.MidX, bounds.MidY - label.InkBounds.MidY, paint);
    }

    private void RightAlignedLabel(SKCanvas canvas, string text, float right, float y, float size, SKColor color, float width)
    {
        var label = GetLabel(text, size, width);
        using var paint = Paint(color);
        label.Draw(canvas, right - label.InkBounds.Right, y, paint);
    }

    private void Label(SKCanvas canvas, string text, float x, float y, float size, SKColor color,
        float width, bool bold = false, int maxRows = 1)
    {
        if (string.IsNullOrEmpty(text) || width <= 0) return;
        var label = GetLabel(text, size, width, bold, maxRows);
        using var paint = Paint(color);
        canvas.Save();
        canvas.ClipRect(new SKRect(x, y - 4, x + width, y + label.Height + 4));
        label.Draw(canvas, x, y, paint);
        canvas.Restore();
    }

    private void ClearRows()
    {
        foreach (var row in _rows)
        {
            row.Normal.Dispose();
            row.LyricsOnly.Dispose();
            row.IdleImage?.Dispose();
        }
        _rows.Clear();
        Volatile.Write(ref _maxScroll, 0);
    }
    private void ClearLabels()
    {
        foreach (var label in _labels.Values) label.Dispose();
        _labels.Clear();
    }

    private static string Time(double ms) => $"{(int)(Math.Max(0, ms) / 60_000)}:{(int)(Math.Max(0, ms) / 1_000) % 60:00}";
    private static SKColor White(byte alpha) => new(255, 255, 255, alpha);
    private static SKColor Darken(SKColor color, float amount) => new(
        (byte)(color.Red * amount), (byte)(color.Green * amount), (byte)(color.Blue * amount));
    private static SKPaint Paint(SKColor color) => new() { IsAntialias = true, Color = color };
    private static SKRect ToSk(Rect rect) => new((float)rect.Left, (float)rect.Top, (float)rect.Right, (float)rect.Bottom);

    private void Icon(SKCanvas canvas, string icon, float x, float y, float size, SKColor color)
    {
        canvas.Save();
        canvas.Translate(x, y);
        canvas.Scale(size / 24);
        using var paint = Paint(color);
        paint.Style = SKPaintStyle.Stroke;
        paint.StrokeWidth = 1.7f;
        paint.StrokeCap = SKStrokeCap.Round;
        paint.StrokeJoin = SKStrokeJoin.Round;
        using var path = new SKPath();
        switch (icon)
        {
            case "close": Line(-6, -6, 6, 6); Line(6, -6, -6, 6); break;
            case "minimize": Line(-7, 3, 7, 3); break;
            case "maximize": canvas.DrawRoundRect(new SKRect(-7, -7, 7, 7), 1, 1, paint); break;
            case "restore": canvas.DrawRect(new SKRect(-7, -3, 3, 7), paint); Path(( -3, -7), (7, -7), (7, 3)); break;
            case "fullscreen":
                Path((-9, -3), (-9, -9), (-3, -9)); Path((3, -9), (9, -9), (9, -3));
                Path((-9, 3), (-9, 9), (-3, 9)); Path((3, 9), (9, 9), (9, 3)); break;
            case "pause":
                paint.Style = SKPaintStyle.Fill;
                canvas.DrawRoundRect(new SKRect(-7, -9, -2, 9), 1, 1, paint);
                canvas.DrawRoundRect(new SKRect(2, -9, 7, 9), 1, 1, paint); break;
            case "play": Triangle(-6, -10, 10, 0, -6, 10); break;
            case "next": Triangle(-10, -8, 1, 0, -10, 8); Triangle(1, -8, 12, 0, 1, 8); break;
            case "previous": Triangle(10, -8, -1, 0, 10, 8); Triangle(-1, -8, -12, 0, -1, 8); break;
            case "addCircle":
                canvas.DrawCircle(0, 0, 10 - paint.StrokeWidth / 2, paint);
                Line(-4.2f, 0, 4.2f, 0); Line(0, -4.2f, 0, 4.2f);
                break;
            case "checkCircle":
                paint.Style = SKPaintStyle.Stroke;
                paint.StrokeWidth = 2.1f;
                path.MoveTo(-4.2f, .2f);
                path.LineTo(-1.2f, 3.2f);
                path.LineTo(4.7f, -3.4f);
                using (var cutout = paint.GetFillPath(path))
                    canvas.ClipPath(cutout, SKClipOperation.Difference, true);
                paint.Style = SKPaintStyle.Fill;
                canvas.DrawCircle(0, 0, 10, paint);
                break;
            case "shuffle": Path((-9, -6), (-5, -6), (5, 6), (10, 6)); Path((6, 2), (10, 6), (6, 10));
                Path((-9, 6), (-5, 6), (5, -6), (10, -6)); Path((6, -10), (10, -6), (6, -2)); break;
            case "repeat": case "repeatOne":
                Path((-9, 2), (-9, -5), (8, -5)); Path((4, -9), (8, -5), (4, -1));
                Path((9, -2), (9, 5), (-8, 5)); Path((-4, 1), (-8, 5), (-4, 9));
                if (icon == "repeatOne") { paint.Style = SKPaintStyle.Fill; canvas.DrawText("1", -2, 3, _fonts.Get("1", 8, true), paint); }
                break;
            case "pin": Path((-5, -9), (5, -9), (4, -2), (8, 3), (-8, 3), (-4, -2), (-5, -9)); Line(0, 3, 0, 10); break;
            case "lyrics": Line(-8, -7, 8, -7); Line(-8, 0, 8, 0); Line(-8, 7, 3, 7); break;
            case "island": canvas.DrawRoundRect(new SKRect(-11, -5, 11, 5), 5, 5, paint); Line(-5, 0, 5, 0); break;
            case "translation":
                paint.Style = SKPaintStyle.Fill;
                canvas.DrawText("译", -10, 8, _fonts.Get("译", 20, false), paint); break;
            case "settings":
                canvas.DrawCircle(0, 0, 6, paint); canvas.DrawCircle(0, 0, 2, paint);
                for (var i = 0; i < 8; i++) { var a = i * MathF.PI / 4; Line(MathF.Cos(a) * 7, MathF.Sin(a) * 7, MathF.Cos(a) * 9, MathF.Sin(a) * 9); }
                break;
            case "volume": Path((-8, -3), (-4, -3), (1, -7), (1, 7), (-4, 3), (-8, 3), (-8, -3));
                path.AddArc(new SKRect(-2, -6, 10, 6), -55, 110); canvas.DrawPath(path, paint); break;
            case "note": Path((-4, 6), (-4, -8), (7, -10), (7, 3));
                paint.Style = SKPaintStyle.Fill; canvas.DrawOval(-7, 6, 4, 3, paint); canvas.DrawOval(4, 3, 4, 3, paint); break;
            case "dots": paint.Style = SKPaintStyle.Fill;
                for (var i = -1; i <= 1; i++) canvas.DrawCircle(i * 7, 0, 2, paint); break;
        }
        canvas.Restore();
        return;
        void Line(float a, float b, float c, float d) => canvas.DrawLine(a, b, c, d, paint);
        void Path(params (float X, float Y)[] points)
        {
            path.Reset(); path.MoveTo(points[0].X, points[0].Y);
            foreach (var p in points.Skip(1)) path.LineTo(p.X, p.Y);
            canvas.DrawPath(path, paint);
        }
        void Triangle(float a, float b, float c, float d, float e, float f)
        {
            paint.Style = SKPaintStyle.Fill;
            path.Reset(); path.MoveTo(a, b); path.LineTo(c, d); path.LineTo(e, f); path.Close();
            canvas.DrawPath(path, paint);
        }
    }
}
