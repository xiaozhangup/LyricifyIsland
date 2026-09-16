using System.Diagnostics;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.VisualTree;

namespace LyricifyIsland;

internal sealed class LyricsWindow : Window
{
    private readonly LyricsWindowControl _lyrics;
    private NativeOverlay? _inputShape;
    private WindowState _beforeFullScreen = WindowState.Normal;

    public LyricsWindow(PlaybackStore store, IslandSettings settings, WindowIcon? icon,
        Func<PlaybackCommand, CancellationToken, Task<string?>> controlPlayback,
        Action showSettings, Func<bool, bool> setTranslation, Func<bool, bool> setIslandVisibility)
    {
        Title = "歌词 · Lyricify Island";
        Icon = icon;
        Width = 1120;
        Height = 720;
        MinWidth = 620;
        MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        WindowDecorations = WindowDecorations.None;
        ExtendClientAreaToDecorationsHint = true;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;
        CanResize = true;
        _lyrics = new LyricsWindowControl(store, settings, controlPlayback, setTranslation, setIslandVisibility);
        _lyrics.WindowAction = action =>
        {
            switch (action)
            {
                case LyricsWindowAction.Close: Close(); break;
                case LyricsWindowAction.Minimize: WindowState = WindowState.Minimized; break;
                case LyricsWindowAction.Maximize:
                    WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                    break;
                case LyricsWindowAction.FullScreen:
                    if (WindowState == WindowState.FullScreen) WindowState = _beforeFullScreen;
                    else { _beforeFullScreen = WindowState; WindowState = WindowState.FullScreen; }
                    break;
                case LyricsWindowAction.Pin: Topmost = !Topmost; break;
                case LyricsWindowAction.Settings: showSettings(); break;
            }
            UpdateWindowState();
        };
        _lyrics.MoveWindow = args =>
        {
            if (args.ClickCount == 2) _lyrics.WindowAction(LyricsWindowAction.Maximize);
            else if (WindowState != WindowState.FullScreen) BeginMoveDrag(args);
        };
        _lyrics.ResizeWindow = (edge, args) =>
        {
            if (WindowState == WindowState.Normal) BeginResizeDrag(edge, args);
        };
        Content = _lyrics;
        Opened += (_, _) =>
        {
            if (Screens.ScreenFromWindow(this) is { } screen)
            {
                Width = Math.Min(Width, screen.WorkingArea.Width / screen.Scaling);
                Height = Math.Min(Height, screen.WorkingArea.Height / screen.Scaling);
            }
            _lyrics.Focus();
            _inputShape = NativeOverlay.TryCreate(this, false);
            UpdateInputShape();
            UpdateWindowState();
        };
        PropertyChanged += (_, args) =>
        {
            if (args.Property == WindowStateProperty || args.Property == TopmostProperty || args.Property == IsVisibleProperty)
                UpdateWindowState();
            if (args.Property == BoundsProperty || args.Property.Name == "RenderScaling") UpdateInputShape();
        };
        Closed += (_, _) => { _inputShape?.Dispose(); _lyrics.Dispose(); };
    }

    private void UpdateInputShape()
    {
        var fillWindow = WindowState is WindowState.FullScreen or WindowState.Maximized;
        var bounds = new Rect(ClientSize);
        _inputShape?.SetInputRegion(fillWindow ? bounds : bounds.Deflate(10), RenderScaling,
            cornerRadius: fillWindow ? 0 : 24);
    }

    private void UpdateWindowState()
    {
        _lyrics.FullScreen = WindowState == WindowState.FullScreen;
        _lyrics.Maximized = WindowState == WindowState.Maximized;
        _lyrics.Pinned = Topmost;
        UpdateInputShape();
        if (IsVisible && WindowState != WindowState.Minimized) _lyrics.ResumeAnimation();
        _lyrics.InvalidateVisual();
    }

    public void ApplySettings(IslandSettings settings) => _lyrics.ApplySettings(settings);
}

internal sealed class LyricsWindowControl : Control, IDisposable
{
    private readonly PlaybackStore _store;
    private readonly LyricsWindowRenderer _renderer = new();
    private readonly Func<PlaybackCommand, CancellationToken, Task<string?>> _controlPlayback;
    private readonly Func<bool, bool> _setTranslation;
    private readonly Func<bool, bool> _setIslandVisibility;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private IslandSettings _settings;
    private Point _pointer = new(-1, -1);
    private Point _pressPoint;
    private LyricsWindowHit? _pressed;
    private string? _pressedTrack;
    private string? _trackId;
    private double _browseOrigin;
    private double? _browseScroll;
    private double _browseUntil;
    private double? _seekPreview;
    private double? _volumePreview;
    private bool _dragged;
    private bool _draggingLyrics;
    private bool _busy;
    private bool _queued;
    private bool _disposed;
    private string? _toast;
    private double _toastUntil;
    private StandardCursorType? _cursorType;

    public Action<LyricsWindowAction>? WindowAction { get; set; }
    public Action<PointerPressedEventArgs>? MoveWindow { get; set; }
    public Action<WindowEdge, PointerPressedEventArgs>? ResizeWindow { get; set; }
    public bool FullScreen { get; set; }
    public bool Maximized { get; set; }
    public bool Pinned { get; set; }
    private bool LyricsOnly { get; set; }

    public LyricsWindowControl(PlaybackStore store, IslandSettings settings,
        Func<PlaybackCommand, CancellationToken, Task<string?>> controlPlayback,
        Func<bool, bool> setTranslation, Func<bool, bool> setIslandVisibility)
    {
        _store = store;
        _settings = settings;
        _controlPlayback = controlPlayback;
        _setTranslation = setTranslation;
        _setIslandVisibility = setIslandVisibility;
        Focusable = true;
        AutomationProperties.SetName(this, "歌词窗口：空格播放暂停，方向键快进快退，Home 回到当前歌词，F11 全屏");
        AttachedToVisualTree += (_, _) => QueueFrame();
        PointerPressed += OnPressed;
        PointerMoved += OnMoved;
        PointerReleased += OnReleased;
        PointerCaptureLost += (_, _) => CancelGesture();
        PointerExited += (_, _) => { _pointer = new Point(-1, -1); InvalidateVisual(); };
        PointerWheelChanged += OnWheel;
        KeyDown += OnKeyDown;
    }

    public void ApplySettings(IslandSettings settings)
    {
        _settings = settings;
        InvalidateVisual();
    }

    internal void ResumeAnimation() => QueueFrame();

    private void QueueFrame()
    {
        if (_disposed || _queued || TopLevel.GetTopLevel(this) is not { } top) return;
        _queued = true;
        top.RequestAnimationFrame(_ =>
        {
            _queued = false;
            if (_disposed || !this.IsAttachedToVisualTree()) return;
            if (!top.IsVisible || top is Window { WindowState: WindowState.Minimized }) return;
            InvalidateVisual();
            QueueFrame();
        });
    }

    public override void Render(DrawingContext context)
    {
        if (_disposed) return;
        var snapshot = _store.Snapshot;
        if (_trackId != snapshot.Track?.Id)
        {
            _trackId = snapshot.Track?.Id;
            _browseScroll = null;
            CancelGesture();
        }
        if (_clock.Elapsed.TotalSeconds > _browseUntil && !_draggingLyrics) _browseScroll = null;
        var position = snapshot.PositionMs;
        var lyricPosition = Math.Clamp(position + LyricsOffset(snapshot.Track?.Id), 0,
            snapshot.Track is { DurationMs: > 0 } track ? track.DurationMs : double.MaxValue);
        var frame = new LyricsWindowFrame(snapshot, position, lyricPosition, _clock.Elapsed.TotalSeconds,
            _settings.ShowTranslation, LyricsOnly, Pinned, FullScreen, Maximized, _browseScroll, _seekPreview,
            _clock.Elapsed.TotalSeconds < _toastUntil ? _toast : null, _pointer, _busy)
        {
            VolumePreview = _volumePreview,
            ShowIsland = _settings.ShowIsland,
            PressedAction = _pressed?.Action
        };
        context.Custom(new LyricsWindowDrawOperation(new Rect(Bounds.Size), _renderer, frame));
    }

    private int LyricsOffset(string? trackId) => _settings.LyricsOffsetMs
        + (_settings.EnableTrackOffsets ? _settings.TrackOffsetFor(trackId) : 0);

    private LyricsWindowLayout Layout => _renderer.Layout
        ?? LyricsWindowLayout.Create((float)Bounds.Width, (float)Bounds.Height, LyricsOnly, FullScreen || Maximized);

    private void OnPressed(object? sender, PointerPressedEventArgs args)
    {
        if (args.GetCurrentPoint(this).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed) return;
        Focus();
        var point = args.GetPosition(this);
        _pointer = _pressPoint = point;
        if (!FullScreen && !Maximized && ResizeEdge(point) is { } edge)
        {
            ResizeWindow?.Invoke(edge, args);
            args.Handled = true;
            return;
        }
        _pressed = _renderer.HitTest(point);
        _pressedTrack = _store.Snapshot.Track?.Id;
        _dragged = false;
        if (_pressed is { Enabled: false, Action: not LyricsWindowAction.Lyric })
        { _pressed = null; args.Handled = true; return; }
        if (_pressed?.Action is LyricsWindowAction.Seek or LyricsWindowAction.Volume)
        {
            args.Pointer.Capture(this);
            UpdateSlider(point);
        }
        else if (Layout.Lyrics.Contains(point) && _pressed?.Action != LyricsWindowAction.Follow)
        {
            _draggingLyrics = true;
            _browseOrigin = _browseScroll ?? _renderer.ScrollTarget;
            args.Pointer.Capture(this);
        }
        else if (_pressed is not null)
            args.Pointer.Capture(this);
        else if (point.Y < 68 || Layout.Cover.Contains(point))
            MoveWindow?.Invoke(args);
        args.Handled = true;
    }

    private WindowEdge? ResizeEdge(Point point)
    {
        var left = point.X < 22;
        var right = point.X > Bounds.Width - 22;
        var top = point.Y < 22;
        var bottom = point.Y > Bounds.Height - 22;
        return (left, right, top, bottom) switch
        {
            (true, _, true, _) => WindowEdge.NorthWest,
            (_, true, true, _) => WindowEdge.NorthEast,
            (true, _, _, true) => WindowEdge.SouthWest,
            (_, true, _, true) => WindowEdge.SouthEast,
            (true, _, _, _) => WindowEdge.West,
            (_, true, _, _) => WindowEdge.East,
            (_, _, true, _) => WindowEdge.North,
            (_, _, _, true) => WindowEdge.South,
            _ => null
        };
    }

    private void OnMoved(object? sender, PointerEventArgs args)
    {
        _pointer = args.GetPosition(this);
        if (args.Pointer.Captured == this)
        {
            if (_pressed?.Action is LyricsWindowAction.Seek or LyricsWindowAction.Volume) UpdateSlider(_pointer);
            else if (_draggingLyrics && Math.Abs(_pointer.X - _pressPoint.X) + Math.Abs(_pointer.Y - _pressPoint.Y) > 5)
            {
                _dragged = true;
                Browse(_browseOrigin - (_pointer.Y - _pressPoint.Y));
            }
        }
        var edge = !Maximized && !FullScreen ? ResizeEdge(_pointer) : null;
        var cursor = edge switch
        {
            WindowEdge.North or WindowEdge.South => StandardCursorType.SizeNorthSouth,
            WindowEdge.East or WindowEdge.West => StandardCursorType.SizeWestEast,
            WindowEdge.NorthWest or WindowEdge.SouthEast => StandardCursorType.TopLeftCorner,
            WindowEdge.NorthEast or WindowEdge.SouthWest => StandardCursorType.TopRightCorner,
            _ => _renderer.HitTest(_pointer) is { Enabled: true } ? StandardCursorType.Hand : StandardCursorType.Arrow
        };
        if (_cursorType != cursor)
        {
            var previousCursor = Cursor;
            Cursor = new Cursor(cursor);
            _cursorType = cursor;
            previousCursor?.Dispose();
        }
        InvalidateVisual();
    }

    private void UpdateSlider(Point point)
    {
        if (_pressed is null) return;
        var fraction = Math.Clamp((point.X - _pressed.Bounds.Left) / _pressed.Bounds.Width, 0, 1);
        if (_pressed.Action == LyricsWindowAction.Seek) _seekPreview = fraction * (_store.Snapshot.Track?.DurationMs ?? 0);
        else _volumePreview = fraction;
        InvalidateVisual();
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs args)
    {
        if (args.InitialPressMouseButton != MouseButton.Left) return;
        var pressed = _pressed;
        var seek = _seekPreview;
        var volume = _volumePreview;
        var trackId = _pressedTrack;
        var dragged = _dragged;
        var point = args.GetPosition(this);
        CancelGesture();
        args.Pointer.Capture(null);
        if (pressed is null || trackId != _store.Snapshot.Track?.Id) return;
        if (pressed.Action == LyricsWindowAction.Seek && seek is { } position)
            _ = SendAsync(new PlaybackCommand(PlaybackAction.Seek, position, trackId));
        else if (pressed.Action == LyricsWindowAction.Volume && volume is { } level)
            _ = SendAsync(new PlaybackCommand(PlaybackAction.Volume, level, trackId));
        else if (!dragged && _renderer.HitTest(point) is { } released
            && released.Action == pressed.Action && released.Line == pressed.Line)
            Activate(pressed);
        args.Handled = true;
    }

    private void OnWheel(object? sender, PointerWheelEventArgs args)
    {
        if (!Layout.Lyrics.Contains(args.GetPosition(this))) return;
        Browse((_browseScroll ?? _renderer.ScrollTarget) - args.Delta.Y * 66);
        args.Handled = true;
    }

    private void Browse(double position)
    {
        _browseScroll = Math.Clamp(position, -Layout.Lyrics.Height * .25, _renderer.MaxScroll);
        _browseUntil = _clock.Elapsed.TotalSeconds + 5;
        InvalidateVisual();
    }

    private void Activate(LyricsWindowHit hit)
    {
        if (!hit.Enabled) return;
        var snapshot = _store.Snapshot;
        var track = snapshot.Track;
        switch (hit.Action)
        {
            case LyricsWindowAction.LyricsOnly: LyricsOnly = !LyricsOnly; _browseScroll = null; break;
            case LyricsWindowAction.Translation:
                if (!_setTranslation(!_settings.ShowTranslation)) Toast("翻译设置保存失败");
                break;
            case LyricsWindowAction.Island:
                if (!_setIslandVisibility(!_settings.ShowIsland)) Toast("灵动岛显示设置保存失败");
                break;
            case LyricsWindowAction.Follow: _browseScroll = null; break;
            case LyricsWindowAction.PlayPause: Send(PlaybackAction.PlayPause); break;
            case LyricsWindowAction.Previous: Send(PlaybackAction.Previous); break;
            case LyricsWindowAction.Next: Send(PlaybackAction.Next); break;
            case LyricsWindowAction.Shuffle: Send(PlaybackAction.Shuffle, snapshot.Controls.Shuffle == true ? 0 : 1); break;
            case LyricsWindowAction.Repeat:
                Send(PlaybackAction.Repeat, snapshot.Controls.Repeat switch { "off" => 1, "context" => 2, _ => 0 }); break;
            case LyricsWindowAction.Lyric:
                if (track is not null && hit.Line >= 0 && hit.Line < track.Lyrics.Length)
                    Send(PlaybackAction.Seek, Math.Max(0, track.Lyrics[hit.Line].StartMs - LyricsOffset(track.Id)));
                break;
            default: WindowAction?.Invoke(hit.Action); break;
        }
        InvalidateVisual();
        return;
        void Send(PlaybackAction action, double value = 0) => _ = SendAsync(new PlaybackCommand(action, value, track?.Id));
    }

    private async Task SendAsync(PlaybackCommand command)
    {
        if (_busy || _disposed) return;
        _busy = true;
        try
        {
            var error = await _controlPlayback(command, _lifetime.Token);
            if (_disposed) return;
            if (error is not null) Toast(error);
            else if (command.Action == PlaybackAction.Seek) _browseScroll = null;
        }
        catch (OperationCanceledException) { if (!_disposed) Toast("播放操作已取消或超时"); }
        catch (Exception) { if (!_disposed) Toast("播放控制失败，请检查播放器连接"); }
        finally { _busy = false; if (!_disposed) InvalidateVisual(); }
    }

    private void Toast(string message)
    {
        _toast = message;
        _toastUntil = _clock.Elapsed.TotalSeconds + 6;
    }

    private void OnKeyDown(object? sender, KeyEventArgs args)
    {
        var snapshot = _store.Snapshot;
        switch (args.Key)
        {
            case Key.Space when snapshot.Controls.CanPlayPause:
                Activate(new LyricsWindowHit(LyricsWindowAction.PlayPause, default)); break;
            case Key.Left when snapshot.Controls.CanSeek:
            case Key.Right when snapshot.Controls.CanSeek:
                _ = SendAsync(new PlaybackCommand(PlaybackAction.Seek,
                    Math.Clamp(snapshot.PositionMs + (args.Key == Key.Right ? 5_000 : -5_000), 0,
                        Math.Max(0, (snapshot.Track?.DurationMs ?? 0) - 1)), snapshot.Track?.Id)); break;
            case Key.Up: Browse((_browseScroll ?? _renderer.ScrollTarget) - 90); break;
            case Key.Down: Browse((_browseScroll ?? _renderer.ScrollTarget) + 90); break;
            case Key.Home: _browseScroll = null; break;
            case Key.F11: WindowAction?.Invoke(LyricsWindowAction.FullScreen); break;
            case Key.Escape:
                if (FullScreen) WindowAction?.Invoke(LyricsWindowAction.FullScreen);
                else if (_browseScroll.HasValue) _browseScroll = null;
                else WindowAction?.Invoke(LyricsWindowAction.Close);
                break;
            case Key.T: Activate(new LyricsWindowHit(LyricsWindowAction.Translation, default)); break;
            case Key.L when args.KeyModifiers.HasFlag(KeyModifiers.Control):
                Activate(new LyricsWindowHit(LyricsWindowAction.LyricsOnly, default)); break;
            default: return;
        }
        args.Handled = true;
        InvalidateVisual();
    }

    private void CancelGesture()
    {
        _pressed = null;
        _pressedTrack = null;
        _draggingLyrics = false;
        _dragged = false;
        _seekPreview = null;
        _volumePreview = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
        _renderer.Dispose();
        Cursor?.Dispose();
    }
}

internal sealed class LyricsWindowDrawOperation : ICustomDrawOperation
{
    private readonly LyricsWindowRenderer _renderer;
    private readonly LyricsWindowFrame _frame;
    private int _disposed;
    public Rect Bounds { get; }

    public LyricsWindowDrawOperation(Rect bounds, LyricsWindowRenderer renderer, LyricsWindowFrame frame)
    {
        Bounds = bounds;
        _renderer = renderer;
        _frame = frame;
        renderer.Retain();
    }

    public void Render(ImmediateDrawingContext context)
    {
        if (context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } feature) return;
        using var lease = feature.Lease();
        RendererDiagnostics.Observe(lease.GrContext is not null);
        if (lease.GrContext is not null) NativeVerticalSync.Apply();
        _renderer.Draw(lease.SkCanvas, (float)Bounds.Width, (float)Bounds.Height, _frame);
    }

    public bool HitTest(Point point)
    {
        if (_frame.FullScreen || _frame.Maximized) return Bounds.Contains(point);
        var rect = Bounds.Deflate(10);
        if (!rect.Contains(point)) return false;
        const double radius = 24;
        var x = Math.Clamp(point.X, rect.Left + radius, rect.Right - radius);
        var y = Math.Clamp(point.Y, rect.Top + radius, rect.Bottom - radius);
        return Math.Pow(point.X - x, 2) + Math.Pow(point.Y - y, 2) <= radius * radius;
    }
    public bool Equals(ICustomDrawOperation? other) => false;
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _renderer.Dispose();
    }
}
