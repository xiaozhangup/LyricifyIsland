using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SkiaSharp;

namespace LyricifyIsland;

public sealed class OverlayWindow : Window
{
    private const double BaseHeight = 116d;
    private static readonly IBrush MenuBackground = new SolidColorBrush(Color.Parse("#101010"));
    private static readonly IBrush MenuBorder = new SolidColorBrush(Color.Parse("#2A2A2A"));
    private static readonly IBrush MenuForeground = new SolidColorBrush(Color.Parse("#F5F5F5"));
    private static readonly IBrush MenuDisabled = new SolidColorBrush(Color.Parse("#777777"));
    private readonly PlaybackStore _store;
    private readonly IslandControl _island;
    private readonly Func<TrackInfo, int, bool> _trackOffsetChanged;
    private readonly Func<PlaybackCommand, CancellationToken, Task<string?>> _controlPlayback;
    private readonly CancellationTokenSource _lifetime = new();
    private PointerPressedEventArgs? _pressArgs;
    private Point _pressPoint;
    private bool _dragged;
    private IDisposable? _pendingClick;
    private bool _playbackBusy;
    private readonly MenuItem _hideItem;
    private readonly MenuItem _bannerItem;
    private readonly MenuItem _earlierTrackOffsetItem;
    private readonly MenuItem _laterTrackOffsetItem;
    private readonly MenuItem _resetTrackOffsetItem;
    private readonly Separator _trackOffsetSeparator;
    private IslandSettings _settings;
    private NativeOverlay? _nativeOverlay;
    private bool _opened;
    private bool _closed;
    private bool _temporarilyHidden;

    internal OverlayWindow(
        PlaybackStore store,
        IslandSettings settings,
        Func<TrackInfo, int, bool> trackOffsetChanged,
        Func<PlaybackCommand, CancellationToken, Task<string?>> controlPlayback,
        Action showSettings,
        Action showLyricsWindow,
        Action showBanner,
        Action exit)
    {
        _store = store;
        _settings = SettingsStore.Normalize(settings);
        _trackOffsetChanged = trackOffsetChanged;
        _controlPlayback = controlPlayback;
        Title = "词岛";
        Width = 960;
        Height = CalculateLogicalHeight(_settings.ScalePercent);
        Background = Brushes.Transparent;
        WindowDecorations = Avalonia.Controls.WindowDecorations.None;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        CanResize = false;
        ShowInTaskbar = false;
        ShowActivated = false;
        ExtendClientAreaToDecorationsHint = true;

        _island = new IslandControl(store);
        _island.ApplySettings(_settings);
        _island.PillBoundsChanged += UpdateSurfaceRegions;
        _island.PointerPressed += OnIslandPressed;
        _island.PointerMoved += OnIslandMoved;
        _island.PointerReleased += OnIslandReleased;
        _island.PointerCaptureLost += (_, _) => _pressArgs = null;
        _island.PointerWheelChanged += OnIslandWheel;

        _hideItem = CreateContextMenuItem(HideLabel(_settings.TemporaryHideSeconds));
        _hideItem.Click += (_, _) => _ = HideTemporarilyAsync();
        _bannerItem = CreateContextMenuItem("复制歌曲分享图…");
        _bannerItem.Click += (_, _) => showBanner();
        _earlierTrackOffsetItem = CreateContextMenuItem("歌词提前 100 ms");
        _earlierTrackOffsetItem.StaysOpenOnClick = true;
        _earlierTrackOffsetItem.Click += (_, _) => AdjustTrackOffset(100);
        _laterTrackOffsetItem = CreateContextMenuItem("歌词延后 100 ms");
        _laterTrackOffsetItem.StaysOpenOnClick = true;
        _laterTrackOffsetItem.Click += (_, _) => AdjustTrackOffset(-100);
        _resetTrackOffsetItem = CreateContextMenuItem("归零");
        _resetTrackOffsetItem.StaysOpenOnClick = true;
        _resetTrackOffsetItem.Click += (_, _) => ResetTrackOffset();
        _trackOffsetSeparator = CreateMenuSeparator();
        var settingsItem = CreateContextMenuItem("设置…");
        settingsItem.Click += (_, _) => showSettings();
        var lyricsWindowItem = CreateContextMenuItem("打开歌词窗口");
        lyricsWindowItem.Click += (_, _) => showLyricsWindow();
        var exitItem = CreateContextMenuItem("退出");
        exitItem.Click += (_, _) => exit();
        var contextMenu = new ContextMenu
        {
            ItemsSource = new Control[]
            {
                lyricsWindowItem,
                CreateMenuSeparator(),
                _hideItem,
                _bannerItem,
                _trackOffsetSeparator,
                _earlierTrackOffsetItem,
                _laterTrackOffsetItem,
                _resetTrackOffsetItem,
                CreateMenuSeparator(),
                settingsItem,
                exitItem
            },
            Width = 180,
            Padding = new Thickness(4),
            Background = MenuBackground,
            Foreground = MenuForeground,
            BorderBrush = MenuBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            FontFamily = new FontFamily("Noto Sans CJK SC"),
            FontSize = 12.5,
            LetterSpacing = 0,
            WindowManagerAddShadowHint = true
        };
        contextMenu.Resources["MenuFlyoutPresenterBackground"] = MenuBackground;
        contextMenu.Resources["MenuFlyoutPresenterBorderBrush"] = MenuBorder;
        contextMenu.Resources["MenuFlyoutPresenterBorderThemeThickness"] = new Thickness(1);
        contextMenu.Resources["MenuFlyoutPresenterThemePadding"] = new Thickness(4);
        contextMenu.Resources["MenuFlyoutThemeMinHeight"] = 30d;
        contextMenu.Resources["MenuFlyoutItemThemePadding"] = new Thickness(9, 4);
        contextMenu.Resources["MenuFlyoutItemForeground"] = MenuForeground;
        contextMenu.Resources["MenuFlyoutItemForegroundPointerOver"] = MenuForeground;
        contextMenu.Resources["MenuFlyoutItemForegroundPressed"] = MenuForeground;
        contextMenu.Resources["MenuFlyoutItemForegroundDisabled"] = MenuDisabled;
        contextMenu.Opening += (_, _) =>
        {
            CancelPendingClick();
            RefreshTrackOffsetMenu();
            _bannerItem.IsEnabled = _store.Snapshot.Track is not null;
        };
        _island.ContextMenu = contextMenu;
        RefreshTrackOffsetMenu();
        Content = _island;

        Opened += (_, _) =>
        {
            if (!_opened)
            {
                _opened = true;
                ApplyGeometry();
                Screens.Changed += ScreensChanged;
                _nativeOverlay = NativeOverlay.TryCreate(this);
            }
            UpdateSurfaceRegions(_island.PillBounds);
        };
        Closed += (_, _) =>
        {
            _closed = true;
            CancelPendingClick();
            _lifetime.Cancel();
            _lifetime.Dispose();
            Screens.Changed -= ScreensChanged;
            _nativeOverlay?.Dispose();
            _island.Dispose();
        };
    }

    internal void ApplySettings(IslandSettings settings)
    {
        var previous = _settings;
        _settings = SettingsStore.Normalize(settings);
        _island.ApplySettings(_settings);
        _hideItem.Header = HideLabel(_settings.TemporaryHideSeconds);
        RefreshTrackOffsetMenu();
        UpdateSurfaceRegions(_island.PillBounds);
        var geometryChanged = previous.WidthPercent != _settings.WidthPercent
            || previous.ScalePercent != _settings.ScalePercent;
        if (IsVisible && geometryChanged)
            ApplyGeometry();
    }

    private void UpdateSurfaceRegions(Rect bounds)
    {
        _nativeOverlay?.SetIslandRegion(bounds, _settings.ClickThrough || _temporarilyHidden,
            _settings.BackgroundBlur && !_temporarilyHidden);
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

    private void CancelPendingClick()
    {
        _pendingClick?.Dispose();
        _pendingClick = null;
    }

    private void OnIslandPressed(object? sender, PointerPressedEventArgs args)
    {
        CancelPendingClick();
        if (args.GetCurrentPoint(_island).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed)
            return;
        args.Handled = true;
        if (args.ClickCount == 2)
        {
            _pressArgs = null;
            _ = HideTemporarilyAsync();
            return;
        }
        _pressArgs = args;
        _pressPoint = args.GetPosition(_island);
        _dragged = false;
        args.Pointer.Capture(_island);
    }

    private void OnIslandMoved(object? sender, PointerEventArgs args)
    {
        if (_pressArgs is not { } press || _dragged)
            return;
        var point = args.GetPosition(_island);
        var tapSize = Application.Current!.PlatformSettings!.GetTapSize(args.Pointer.Type);
        if (Math.Abs(point.X - _pressPoint.X) <= tapSize.Width / 2
            && Math.Abs(point.Y - _pressPoint.Y) <= tapSize.Height / 2)
            return;
        _dragged = true;
        if (!_settings.LockPosition)
        {
            _pressArgs = null;
            BeginMoveDrag(press);
        }
        args.Handled = true;
    }

    private void OnIslandReleased(object? sender, PointerReleasedEventArgs args)
    {
        if (args.InitialPressMouseButton != MouseButton.Left || _pressArgs is null)
            return;
        _pressArgs = null;
        args.Pointer.Capture(null);
        args.Handled = true;
        if (_dragged || !IslandRenderer.HitTest(_island.PillBounds, args.GetPosition(_island)))
            return;
        var trackId = _store.Snapshot.Track?.Id;
        // Wait for the system's double-click interval so hiding never also pauses playback.
        _pendingClick = DispatcherTimer.RunOnce(() =>
        {
            _pendingClick = null;
            _ = SendPlaybackAsync(PlaybackAction.PlayPause, trackId);
        }, Application.Current!.PlatformSettings!.GetDoubleTapTime(args.Pointer.Type));
    }

    private void OnIslandWheel(object? sender, PointerWheelEventArgs args)
    {
        if (args.Delta.Y == 0)
            return;
        CancelPendingClick();
        args.Handled = true;
        _ = SendPlaybackAsync(args.Delta.Y > 0 ? PlaybackAction.Previous : PlaybackAction.Next,
            _store.Snapshot.Track?.Id);
    }

    private async Task SendPlaybackAsync(PlaybackAction action, string? trackId)
    {
        var snapshot = _store.Snapshot;
        if (_closed || _playbackBusy || _island.TrackSwitchInProgress || snapshot.Track is null || snapshot.Track.Id != trackId)
            return;
        var allowed = action switch
        {
            PlaybackAction.PlayPause => snapshot.Controls.CanPlayPause,
            PlaybackAction.Previous => snapshot.Controls.CanPrevious,
            PlaybackAction.Next => snapshot.Controls.CanNext,
            _ => false
        };
        if (!allowed)
            return;
        _playbackBusy = true;
        var pausing = action == PlaybackAction.PlayPause && snapshot.IsPlaying;
        var switching = action is PlaybackAction.Previous or PlaybackAction.Next;
        if (pausing)
            _island.RetainOnIslandPause(trackId!);
        if (switching)
            _island.BeginTrackSwitch(action == PlaybackAction.Next ? 1 : -1, snapshot);
        try
        {
            var error = await _controlPlayback(new PlaybackCommand(action, TrackId: trackId), _lifetime.Token);
            if (switching) _island.FinishTrackSwitch(error is null);
            if (error is not null)
            {
                if (pausing) _island.CancelIslandPause();
                Console.Error.WriteLine($"[island] {error}");
            }
        }
        catch (OperationCanceledException)
        {
            if (switching) _island.FinishTrackSwitch(false);
            if (pausing) _island.CancelIslandPause();
        }
        catch (Exception exception)
        {
            if (switching) _island.FinishTrackSwitch(false);
            if (pausing) _island.CancelIslandPause();
            Console.Error.WriteLine($"[island] 播放控制失败：{exception.Message}");
        }
        finally { _playbackBusy = false; }
    }

    private async Task HideTemporarilyAsync()
    {
        if (_temporarilyHidden)
            return;
        _temporarilyHidden = true;
        Opacity = 0;
        UpdateSurfaceRegions(_island.PillBounds);
        await Task.Delay(TimeSpan.FromSeconds(_settings.TemporaryHideSeconds));
        if (!_closed)
        {
            _temporarilyHidden = false;
            UpdateSurfaceRegions(_island.PillBounds);
            Opacity = 1;
        }
    }

    private void AdjustTrackOffset(int delta)
    {
        if (!_settings.EnableTrackOffsets || _store.Snapshot.Track is not { } track)
            return;
        var current = _settings.TrackOffsetFor(track.Id);
        var updated = SettingsStore.NormalizeLyricsOffsetMs(current + delta);
        if (updated != current && !_trackOffsetChanged(track, updated))
        {
            (delta > 0 ? _earlierTrackOffsetItem : _laterTrackOffsetItem).Header = "保存失败";
            return;
        }
        RefreshTrackOffsetMenu();
    }

    private void ResetTrackOffset()
    {
        if (_settings.EnableTrackOffsets && _store.Snapshot.Track is { } track
            && _settings.TrackOffsetFor(track.Id) != 0)
        {
            if (!_trackOffsetChanged(track, 0))
            {
                _resetTrackOffsetItem.Header = "保存失败";
                return;
            }
        }
        RefreshTrackOffsetMenu();
    }

    private void RefreshTrackOffsetMenu()
    {
        var track = _store.Snapshot.Track;
        var trackActionsVisible = _settings.EnableTrackOffsets && track is not null;
        _trackOffsetSeparator.IsVisible = trackActionsVisible;
        _earlierTrackOffsetItem.IsVisible = trackActionsVisible;
        _laterTrackOffsetItem.IsVisible = trackActionsVisible;
        _earlierTrackOffsetItem.Header = "歌词提前 100 ms";
        _laterTrackOffsetItem.Header = "歌词延后 100 ms";
        var offset = _settings.TrackOffsetFor(track?.Id);
        _earlierTrackOffsetItem.IsEnabled = offset < SettingsStore.MaximumLyricsOffsetMs;
        _laterTrackOffsetItem.IsEnabled = offset > SettingsStore.MinimumLyricsOffsetMs;
        _resetTrackOffsetItem.IsVisible = trackActionsVisible && offset != 0;
        _resetTrackOffsetItem.Header = offset > 0
            ? $"归零（+{offset} ms）"
            : $"归零（{offset} ms）";
    }

    private static MenuItem CreateContextMenuItem(string header)
    {
        var highlight = new SolidColorBrush(Color.Parse("#242424"))
        {
            Opacity = 0,
            Transitions = new Transitions
            {
                new DoubleTransition
                {
                    Property = Brush.OpacityProperty,
                    Duration = TimeSpan.FromMilliseconds(70)
                }
            }
        };
        var item = new MenuItem
        {
            Header = header,
            Width = 170,
            MinHeight = 30,
            Padding = new Thickness(9, 4),
            Background = highlight,
            CornerRadius = new CornerRadius(6),
            LetterSpacing = 0
        };
        item.Resources["MenuFlyoutItemBackground"] = highlight;
        item.Resources["MenuFlyoutItemBackgroundPointerOver"] = highlight;
        item.Resources["MenuFlyoutItemBackgroundPressed"] = highlight;
        item.PointerEntered += (_, _) => highlight.Opacity = item.IsEnabled ? 1 : 0;
        item.PointerExited += (_, _) => highlight.Opacity = 0;
        return item;
    }

    private static Separator CreateMenuSeparator() => new()
    {
        Height = 1,
        Margin = new Thickness(7, 2),
        Background = MenuBorder
    };

    private static string HideLabel(int seconds) => $"隐藏 {seconds} 秒";

    private void ApplyGeometry()
    {
        var screen = Screens.Primary ?? Screens.All.FirstOrDefault();
        var width = Math.Round(screen is null ? Width
            : CalculateLogicalWidth(screen.WorkingArea.Width, screen.Scaling, _settings.WidthPercent));
        var height = Math.Round(CalculateLogicalHeight(_settings.ScalePercent));
        MinWidth = Math.Min(MinWidth, width);
        MinHeight = Math.Min(MinHeight, height);
        MaxWidth = Math.Max(MaxWidth, width);
        MaxHeight = Math.Max(MaxHeight, height);
        Width = width;
        Height = height;
        MinWidth = MaxWidth = width;
        MinHeight = MaxHeight = height;
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
    private int _lyricsOffsetMs;
    private ImmutableDictionary<string, int>? _trackOffsetsMs;
    private bool _showTranslation = true;
    private bool _showIsland = true;
    private PausedDisplayMode _pausedDisplay;
    private bool _inactive;
    private long _inactiveAt;
    private bool _delayedHidePending;
    private string? _islandPauseTrackId;
    private bool _islandPauseObserved;
    private double _sidesProgress = 1d;
    private double _sidesFrom = 1d;
    private bool _sidesShown = true;
    private long _sidesTransitionAt;
    private bool _sidesTransitioning;
    private double _lyricsEmphasis = 1d;
    private double _lyricsFrom = 1d;
    private bool _lyricsPlaying = true;
    private long _lyricsTransitionAt;
    private bool _lyricsTransitioning;
    private IslandFrame? _renderedFrame;
    private IslandFrame? _switchOutgoing;
    private int _switchDirection;
    private long _switchPositionMs;
    private long _switchRequestedAt;
    private long _switchCompletedAt;
    private long _switchStartedAt;
    private long _switchReturningAt;
    private Rect _pillBounds;
    private PlaybackSnapshot? _renderedSnapshot;
    private bool _animationFrameQueued;
    private readonly FrameScheduler _frameScheduler = new();
    private IDisposable? _idleFrame;
    private bool _disposed;

    internal Rect PillBounds => _pillBounds;
    internal bool TrackSwitchInProgress => _switchOutgoing is not null;
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
        var snapshot = _store.Snapshot;
        if ((!_showIsland || !snapshot.IsPlaying) && _outgoing is null && !_capsuleTransitioning && !_sidesTransitioning
            && !_lyricsTransitioning
            && !TrackSwitchInProgress
            && !_delayedHidePending && ReferenceEquals(snapshot, _renderedSnapshot))
        {
            _idleFrame = Avalonia.Threading.DispatcherTimer.RunOnce(() =>
            {
                _idleFrame = null;
                OnAnimationFrame(default);
            }, TimeSpan.FromMilliseconds(100));
            return;
        }
        _frameScheduler.Request(topLevel, OnAnimationFrame);
    }

    private void OnAnimationFrame(TimeSpan _)
    {
        _animationFrameQueued = false;
        if (_disposed || !this.IsAttachedToVisualTree())
            return;

        var snapshot = _store.Snapshot;
        if ((_showIsland && snapshot.IsPlaying) || _outgoing is not null || _capsuleTransitioning || _sidesTransitioning
            || _lyricsTransitioning || _delayedHidePending
            || TrackSwitchInProgress
            || !ReferenceEquals(snapshot, _renderedSnapshot))
            InvalidateVisual();
        QueueAnimationFrame();
    }

    internal void ApplySettings(IslandSettings settings)
    {
        _scale = Math.Clamp(settings.ScalePercent / 100d, 0.5d, 2d);
        _lyricsOffsetMs = settings.LyricsOffsetMs;
        _trackOffsetsMs = settings.EnableTrackOffsets ? settings.TrackOffsetsMs : null;
        _showTranslation = settings.ShowTranslation;
        _showIsland = settings.ShowIsland;
        if (_renderedSnapshot is null)
        {
            _capsuleShown = _showIsland;
            _capsuleProgress = _capsuleFrom = _showIsland ? 1 : 0;
        }
        _pausedDisplay = settings.PausedDisplay;
        _renderer.SetBackgroundOpacity(settings.BackgroundOpacityPercent);
        if (_idleFrame is not null)
        {
            _idleFrame.Dispose();
            _idleFrame = null;
            _animationFrameQueued = false;
            QueueAnimationFrame();
        }
        InvalidateVisual();
    }

    internal void RetainOnIslandPause(string trackId)
    {
        _islandPauseTrackId = trackId;
        _islandPauseObserved = false;
        InvalidateVisual();
    }

    internal void CancelIslandPause()
    {
        ResetIslandPause();
        InvalidateVisual();
    }

    private void ResetIslandPause()
    {
        _islandPauseTrackId = null;
        _islandPauseObserved = false;
    }

    internal void BeginTrackSwitch(int direction, PlaybackSnapshot snapshot)
    {
        if (_renderedFrame is not { Track: not null } outgoing || outgoing.Track.Id != snapshot.Track!.Id)
            return;
        _switchOutgoing = outgoing with { TrackSwitch = null };
        _switchDirection = direction;
        _switchPositionMs = snapshot.ReportedPositionMs;
        _switchRequestedAt = Stopwatch.GetTimestamp();
        _switchCompletedAt = _switchStartedAt = _switchReturningAt = 0;
        InvalidateVisual();
    }

    internal void FinishTrackSwitch(bool succeeded)
    {
        if (_switchOutgoing is null || _switchStartedAt != 0 || _switchReturningAt != 0)
            return;
        if (succeeded) _switchCompletedAt = Stopwatch.GetTimestamp();
        else _switchReturningAt = Stopwatch.GetTimestamp();
        InvalidateVisual();
    }

    private IslandTrackSwitch? TrackSwitchFrame(PlaybackSnapshot snapshot, long now)
    {
        if (_switchOutgoing is not { } outgoing)
            return null;
        if (snapshot.Track is null || PlaybackStore.RequiresAttention(snapshot.Status))
        {
            _switchOutgoing = null;
            return null;
        }
        if (_switchStartedAt == 0 && _switchReturningAt == 0)
        {
            // Some players restart the current song for Previous instead of changing its ID.
            if (snapshot.Track.Id != outgoing.Track!.Id
                || _switchDirection < 0 && snapshot.ReportedPositionMs < _switchPositionMs - 1_000)
            {
                _switchStartedAt = now;
                if (_islandPauseTrackId == outgoing.Track.Id && !snapshot.IsPlaying)
                    _islandPauseTrackId = snapshot.Track.Id;
            }
            else if (_switchCompletedAt != 0 && Stopwatch.GetElapsedTime(_switchCompletedAt, now).TotalSeconds >= 3)
            {
                // A successful control response can leave the track unchanged (for example at a queue boundary).
                // Only the visual waiting state ends here; the playback source keeps polling normally.
                _switchReturningAt = now;
            }
        }
        var returning = _switchReturningAt != 0;
        var waiting = _switchStartedAt == 0 && !returning;
        var startedAt = returning ? _switchReturningAt : waiting ? _switchRequestedAt : _switchStartedAt;
        var seconds = Stopwatch.GetElapsedTime(startedAt, now).TotalSeconds;
        if (!waiting && seconds >= (returning ? IslandRenderer.TrackReturnDuration : IslandRenderer.TrackSwitchDuration))
        {
            _switchOutgoing = null;
            return null;
        }
        var feedbackAt = returning ? _switchReturningAt : waiting ? now : _switchStartedAt;
        var feedbackSeconds = Stopwatch.GetElapsedTime(_switchRequestedAt, feedbackAt).TotalSeconds;
        return new IslandTrackSwitch(outgoing, _switchDirection, seconds, waiting, returning,
            Math.Clamp(feedbackSeconds / .09d, 0d, 1d));
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

        var now = Stopwatch.GetTimestamp();
        var trackSwitch = TrackSwitchFrame(snapshot, now);
        var requiresAttention = PlaybackStore.RequiresAttention(snapshot.Status);
        if (_islandPauseTrackId is not null)
        {
            if (requiresAttention || track?.Id != _islandPauseTrackId || snapshot.IsPlaying && _islandPauseObserved)
                ResetIslandPause();
            else if (!snapshot.IsPlaying)
                _islandPauseObserved = true;
        }
        var retainedPause = _islandPauseTrackId is not null && !snapshot.IsPlaying;
        var inactive = !requiresAttention && (track is not null && !snapshot.IsPlaying
            || track is null && PlaybackStore.IsNoActivePlayback(snapshot.Status));
        if (inactive != _inactive)
        {
            _inactive = inactive;
            _inactiveAt = inactive ? now : 0;
        }
        var inactiveSeconds = inactive && _inactiveAt != 0
            ? Stopwatch.GetElapsedTime(_inactiveAt, now).TotalSeconds
            : 0d;
        var capsuleShown = _showIsland && (retainedPause || ShouldShowCapsule(inactive, _pausedDisplay, inactiveSeconds));
        _delayedHidePending = _showIsland && inactive && !retainedPause
            && _pausedDisplay == PausedDisplayMode.HideAfterThreeSeconds
            && inactiveSeconds < 3d;
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

        var sidesShown = !retainedPause;
        if (sidesShown != _sidesShown)
        {
            _sidesShown = sidesShown;
            _sidesFrom = _sidesProgress;
            _sidesTransitionAt = now;
        }
        var sidesSeconds = _sidesTransitionAt == 0
            ? IslandRenderer.CapsuleTransitionDuration
            : Stopwatch.GetElapsedTime(_sidesTransitionAt, now).TotalSeconds;
        _sidesProgress = IslandRenderer.AnimatedCapsuleProgress(
            _sidesFrom, sidesShown ? 1d : 0d, sidesSeconds);
        _sidesTransitioning = sidesSeconds < IslandRenderer.CapsuleTransitionDuration;

        if (snapshot.IsPlaying != _lyricsPlaying)
        {
            _lyricsPlaying = snapshot.IsPlaying;
            _lyricsFrom = _lyricsEmphasis;
            _lyricsTransitionAt = now;
        }
        var lyricsSeconds = _lyricsTransitionAt == 0
            ? IslandRenderer.CapsuleTransitionDuration
            : Stopwatch.GetElapsedTime(_lyricsTransitionAt, now).TotalSeconds;
        _lyricsEmphasis = IslandRenderer.AnimatedCapsuleProgress(
            _lyricsFrom, snapshot.IsPlaying ? 1d : 0d, lyricsSeconds);
        _lyricsTransitioning = lyricsSeconds < IslandRenderer.CapsuleTransitionDuration;

        var frame = new IslandFrame(
            requiresAttention ? null : track,
            requiresAttention ? null : _line,
            requiresAttention ? null : _outgoing,
            position,
            snapshot.IsPlaying,
            snapshot.Status,
            transitionSeconds,
            _scale,
            _capsuleProgress,
            _showTranslation,
            _sidesProgress,
            _lyricsEmphasis,
            trackSwitch);
        _renderedFrame = frame;
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

    internal double CurrentPosition(PlaybackSnapshot snapshot)
    {
        var elapsed = snapshot.IsPlaying
            ? Stopwatch.GetElapsedTime(snapshot.ReportedAtTimestamp).TotalMilliseconds * snapshot.PlaybackRate
            : 0d;
        var trackOffset = snapshot.Track is { } track
            && _trackOffsetsMs?.TryGetValue(track.Id, out var offset) == true
                ? offset
                : 0;
        return ApplyLyricsOffset(snapshot, elapsed, _lyricsOffsetMs + trackOffset);
    }

    internal static double ApplyLyricsOffset(PlaybackSnapshot snapshot, double elapsedMs, int offsetMs) =>
        Math.Clamp(
            snapshot.ReportedPositionMs + elapsedMs + offsetMs,
            0d,
            snapshot.Track is { DurationMs: > 0 } track ? track.DurationMs : double.MaxValue);

    internal static bool ShouldShowCapsule(
        bool inactive,
        PausedDisplayMode mode,
        double inactiveSeconds) => !inactive
            || mode == PausedDisplayMode.KeepVisible
            || mode == PausedDisplayMode.HideAfterThreeSeconds && inactiveSeconds < 3d;

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
        _idleFrame?.Dispose();
        _frameScheduler.Dispose();
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
        if (lease.GrContext is not null) NativeVerticalSync.Apply();
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
    double CapsuleProgress = 1d,
    bool ShowTranslation = true,
    double SidesProgress = 1d,
    double? LyricsEmphasis = null,
    IslandTrackSwitch? TrackSwitch = null)
{
    internal float HighlightEmphasis => (float)Math.Clamp(LyricsEmphasis ?? (IsPlaying ? 1d : 0d), 0d, 1d);
    internal float TextScale => .8f + .2f * HighlightEmphasis;
}

internal sealed record IslandTrackSwitch(
    IslandFrame Outgoing, int Direction, double Seconds, bool Waiting, bool Returning, double FeedbackProgress);

internal sealed class IslandRenderer : IDisposable
{
    public const double TransitionDuration = 0.9;
    public const double CapsuleTransitionDuration = 0.38d;
    public const double TrackSwitchDuration = 0.28d;
    public const double TrackReturnDuration = 0.18d;
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
    private readonly SKPaint _sidesPaint = Paint(SKColors.White);
    private readonly SKPaint _switchPaint = Paint(SKColors.White);
    private IslandFrame? _switchImageFrame;
    private SKImage? _switchImage;
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

    internal void SetBackgroundOpacity(double percent) =>
        _fill.Color = new SKColor(3, 5, 7, BackgroundAlpha(percent));

    internal static byte BackgroundAlpha(double percent) =>
        (byte)Math.Round(255d * SettingsStore.NormalizeBackgroundOpacityPercent(percent) / 100d);

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
        DrawPill(canvas, pillRect);
        if (frame.TrackSwitch is { } trackSwitch)
        {
            DrawTrackSwitch(canvas, width, height, frame, pillRect, trackSwitch);
        }
        else
        {
            _switchImage?.Dispose();
            _switchImage = null;
            _switchImageFrame = null;
            DrawContent(canvas, frame, pillRect);
        }
        canvas.Restore();
    }

    private void DrawContent(SKCanvas canvas, IslandFrame frame, SKRect pillRect)
    {
        var mainFonts = new MainFontSet(_latinFont, _cjkFont, _japaneseFont);
        var pillHeight = pillRect.Height;
        var pillLeft = pillRect.Left;
        var pillTop = pillRect.Top;
        var sidesProgress = (float)Math.Clamp(frame.SidesProgress, 0d, 1d);
        if (sidesProgress > 0f)
        {
            _sidesPaint.Color = SKColors.White.WithAlpha((byte)Math.Round(255f * sidesProgress));
            if (sidesProgress < 1f) canvas.SaveLayer(_sidesPaint);
            else canvas.Save();
            DrawSides(canvas, frame, pillRect);
            canvas.Restore();
        }

        var verticalInset = pillHeight < DoubleLinePillHeight ? 4f : 7f;
        var clip = new SKRect(
            pillLeft + 24f + 43f * sidesProgress, pillTop + verticalInset,
            pillRect.Right - 23f - 43f * sidesProgress, pillRect.Bottom - verticalInset);
        canvas.Save();
        canvas.ClipRect(clip);
        canvas.Translate(clip.MidX, clip.MidY);
        canvas.Scale(frame.TextScale);
        canvas.Translate(-clip.MidX, -clip.MidY);

        if (frame.Outgoing is not null && frame.TransitionSeconds < 0.12)
        {
            var p = Math.Clamp(frame.TransitionSeconds / 0.11, 0d, 1d);
            DrawLineBlock(canvas, frame.Outgoing, frame.PositionMs, clip, mainFonts, _translationFont,
                1f - 0.25f * (float)EaseOutCubic(p), (float)(1d - EaseOutCubic(p)), frame.ShowTranslation,
                frame.HighlightEmphasis);
        }

        var incomingStart = frame.Outgoing is null ? 0d : 0.30d;
        if (frame.Line is not null && frame.TransitionSeconds >= incomingStart)
        {
            var p = frame.Outgoing is null
                ? 1d
                : Math.Clamp((frame.TransitionSeconds - incomingStart) / 0.60d, 0d, 1d);
            var scale = frame.Outgoing is null ? 1f : (float)(0.75d + 0.25d * Spring(p));
            DrawLineBlock(canvas, frame.Line, frame.PositionMs, clip, mainFonts, _translationFont,
                scale, (float)EaseOutCubic(p), frame.ShowTranslation, frame.HighlightEmphasis);
        }
        else if (frame.Line is null)
        {
            DrawStatus(canvas, frame, clip, mainFonts, _translationFont);
        }

        canvas.Restore();
    }

    private void DrawTrackSwitch(
        SKCanvas canvas, float width, float height, IslandFrame frame, SKRect pill, IslandTrackSwitch transition)
    {
        var captureScale = Math.Max(1f, Math.Abs(canvas.TotalMatrix.ScaleX));
        var imageWidth = (int)Math.Ceiling(width * captureScale);
        var imageHeight = (int)Math.Ceiling(height * captureScale);
        if (!ReferenceEquals(_switchImageFrame, transition.Outgoing)
            || _switchImage?.Width != imageWidth || _switchImage.Height != imageHeight)
        {
            _switchImage?.Dispose();
            using var surface = SKSurface.Create(new SKImageInfo(imageWidth, imageHeight));
            surface.Canvas.Clear(SKColors.Transparent);
            surface.Canvas.Scale(captureScale);
            var outgoingPill = PillRect(width, height, transition.Outgoing,
                new MainFontSet(_latinFont, _cjkFont, _japaneseFont), _translationFont);
            DrawContent(surface.Canvas, transition.Outgoing, outgoingPill);
            _switchImage = surface.Snapshot();
            _switchImageFrame = transition.Outgoing;
        }

        var feedback = (float)SmoothStep(transition.FeedbackProgress);
        var offset = -transition.Direction * 6f * feedback;
        var opacity = 1f - .28f * feedback;
        if (transition.Returning)
        {
            var remaining = 1f - (float)SmoothStep(Math.Clamp(transition.Seconds / TrackReturnDuration, 0d, 1d));
            offset *= remaining;
            opacity = 1f - .28f * feedback * remaining;
        }
        else if (!transition.Waiting)
        {
            var progress = (float)EaseOutCubic(Math.Clamp(transition.Seconds / .16d, 0d, 1d));
            offset = Lerp(offset, -transition.Direction * 24f, progress);
            opacity *= 1f - progress;
        }

        canvas.Save();
        canvas.ClipRoundRect(new SKRoundRect(pill, pill.Height / 2f), SKClipOperation.Intersect, true);
        _switchPaint.Color = SKColors.White.WithAlpha((byte)Math.Round(255f * opacity));
        canvas.DrawImage(_switchImage!, new SKRect(0, offset, width, height + offset),
            new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), _switchPaint);
        if (!transition.Waiting && !transition.Returning)
        {
            var progress = Math.Clamp(transition.Seconds / TrackSwitchDuration, 0d, 1d);
            var eased = (float)EaseOutCubic(progress);
            var incomingOffset = transition.Direction * 24f * (1f - eased);
            var incomingScale = .94f + .06f * (float)Spring(progress);
            _switchPaint.Color = SKColors.White.WithAlpha((byte)Math.Round(255f * eased));
            canvas.SaveLayer(_switchPaint);
            canvas.Translate(pill.MidX, pill.MidY + incomingOffset);
            canvas.Scale(incomingScale);
            canvas.Translate(-pill.MidX, -pill.MidY);
            DrawContent(canvas, frame with { Outgoing = null, TransitionSeconds = 99d, TrackSwitch = null }, pill);
            canvas.Restore();
        }
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

    private SKRect PillRect(
        float width,
        float height,
        IslandFrame frame,
        MainFontSet mainFonts,
        SKFont translationFont)
    {
        var settledWidth = DesiredWidth(
            frame.Track, frame.Line, frame.Status, mainFonts, translationFont, frame.ShowTranslation,
            frame.TextScale);
        var outgoingWidth = DesiredWidth(
            frame.Track, frame.Outgoing, frame.Status, mainFonts, translationFont, frame.ShowTranslation,
            frame.TextScale);
        var sideInset = (float)(86d * (1d - Math.Clamp(frame.SidesProgress, 0d, 1d)));
        var pillWidth = Math.Min(width - 16f,
            AnimatedWidth(outgoingWidth, settledWidth, frame.TransitionSeconds)) - sideInset;
        var pillHeight = AnimatedHeight(
            DesiredPillHeight(frame.Outgoing ?? frame.Line, frame.ShowTranslation),
            DesiredPillHeight(frame.Line, frame.ShowTranslation),
            frame.TransitionSeconds) * frame.TextScale;
        if (frame.TrackSwitch is { } trackSwitch)
        {
            var outgoing = PillRect(width, height, trackSwitch.Outgoing,
                mainFonts, translationFont);
            var progress = trackSwitch.Waiting || trackSwitch.Returning
                ? 0f
                : (float)EaseOutCubic(Math.Clamp(trackSwitch.Seconds / TrackSwitchDuration, 0d, 1d));
            pillWidth = Lerp(outgoing.Width, pillWidth, progress);
            pillHeight = Lerp(outgoing.Height, pillHeight, progress);
        }
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
        float opacity,
        bool showTranslation,
        float highlightEmphasis)
    {
        if (opacity <= 0.001f || scale <= 0.001f)
            return;

        var layout = LayoutLine(line, mainFonts);
        var mainWidth = layout.Width;
        var translation = showTranslation ? line.Translation ?? string.Empty : string.Empty;
        var hasTranslation = !string.IsNullOrWhiteSpace(translation);
        var translationWidth = hasTranslation ? layout.TranslationWidth : 0f;
        var centerX = clip.MidX;
        var originY = clip.MidY + (hasTranslation ? DoubleLineOriginOffset : 1f);

        canvas.Save();
        canvas.Translate(centerX, originY);
        canvas.Scale(scale, 1f);
        canvas.Translate(-centerX, -originY);

        var mainX = centerX - mainWidth / 2f;
        var mainBaseline = hasTranslation ? originY - 5f : CenteredBaseline(originY, layout.Runs, mainFonts.Latin) - 2f;
        DrawKaraokeText(canvas, line, positionMs, mainX, mainBaseline, layout, opacity, highlightEmphasis);

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
        float opacity,
        float highlightEmphasis)
    {
        var activeWidth = ActiveWidth(line, positionMs, layout);
        _idlePaint.Color = new SKColor(108, 109, 114, (byte)(225 * opacity));
        if (activeWidth <= 0f || highlightEmphasis <= 0f)
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
            var glyphBaseline = baseline - 4f * liftProgress * highlightEmphasis;
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
                _edgeBloom.Color = new SKColor(255, 255, 255, (byte)(51f * glowProgress * opacity * highlightEmphasis));
                canvas.DrawPath(glyph.Path, _edgeBloom);
            }
            _wideGlow.Color = new SKColor(255, 255, 255, (byte)(42 * opacity * highlightEmphasis));
            canvas.DrawPath(glyph.Path, _wideGlow);
            _tightGlow.Color = new SKColor(255, 255, 255, (byte)(70 * opacity * highlightEmphasis));
            canvas.DrawPath(glyph.Path, _tightGlow);
            _corePaint.Color = new SKColor(
                (byte)(108 + 139 * highlightEmphasis),
                (byte)(109 + 139 * highlightEmphasis),
                (byte)(114 + 135 * highlightEmphasis),
                (byte)((225 + 30 * highlightEmphasis) * opacity));
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
                _edgeBloom.Color = new SKColor(255, 255, 255, (byte)(78 * opacity * highlightEmphasis));
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

    private static float ActiveWidth(LyricLine line, double positionMs, KaraokeLayout layout)
    {
        if (positionMs <= line.StartMs)
            return 0f;

        if (layout.Syllables.Length == 0)
        {
            var duration = Math.Max(1, line.EndMs - line.StartMs);
            var p = Math.Clamp((positionMs - line.StartMs) / (double)duration, 0d, 1d);
            return layout.Width * (float)SmoothStep(p);
        }

        foreach (var syllable in layout.Syllables)
        {
            if (positionMs < syllable.StartMs)
                return syllable.PrefixWidth;

            if (positionMs <= syllable.EndMs)
            {
                var duration = Math.Max(1, syllable.EndMs - syllable.StartMs);
                var p = Math.Clamp((positionMs - syllable.StartMs) / (double)duration, 0d, 1d);
                return syllable.PrefixWidth + syllable.Width * (float)SmoothStep(p);
            }
        }
        return layout.Width;
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
        var title = frame.Track?.Title ?? frame.Status ?? "等待播放信息";
        var subtitle = StatusSubtitle(frame.Track, frame.Status);
        var titleLayout = LayoutMainText(title, mainFonts);
        var subtitleWidth = translationFont.MeasureText(subtitle);
        var originY = clip.MidY + DoubleLineOriginOffset;
        DrawMainText(canvas, titleLayout.Runs, clip.MidX - titleLayout.Width / 2f, originY - 5f, _titlePaint);
        canvas.DrawText(subtitle, clip.MidX - subtitleWidth / 2f, originY + 24f, translationFont, _subtitlePaint);
    }

    private float DesiredWidth(
        TrackInfo? track,
        LyricLine? line,
        string? status,
        MainFontSet mainFonts,
        SKFont translationFont,
        bool showTranslation,
        float textScale)
    {
        float mainWidth;
        float subWidth;
        if (line is not null)
        {
            var layout = LayoutLine(line, mainFonts);
            mainWidth = layout.Width;
            subWidth = showTranslation ? layout.TranslationWidth : 0f;
        }
        else
        {
            var main = track?.Title ?? status ?? "等待播放信息";
            var sub = StatusSubtitle(track, status);
            mainWidth = MeasureMainText(main, mainFonts);
            subWidth = translationFont.MeasureText(sub);
        }
        var content = Math.Max(mainWidth, subWidth) * textScale;
        return Math.Max(content + 146f, 390f);
    }

    private static string StatusSubtitle(TrackInfo? track, string? status)
    {
        if (track is not null)
            return status ?? string.Join(", ", track.Artists);
        if (status == PlaybackStore.MissingSpotifyCredentialsStatus)
            return "托盘菜单 → 设置 → 播放信息源";
        return status?.Contains("MPRIS", StringComparison.Ordinal) == true
            ? "读取本机播放器的 MPRIS 信息"
            : "首次使用 Spotify 会打开浏览器授权";
    }

    internal static float DesiredPillHeight(LyricLine? line, bool showTranslation = true) =>
        line is not null && (!showTranslation || string.IsNullOrWhiteSpace(line.Translation))
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
        var translationWidth = string.IsNullOrWhiteSpace(line.Translation)
            ? 0f
            : _translationFont.MeasureText(line.Translation);
        return _karaokeLayout1 = new KaraokeLayout(
            runs,
            BuildGlyphs(runs, line, width),
            BuildSyllables(line, fonts),
            width,
            translationWidth);
    }

    private static KaraokeSyllable[] BuildSyllables(LyricLine line, MainFontSet fonts)
    {
        if (line.Syllables.IsDefaultOrEmpty)
            return [];

        var japanese = line.Text.EnumerateRunes().Any(IsKana);
        var syllables = new KaraokeSyllable[line.Syllables.Length];
        var builder = new StringBuilder(line.Text.Length);
        var prefixWidth = 0f;
        for (var i = 0; i < line.Syllables.Length; i++)
        {
            var syllable = line.Syllables[i];
            var width = MeasureMainText(syllable.Text, fonts, japanese);
            syllables[i] = new KaraokeSyllable(syllable.StartMs, syllable.EndMs, prefixWidth, width);
            builder.Append(syllable.Text);
            prefixWidth = MeasureMainText(builder.ToString(), fonts, japanese);
        }
        return syllables;
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
        _sidesPaint.Dispose();
        _switchPaint.Dispose();
        _switchImage?.Dispose();
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

    private sealed record KaraokeLayout(
        List<MainTextRun> Runs,
        List<KaraokeGlyph> Glyphs,
        KaraokeSyllable[] Syllables,
        float Width,
        float TranslationWidth) : IDisposable
    {
        public void Dispose()
        {
            foreach (var glyph in Glyphs)
                glyph.Path.Dispose();
        }
    }

    private readonly record struct KaraokeSyllable(long StartMs, long EndMs, float PrefixWidth, float Width);

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
