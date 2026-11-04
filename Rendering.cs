using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Threading;

namespace LyricifyIsland;

internal enum VerticalSyncMode { Disabled, Enabled, Adaptive }

internal static class RenderScheduling
{
    internal sealed record Configuration(int MaximumFrameRate, VerticalSyncMode VerticalSync);
    private static Configuration _configuration = new(0, VerticalSyncMode.Enabled);
    internal static Configuration Current => Volatile.Read(ref _configuration);

    public static void Initialize(IslandSettings settings) => Apply(settings);

    public static void Apply(IslandSettings settings) =>
        Volatile.Write(ref _configuration, new Configuration(settings.MaximumFrameRate, settings.VerticalSync));
}

internal sealed class FrameScheduler : IDisposable
{
    private readonly Timer _timer;
    private Action<TimeSpan>? _callback;
    private long _deadline;
    private int _frameRate;

    public FrameScheduler()
    {
        // Wake the UI dispatcher even when the Wayland compositor has no frame callback pending.
        _timer = new Timer(static state =>
        {
            var scheduler = (FrameScheduler)state!;
            Dispatcher.UIThread.Post(scheduler.OnTimer, DispatcherPriority.Render);
        }, this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    private void OnTimer()
    {
        var callback = _callback;
        _callback = null;
        callback?.Invoke(default);
    }

    public void Request(TopLevel topLevel, Action<TimeSpan> callback)
    {
        var fps = RenderScheduling.Current.MaximumFrameRate;
        if (fps == 0)
        {
            if (_frameRate != 0)
                _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _callback = null;
            _deadline = 0;
            _frameRate = 0;
            topLevel.RequestAnimationFrame(callback);
            return;
        }

        var now = Stopwatch.GetTimestamp();
        var interval = (long)Math.Round((double)Stopwatch.Frequency / fps);
        if (_frameRate != fps || _deadline == 0 || now - _deadline >= interval)
            _deadline = now + interval;
        else
            _deadline += interval;
        _frameRate = fps;
        var delay = TimeSpan.FromSeconds(Math.Max(0, _deadline - now) / (double)Stopwatch.Frequency);
        _callback = callback;
        _timer.Change(delay, Timeout.InfiniteTimeSpan);
    }

    public void Dispose()
    {
        _callback = null;
        _timer.Dispose();
    }
}

internal static class NativeVerticalSync
{
    private const int EglDraw = 0x3059;
    private const int EglExtensions = 0x3055;
    private static readonly Dictionary<(nint Display, nint Surface), VerticalSyncMode> Applied = [];
    private static bool _unavailable;

    // Avalonia invokes the draw operation with its EGL context current.
    public static void Apply()
    {
        if (!OperatingSystem.IsLinux() || _unavailable)
            return;
        try
        {
            var display = eglGetCurrentDisplay();
            var surface = eglGetCurrentSurface(EglDraw);
            if (display == 0 || surface == 0)
                return;
            var requested = RenderScheduling.Current.VerticalSync;
            var key = (display, surface);
            if (Applied.TryGetValue(key, out var previous) && previous == requested)
                return;
            var extensions = " " + Marshal.PtrToStringAnsi(eglQueryString(display, EglExtensions)) + " ";
            var adaptive = extensions.Contains(" EGL_EXT_swap_control_tear ", StringComparison.Ordinal);
            var interval = requested == VerticalSyncMode.Disabled ? 0
                : requested == VerticalSyncMode.Adaptive && adaptive ? -1 : 1;
            var applied = eglSwapInterval(display, interval) != 0;
            Console.Error.WriteLine($"[render] EGL sync: {requested}, interval={interval}, applied={applied}");
            if (Applied.Count >= 64) Applied.Clear();
            Applied[key] = requested;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            _unavailable = true;
            Console.Error.WriteLine("[render] EGL swap control is unavailable; using compositor synchronization.");
        }
    }

    [DllImport("libEGL.so.1")] private static extern nint eglGetCurrentDisplay();
    [DllImport("libEGL.so.1")] private static extern nint eglGetCurrentSurface(int readdraw);
    [DllImport("libEGL.so.1")] private static extern nint eglQueryString(nint display, int name);
    [DllImport("libEGL.so.1")] private static extern int eglSwapInterval(nint display, int interval);
}
