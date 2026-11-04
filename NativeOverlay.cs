using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using NWayland.Protocols.ExtBackgroundEffectV1;
using NWayland.Protocols.Wayland;
using NWayland.Protocols.XdgShell;

namespace LyricifyIsland;

internal sealed class NativeOverlay : IDisposable
{
    private const BindingFlags InstanceMembers = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private sealed record State(PixelRect InputBounds, bool InputTransparent, double? CornerRadius, PixelRect BlurBounds, bool Blur);
    private sealed class BlurListener : ExtBackgroundEffectManagerV1.Listener
    {
        protected override void Capabilities(ExtBackgroundEffectManagerV1 sender, ExtBackgroundEffectManagerV1.CapabilityEnum flags) { }
    }

    private readonly Window _window;
    private readonly object _target;
    private readonly PropertyInfo _surfaceProperty;
    private readonly PropertyInfo _globalsProperty;
    private readonly FieldInfo _topLevelField;
    private readonly Action<Action> _post;
    private readonly DispatcherTimer _reconnectTimer;
    private State _state = new(default, false, null, default, false);
    private int _queued;
    private bool _disposed;

    // The following fields are accessed only on Avalonia's Wayland worker thread.
    private object? _globals;
    private WlSurface? _surface;
    private WlCompositor? _compositor;
    private ExtBackgroundEffectManagerV1? _manager;
    private ExtBackgroundEffectSurfaceV1? _effect;
    private bool _checkedManager;
    private State? _applied;

    private NativeOverlay(Window window, object target, object worker, Type surfaceType)
    {
        _window = window;
        _target = target;
        _surfaceProperty = surfaceType.GetProperty("WlSurface", InstanceMembers)!;
        _globalsProperty = surfaceType.GetProperty("Globals", InstanceMembers)!;
        _topLevelField = target.GetType().GetField("_xdgTopLevel", InstanceMembers)!;
        _post = worker.GetType().GetMethod("PostOob", InstanceMembers)!
            .CreateDelegate<Action<Action>>(worker);
        _reconnectTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _reconnectTimer.Tick += (_, _) => QueueUpdate();
        _reconnectTimer.Start();
    }

    public static NativeOverlay? TryCreate(Window window)
    {
        if (!OperatingSystem.IsLinux())
            return null;

        // Avalonia 12.1.1 does not expose its Wayland surface publicly. Access it on
        // Avalonia's own worker thread and keep this adapter tied to that version.
        var implementation = window.PlatformImpl!;
        var proxy = implementation.GetType().GetField("_surfaceProxy", InstanceMembers)!.GetValue(implementation)!;
        var target = proxy.GetType().GetProperty("ProxyTarget", InstanceMembers | BindingFlags.DeclaredOnly)!.GetValue(proxy)!;
        var surfaceType = target.GetType();
        while (surfaceType.Name != "WSurface")
            surfaceType = surfaceType.BaseType!;
        var worker = surfaceType.GetProperty("Worker", InstanceMembers)!.GetValue(target)!;
        return new NativeOverlay(window, target, worker, surfaceType);
    }

    public void SetInputRegion(Rect logicalBounds, double? cornerRadius = null)
    {
        _state = _state with
        {
            InputBounds = LogicalBounds(logicalBounds),
            InputTransparent = false,
            CornerRadius = cornerRadius
        };
        QueueUpdate();
    }

    public void SetIslandRegion(Rect logicalBounds, bool inputTransparent, bool blur)
    {
        var bounds = LogicalBounds(logicalBounds);
        _state = new State(bounds, inputTransparent, null, bounds, blur);
        QueueUpdate();
    }

    private void QueueUpdate()
    {
        if (_disposed || Interlocked.Exchange(ref _queued, 1) != 0)
            return;
        _post(() =>
        {
            Interlocked.Exchange(ref _queued, 0);
            ApplyOnWorker(Volatile.Read(ref _state));
        });
    }

    private void ApplyOnWorker(State state)
    {
        var surface = (WlSurface?)_surfaceProperty.GetValue(_target);
        var globals = _globalsProperty.GetValue(_target);
        if (surface is null || globals is null)
        {
            _surface = null;
            _globals = null;
            _compositor = null;
            _manager = null;
            _effect = null;
            _checkedManager = false;
            _applied = null;
            return;
        }

        if (!ReferenceEquals(surface, _surface))
        {
            _surface = surface;
            _globals = globals;
            var type = globals.GetType();
            _compositor = (WlCompositor)type.GetProperty("WlCompositor", InstanceMembers)!.GetValue(globals)!;
            ((XdgToplevel)_topLevelField.GetValue(_target)!).SetAppId("lyricify-island");
            _manager = null;
            _effect = null;
            _checkedManager = false;
            _applied = null;
        }

        if (state == _applied)
            return;

        if (_applied is null || state.InputBounds != _applied.InputBounds
            || state.InputTransparent != _applied.InputTransparent || state.CornerRadius != _applied.CornerRadius)
        {
            var input = _compositor!.CreateRegion();
            if (!state.InputTransparent)
            {
                var rows = state.CornerRadius is { } radius
                    ? RoundedRows(state.InputBounds, radius)
                    : CapsuleRows(state.InputBounds);
                AddRows(input, rows);
            }
            surface.SetInputRegion(input);
            input.Destroy();
        }

        if (state.Blur && !_checkedManager)
        {
            var bind = globals.GetType().GetMethod("Bind", InstanceMembers)!
                .MakeGenericMethod(typeof(ExtBackgroundEffectManagerV1));
            _manager = (ExtBackgroundEffectManagerV1?)bind.Invoke(globals, [1u, 1u, new BlurListener()]);
            _checkedManager = true;
            if (_manager is null)
                Console.Error.WriteLine("[wayland] The compositor does not support ext-background-effect-v1.");
        }
        if (_manager is not null && (_applied is null || state.Blur != _applied.Blur
                                    || state.Blur && state.BlurBounds != _applied.BlurBounds))
        {
            if (state.Blur)
            {
                _effect ??= _manager.GetBackgroundEffect(surface);
                var region = _compositor!.CreateRegion();
                AddRows(region, CapsuleRows(state.BlurBounds));
                _effect.SetBlurRegion(region);
                region.Destroy();
            }
            else if (_effect is not null)
                _effect.SetBlurRegion(null!);
        }
        _applied = state;
        // Both regions are double-buffered; let Avalonia perform the next surface commit.
        Dispatcher.UIThread.Post(_window.InvalidateVisual);
    }

    private static void AddRows(WlRegion region, PixelRect[] rows)
    {
        for (var i = 0; i < rows.Length;)
        {
            var first = rows[i++];
            var height = 1;
            while (i < rows.Length && rows[i].X == first.X && rows[i].Width == first.Width)
            {
                height++;
                i++;
            }
            region.Add(first.X, first.Y, first.Width, height);
        }
    }

    private static PixelRect LogicalBounds(Rect bounds)
    {
        var left = (int)Math.Floor(bounds.Left);
        var top = (int)Math.Floor(bounds.Top);
        var right = (int)Math.Ceiling(bounds.Right);
        var bottom = (int)Math.Ceiling(bounds.Bottom);
        return new PixelRect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    internal static PixelRect[] CapsuleRows(PixelRect bounds) => RoundedRows(bounds, Math.Min(bounds.Width, bounds.Height) / 2d);

    private static PixelRect[] RoundedRows(PixelRect bounds, double radius)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return [];
        radius = Math.Clamp(radius, 0, Math.Min(bounds.Width, bounds.Height) / 2d);
        var rows = new PixelRect[bounds.Height];
        for (var row = 0; row < rows.Length; row++)
        {
            var dy = Math.Max(0, radius - Math.Min(row + .5, bounds.Height - row - .5));
            var inset = (int)Math.Ceiling(radius - Math.Sqrt(Math.Max(0, radius * radius - dy * dy)) - .5);
            rows[row] = new PixelRect(bounds.X + inset, bounds.Y + row, Math.Max(1, bounds.Width - inset * 2), 1);
        }
        return rows;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _reconnectTimer.Stop();
        _post(() =>
        {
            if (ReferenceEquals(_globals, _globalsProperty.GetValue(_target)))
            {
                _effect?.Destroy();
                _manager?.Destroy();
            }
            _effect = null;
            _manager = null;
        });
    }
}
