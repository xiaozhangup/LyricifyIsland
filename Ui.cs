using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace LyricifyIsland;

/// <summary>Small widget kit shared by the settings and headphone windows.</summary>
internal static class Ui
{
    public static TextBlock Text(string text, double size = 14, FontWeight? weight = null, bool muted = false) => new()
    {
        Text = text,
        FontSize = size,
        FontWeight = weight ?? FontWeight.Normal,
        Foreground = muted ? LyricsTheme.MutedText : LyricsTheme.PrimaryText,
        TextWrapping = TextWrapping.Wrap
    };

    /// <summary>A titled group of rows rendered as one frosted panel.</summary>
    public static StackPanel Section(string title, string description, params Control[] rows) => new()
    {
        Spacing = 12,
        Children =
        {
            new StackPanel
            {
                Spacing = 3,
                Margin = new Thickness(4, 0, 0, 0),
                Children = { Text(title, 19, FontWeight.SemiBold), Text(description, 13, muted: true) }
            },
            Group(rows)
        }
    };

    public static Border Group(params Control[] rows)
    {
        // The first visible row's top hairline is pushed out of the clipped panel.
        var stack = new StackPanel { Margin = new Thickness(0, -1, 0, 0) };
        foreach (var row in rows)
            stack.Children.Add(row);
        return new Border
        {
            Background = LyricsTheme.CardBackground,
            BorderBrush = LyricsTheme.CardBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(LyricsTheme.CardRadius),
            ClipToBounds = true,
            Child = stack
        };
    }

    public static Border Block(Control content) => new()
    {
        Padding = new Thickness(20, 14),
        BorderBrush = LyricsTheme.Divider,
        BorderThickness = new Thickness(0, 1, 0, 0),
        Child = content
    };

    public static Border Row(string title, string description, Control action) =>
        Row(Text(title, 14, FontWeight.SemiBold), Text(description, 12, muted: true), action);

    public static Border Row(Control title, Control description, Control action)
    {
        var labels = new StackPanel
        {
            Spacing = 3,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { title, description }
        };
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,24,Auto"),
            Children = { labels, action }
        };
        action.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(action, 2);
        var row = Block(grid);
        row.MinHeight = 66;
        return row;
    }

    /// <summary>A free-standing frosted card (not part of a row group).</summary>
    public static Border Card(Control content, double padding = 22) => new()
    {
        Padding = new Thickness(padding),
        Background = LyricsTheme.CardBackground,
        BorderBrush = LyricsTheme.CardBorder,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(LyricsTheme.CardRadius),
        Child = content
    };

    public static Button Button(string text, bool accent = false)
    {
        var button = new Button
        {
            Content = text,
            Height = 36,
            Padding = new Thickness(18, 0),
            Background = accent ? LyricsTheme.Accent : LyricsTheme.InputBackground,
            Foreground = accent ? LyricsTheme.OnAccent : LyricsTheme.PrimaryText,
            BorderBrush = LyricsTheme.CardBorder,
            BorderThickness = new Thickness(accent ? 0 : 1),
            CornerRadius = new CornerRadius(LyricsTheme.PillRadius),
            FontWeight = accent ? FontWeight.SemiBold : FontWeight.Medium,
            VerticalContentAlignment = VerticalAlignment.Center,
            Classes = { accent ? "accent" : "glass" }
        };
        return button;
    }

    public static ComboBox Combo(string name, double minWidth = 210)
    {
        var combo = new ComboBox
        {
            MinWidth = minWidth,
            MinHeight = 38,
            Background = LyricsTheme.InputBackground,
            BorderBrush = LyricsTheme.CardBorder,
            CornerRadius = new CornerRadius(LyricsTheme.ControlRadius),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetName(combo, name);
        return combo;
    }

    public static ToggleSwitch Toggle(string name, bool value = false, string? on = null, string? off = null)
    {
        var toggle = new ToggleSwitch
        {
            IsChecked = value,
            OnContent = on,
            OffContent = off,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetName(toggle, name);
        return toggle;
    }

    public static TextBox Input(string name, string placeholder, string text, bool password = false)
    {
        var box = new TextBox
        {
            Text = text,
            PlaceholderText = placeholder,
            MinHeight = 42,
            Padding = new Thickness(14, 9),
            Background = LyricsTheme.InsetBackground,
            BorderBrush = LyricsTheme.CardBorder,
            CornerRadius = new CornerRadius(LyricsTheme.ControlRadius)
        };
        if (password)
        {
            box.PasswordChar = '●';
            box.RevealPassword = false;
        }
        AutomationProperties.SetName(box, name);
        return box;
    }

    /// <summary>Slider with a live value read-out on its left.</summary>
    public static Grid Slider(string name, double minimum, double maximum, double value, double largeChange,
        Func<double, string> format, Action<double> changed, double width = 300)
    {
        var read = new TextBlock
        {
            Text = format(value),
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            Foreground = LyricsTheme.PrimaryText,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        var slider = new Slider
        {
            Minimum = minimum,
            Maximum = maximum,
            Value = value,
            TickFrequency = 1,
            SmallChange = 1,
            LargeChange = largeChange,
            IsSnapToTickEnabled = true,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = LyricsTheme.Accent
        };
        AutomationProperties.SetName(slider, name);
        slider.ValueChanged += (_, args) =>
        {
            read.Text = format(args.NewValue);
            changed(args.NewValue);
        };
        var grid = new Grid
        {
            Width = width,
            ColumnDefinitions = new ColumnDefinitions("*,16,92"),
            Children = { slider, read }
        };
        Grid.SetColumn(read, 2);
        return grid;
    }
}

/// <summary>A pill-shaped segmented selector. Selection is highlighted in the lyrics window's mint accent.</summary>
internal sealed class Segmented<T> : Border
{
    private readonly Grid _grid = new();
    private readonly TranslateTransform _selectionOffset = new();
    private Rect _selectionBounds;
    private readonly Border _selection = new()
    {
        Background = LyricsTheme.Accent,
        CornerRadius = new CornerRadius(17),
        HorizontalAlignment = HorizontalAlignment.Left,
        IsHitTestVisible = false,
        IsVisible = false
    };
    private readonly bool _fill;
    private readonly string _name;
    private readonly List<Button> _buttons = [];
    private List<(T Value, string Label)> _choices = [];
    private Func<T, bool> _isSelected = _ => false;

    public Segmented(string name, bool fill = false)
    {
        _name = name;
        _fill = fill;
        Background = LyricsTheme.InsetBackground;
        BorderBrush = LyricsTheme.CardBorder;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(20);
        Padding = new Thickness(3);
        HorizontalAlignment = fill ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Center;
        _selection.RenderTransform = _selectionOffset;
        Child = new Grid { Children = { _selection, _grid } };
        _grid.LayoutUpdated += (_, _) => PositionSelection(false);
        AutomationProperties.SetName(this, name);
    }

    /// <summary>Raised only when the user picks a segment.</summary>
    public event Action<T>? Changed;

    public void Set(IReadOnlyList<(T Value, string Label)> choices, Func<T, bool> isSelected)
    {
        if (!_choices.SequenceEqual(choices))
            Rebuild(choices);
        _isSelected = isSelected;
        Refresh();
    }

    private void Rebuild(IReadOnlyList<(T Value, string Label)> choices)
    {
        _choices = [.. choices];
        _buttons.Clear();
        _grid.Children.Clear();
        _grid.ColumnDefinitions = new ColumnDefinitions(string.Join(",",
            _choices.Select(_ => _fill ? "*" : "Auto")));
        for (var i = 0; i < _choices.Count; i++)
        {
            var (value, label) = _choices[i];
            var button = new Button
            {
                Content = label,
                Height = 32,
                MinWidth = _fill ? 0 : 64,
                Padding = new Thickness(_fill ? 6 : 14, 0),
                Margin = new Thickness(i == 0 ? 0 : 2, 0, 0, 0),
                Background = Brushes.Transparent,
                Foreground = LyricsTheme.PrimaryText,
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(17),
                FontSize = 13,
                FontWeight = FontWeight.Medium,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Classes = { "seg" }
            };
            AutomationProperties.SetName(button, $"{_name}：{label}");
            button.Click += (_, _) =>
            {
                _isSelected = candidate => EqualityComparer<T>.Default.Equals(candidate, value);
                Refresh();
                Changed?.Invoke(value);
            };
            Grid.SetColumn(button, i);
            _buttons.Add(button);
            _grid.Children.Add(button);
        }
    }

    private void Refresh()
    {
        for (var i = 0; i < _buttons.Count; i++)
        {
            var selected = _isSelected(_choices[i].Value);
            _buttons[i].Classes.Set("segactive", selected);
        }
        PositionSelection(true);
    }

    private void PositionSelection(bool animate)
    {
        var index = _choices.FindIndex(choice => _isSelected(choice.Value));
        if (index < 0 || _buttons[index].Bounds.Width <= 0)
        {
            _selection.IsVisible = false;
            return;
        }

        var bounds = _buttons[index].Bounds;
        if (_selection.IsVisible && bounds == _selectionBounds)
            return;
        _selectionBounds = bounds;
        // Layout changes place the highlight immediately; user selections glide from its current position.
        if (!_selection.IsVisible || !animate)
        {
            _selection.Transitions = null;
            _selectionOffset.Transitions = null;
            _selection.Width = bounds.Width;
            _selectionOffset.X = bounds.X;
        }
        else
        {
            if (_selection.Transitions is null)
            {
                var duration = TimeSpan.FromMilliseconds(220);
                _selection.Transitions = new Transitions
                {
                    new DoubleTransition { Property = WidthProperty, Duration = duration, Easing = new CubicEaseOut() }
                };
                _selectionOffset.Transitions = new Transitions
                {
                    new DoubleTransition { Property = TranslateTransform.XProperty, Duration = duration, Easing = new CubicEaseOut() }
                };
            }
            _selection.Width = bounds.Width;
            _selectionOffset.X = bounds.X;
        }
        _selection.IsVisible = true;
    }
}
