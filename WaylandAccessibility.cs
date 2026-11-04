using System.Reflection;
using Avalonia.Automation.Peers;
using Avalonia.Controls;

namespace LyricifyIsland;

internal sealed class WaylandAccessibility
{
    private readonly List<Window> _windows = [];
    private readonly Dictionary<Window, AutomationPeer> _peers = [];
    private object? _server;
    private MethodInfo? _addWindow;
    private MethodInfo? _removeWindow;

    public void Track(Window window)
    {
        _windows.Add(window);
        window.Opened += (_, _) => AddWindow(window);
        window.Closed += (_, _) =>
        {
            _windows.Remove(window);
            if (_peers.Remove(window, out var peer))
                _removeWindow!.Invoke(_server, [peer]);
        };
    }

    public async Task StartAsync()
    {
        try
        {
            // Avalonia 12.1.1 only wires its AT-SPI server to the X11 backend.
            var type = Assembly.Load("Avalonia.FreeDesktop.AtSpi").GetType("Avalonia.FreeDesktop.AtSpi.AtSpiServer", true)!;
            var server = Activator.CreateInstance(type, nonPublic: true)!;
            await (Task)type.GetMethod("StartAsync")!.Invoke(server, null)!;
            _server = server;
            _addWindow = type.GetMethod("AddWindow")!;
            _removeWindow = type.GetMethod("RemoveWindow")!;
            foreach (var window in _windows.Where(window => window.IsVisible))
                AddWindow(window);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[accessibility] AT-SPI unavailable: {exception.Message}");
        }
    }

    private void AddWindow(Window window)
    {
        if (_server is null || _peers.ContainsKey(window))
            return;
        var peer = ControlAutomationPeer.CreatePeerForElement(window);
        if (peer is null)
            return;
        peer = peer.GetAutomationRoot() ?? peer;
        _addWindow!.Invoke(_server, [peer]);
        _peers.Add(window, peer);
    }

    public void Stop()
    {
        if (_server is IAsyncDisposable disposable)
            _ = disposable.DisposeAsync();
    }
}
