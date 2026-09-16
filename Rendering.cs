using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Rendering;

namespace LyricifyIsland;

internal enum VerticalSyncMode { Disabled, Enabled, Adaptive }

internal static class RenderScheduling
{
    internal sealed record Configuration(int MaximumFrameRate, VerticalSyncMode VerticalSync);
    private static Configuration _configuration = new(0, VerticalSyncMode.Enabled);
    private static SleepLoopRenderTimer? _timer;
    private static int _displayFrameRate = 60;
    internal static Configuration Current => Volatile.Read(ref _configuration);

    public static void Initialize(IslandSettings settings)
    {
        // X11 constructs its compositor before AfterPlatformServicesSetup. Reuse that compositor's
        // clock instead of replacing the service with a second, disconnected render loop.
        // Timer is internal in the pinned Avalonia 12.1.1 DefaultRenderLoop implementation.
        var loop = AvaloniaLocator.Current.GetRequiredService<IRenderLoop>();
        _timer = loop.GetType().GetProperty("Timer", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(loop) as SleepLoopRenderTimer;
        if (_timer is null)
            throw new NotSupportedException("The current Avalonia render loop does not expose the expected X11 frame timer.");
        _displayFrameRate = _timer.DesiredFps;
        Apply(settings);
    }

    public static void Apply(IslandSettings settings)
    {
        Volatile.Write(ref _configuration, new Configuration(settings.MaximumFrameRate, settings.VerticalSync));
        Refresh();
    }

    public static void DisplayChanged()
    {
        // Avalonia updates its screen rate before forwarding Screens.Changed to the app.
        if (_timer is { DesiredFps: >= 1 and <= 1_000 } timer) _displayFrameRate = timer.DesiredFps;
        Refresh();
    }

    internal static void Refresh()
    {
        if (_timer is not { } timer) return;
        var settings = Current;
        var fps = settings.MaximumFrameRate == 0 ? int.MaxValue : settings.MaximumFrameRate;
        // Pace synchronized updates at the screen rate, including frames that have nothing to present.
        if (settings.VerticalSync != VerticalSyncMode.Disabled)
            fps = Math.Min(fps, _displayFrameRate);
        timer.DesiredFps = fps;
    }

    public static void Stop()
    {
        if (_timer is { } timer) timer.Tick = null;
    }
}

internal static class NativeVerticalSync
{
    private sealed record Capabilities(SwapIntervalExt? Ext, SwapIntervalMesa? Mesa, bool Adaptive);
    private static readonly object Gate = new();
    private static readonly Dictionary<nint, Capabilities> Displays = [];
    private static readonly Dictionary<(nint Display, nuint Drawable, nint Context), VerticalSyncMode> Applied = [];
    private static bool _unavailable;
    private static bool _supported;
    internal static bool Supported => Volatile.Read(ref _supported);

    // Called while Avalonia's window GL context is current, before its rendering session swaps buffers.
    public static void Apply()
    {
        if (!OperatingSystem.IsLinux()) return;
        lock (Gate)
        {
            if (_unavailable) return;
            try
            {
                var context = glXGetCurrentContext();
                if (context == 0) return;
                var display = glXGetCurrentDisplay();
                var drawable = glXGetCurrentDrawable();
                if (display == 0 || drawable == 0) return;
                var key = (display, drawable, context);
                var requested = RenderScheduling.Current.VerticalSync;
                if (Applied.TryGetValue(key, out var previous) && previous == requested) return;

                if (!Displays.TryGetValue(display, out var capabilities))
                {
                    var extensions = " " + Marshal.PtrToStringAnsi(glXQueryExtensionsString(display, XDefaultScreen(display))) + " ";
                    bool Has(string extension) => extensions.Contains(" " + extension + " ", StringComparison.Ordinal);
                    capabilities = new Capabilities(
                        Has("GLX_EXT_swap_control") ? Function<SwapIntervalExt>("glXSwapIntervalEXT") : null,
                        Has("GLX_MESA_swap_control") ? Function<SwapIntervalMesa>("glXSwapIntervalMESA") : null,
                        Has("GLX_EXT_swap_control_tear"));
                    Displays[display] = capabilities;
                }
                var interval = requested == VerticalSyncMode.Disabled ? 0
                    : requested == VerticalSyncMode.Adaptive && capabilities.Adaptive && capabilities.Ext is not null ? -1 : 1;
                if (capabilities.Ext is { } ext)
                {
                    ext(display, drawable, interval);
                    glXQueryDrawable(display, drawable, 0x20F1 /* GLX_SWAP_INTERVAL_EXT */, out var actual);
                    Volatile.Write(ref _supported, interval == 0 || actual > 0);
                    var adaptive = 0u;
                    if (capabilities.Adaptive)
                        glXQueryDrawable(display, drawable, 0x20F3 /* GLX_LATE_SWAPS_TEAR_EXT */, out adaptive);
                    Console.Error.WriteLine($"[render] GLX sync: {requested}, interval={actual}, adaptive={adaptive}");
                }
                else if (capabilities.Mesa is { } mesa)
                {
                    Volatile.Write(ref _supported, mesa((uint)Math.Max(0, interval)) == 0);
                    Console.Error.WriteLine($"[render] MESA sync: {requested}, interval={Math.Max(0, interval)}, applied={Supported}");
                }
                else
                {
                    Volatile.Write(ref _supported, false);
                    Console.Error.WriteLine("[render] No GLX swap control; synchronized modes use the display refresh rate timer.");
                }
                RenderScheduling.Refresh();
                // Window handles can change when windows close and reopen.
                if (Applied.Count >= 64) Applied.Clear();
                Applied[key] = requested;
            }
            catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                _unavailable = true;
                Volatile.Write(ref _supported, false);
                Console.Error.WriteLine("[render] GLX swap control is unavailable; keeping compositor synchronization.");
            }
        }
    }

    private static T? Function<T>(string name) where T : Delegate
    {
        var address = glXGetProcAddressARB(name);
        return address == 0 ? null : Marshal.GetDelegateForFunctionPointer<T>(address);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SwapIntervalExt(nint display, nuint drawable, int interval);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SwapIntervalMesa(uint interval);

    [DllImport("libGL.so.1")] private static extern nint glXGetCurrentContext();
    [DllImport("libGL.so.1")] private static extern nint glXGetCurrentDisplay();
    [DllImport("libGL.so.1")] private static extern nuint glXGetCurrentDrawable();
    [DllImport("libGL.so.1")] private static extern nint glXQueryExtensionsString(nint display, int screen);
    [DllImport("libGL.so.1")] private static extern void glXQueryDrawable(nint display, nuint drawable, int attribute, out uint value);
    [DllImport("libGL.so.1")] private static extern nint glXGetProcAddressARB([MarshalAs(UnmanagedType.LPStr)] string name);
    [DllImport("libX11.so.6")] private static extern int XDefaultScreen(nint display);
}
