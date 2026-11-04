using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Threading;

namespace LyricifyIsland;

internal enum PanelKind { Settings, Headphones, Banner }

/// <summary>A page shown inside the lyrics window's panel.</summary>
internal abstract class PanelPage : UserControl
{
    /// <summary>Called every time the page is brought to the front.</summary>
    public virtual void OnShown() { }

    /// <summary>Elements that glide in one after another when the page appears.</summary>
    public virtual IReadOnlyList<Control> EntranceTargets => [];
}

/// <summary>Small helpers for the fade-and-glide motion used by the panel.</summary>
internal static class Motion
{
    public static readonly TimeSpan Enter = TimeSpan.FromMilliseconds(340);
    public static readonly TimeSpan Leave = TimeSpan.FromMilliseconds(220);

    private static TransformOperations Offset(int x, int y) => TransformOperations.Parse($"translate({x}px, {y}px)");

    private static Transitions Make(TimeSpan duration, TimeSpan delay, Easing easing) =>
    [
        new DoubleTransition { Property = Visual.OpacityProperty, Duration = duration, Delay = delay, Easing = easing },
        new TransformOperationsTransition { Property = Visual.RenderTransformProperty, Duration = duration, Delay = delay, Easing = easing }
    ];

    /// <summary>Starts offset and transparent, then eases to rest.</summary>
    public static void In(Control control, int fromX, int fromY, TimeSpan? duration = null, int delayMs = 0)
    {
        control.Transitions = null;
        control.Opacity = 0;
        control.RenderTransform = Offset(fromX, fromY);
        Dispatcher.UIThread.Post(() =>
        {
            control.Transitions = Make(duration ?? Enter, TimeSpan.FromMilliseconds(delayMs), new CubicEaseOut());
            control.Opacity = 1;
            control.RenderTransform = Offset(0, 0);
        }, DispatcherPriority.Render);
    }

    /// <summary>Eases to transparent while drifting by the given offset.</summary>
    public static void Out(Control control, int toX, int toY, TimeSpan? duration = null)
    {
        control.Transitions = Make(duration ?? Leave, TimeSpan.Zero, new CubicEaseIn());
        control.Opacity = 0;
        control.RenderTransform = Offset(toX, toY);
    }

    public static void Stagger(IReadOnlyList<Control> targets, int stepMs = 45, int fromY = 18)
    {
        for (var i = 0; i < targets.Count; i++)
            In(targets[i], 0, fromY, TimeSpan.FromMilliseconds(420), Math.Min(i * stepMs, 360));
    }
}

/// <summary>
/// Hosts the settings, headphone and song-sharing pages on top of the lyrics window. The lyrics renderer keeps drawing its own
/// cover-derived backdrop underneath (dimmed), so the panel inherits the main page's colours and size.
/// </summary>
internal sealed class PanelHost : Grid
{
    private readonly Func<PanelPage> _createSettings;
    private readonly Func<PanelPage> _createHeadphones;
    private readonly Func<PanelPage> _createBanner;
    private readonly Grid _content = new();
    private readonly Grid _pages = new();
    private readonly Segmented<PanelKind> _tabs = new("页面");
    private readonly Dictionary<PanelKind, PanelPage> _created = [];
    private PanelKind? _current;
    private bool _open;

    public PanelHost(Func<PanelPage> createSettings, Func<PanelPage> createHeadphones, Func<PanelPage> createBanner)
    {
        _createSettings = createSettings;
        _createHeadphones = createHeadphones;
        _createBanner = createBanner;
        IsVisible = false;
        Background = Brushes.Transparent;
        // Keep the host's hit-test bounds below the header and inside the resize ring.
        Margin = new Thickness(22, 62, 22, 22);
        ClipToBounds = true;
        Focusable = true;
        TextElement.SetForeground(this, LyricsTheme.PrimaryText);
        TextElement.SetFontFamily(this, new FontFamily("Noto Sans CJK SC"));
        TextElement.SetFontSize(this, 14);
        PanelTheme.Apply(this);

        _tabs.Set([(PanelKind.Settings, "设置"), (PanelKind.Headphones, "耳机控制"), (PanelKind.Banner, "歌曲分享图")], kind => kind == _current);
        _tabs.Changed += kind => Open(kind);
        var back = Ui.Button("返回歌词");
        back.Click += (_, _) => Close();
        var bar = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            Margin = new Thickness(28, 6, 28, 10),
            Children = { _tabs, back }
        };
        _tabs.HorizontalAlignment = HorizontalAlignment.Left;
        Grid.SetColumn(back, 2);

        _content.RowDefinitions = new RowDefinitions("Auto,*");
        _content.Children.Add(bar);
        Grid.SetRow(_pages, 1);
        _content.Children.Add(_pages);
        Children.Add(_content);
    }

    /// <summary>Raised with the page now showing, or null when the panel closes.</summary>
    public event Action<PanelKind?>? Changed;

    public bool IsOpen => _open;

    public PanelKind? Current => _open ? _current : null;

    public void Toggle(PanelKind kind)
    {
        if (_open && _current == kind)
            Close();
        else
            Open(kind);
    }

    public void Open(PanelKind kind)
    {
        var wasOpen = _open;
        var previous = wasOpen ? _current : null;
        _open = true;
        _current = kind;
        IsVisible = true;
        if (!wasOpen)
        {
            Motion.In(_content, 0, 26);
            Focus();
        }

        var direction = previous is null || previous == kind ? 0 : kind > previous ? 1 : -1;
        foreach (var (other, otherPage) in _created.ToList())
        {
            if (other != kind && otherPage.IsVisible)
                HidePage(other, otherPage, -direction);
        }

        var page = Page(kind);
        page.IsVisible = true;
        page.IsHitTestVisible = true;
        page.OnShown();
        if (previous != kind)
        {
            Motion.In(page, 30 * direction, direction == 0 ? 10 : 0);
            Motion.Stagger(page.EntranceTargets);
        }

        _tabs.Set([(PanelKind.Settings, "设置"), (PanelKind.Headphones, "耳机控制"), (PanelKind.Banner, "歌曲分享图")], value => value == kind);
        Changed?.Invoke(kind);
    }

    public void Close()
    {
        if (!_open)
            return;
        _open = false;
        Motion.Out(_content, 0, 20);
        Changed?.Invoke(null);
        DispatcherTimer.RunOnce(() =>
        {
            if (_open)
                return;
            IsVisible = false;
            foreach (var page in _created.Values)
                page.IsVisible = false;
            _current = null;
        }, TimeSpan.FromMilliseconds(260));
    }

    private void HidePage(PanelKind kind, PanelPage page, int direction)
    {
        page.IsHitTestVisible = false;
        Motion.Out(page, 30 * direction, 0);
        DispatcherTimer.RunOnce(() =>
        {
            if (_current != kind || !_open)
                page.IsVisible = false;
        }, TimeSpan.FromMilliseconds(240));
    }

    private PanelPage Page(PanelKind kind)
    {
        if (_created.TryGetValue(kind, out var page))
            return page;
        page = kind switch
        {
            PanelKind.Settings => _createSettings(),
            PanelKind.Headphones => _createHeadphones(),
            PanelKind.Banner => _createBanner(),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        page.IsVisible = false;
        _created[kind] = page;
        _pages.Children.Add(page);
        return page;
    }
}
