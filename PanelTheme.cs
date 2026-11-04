using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Media;
using Avalonia.Styling;

namespace LyricifyIsland;

/// <summary>Gives stock Avalonia controls the lyrics-window look inside the panel host.</summary>
internal static class PanelTheme
{
    public static void Apply(Control host)
    {
        RegisterThemeResources(host);
        RegisterButtonStyles(host);
    }

    private static Setter ColourFade() => new(Animatable.TransitionsProperty, new Transitions
    {
        new BrushTransition { Property = ContentPresenter.BackgroundProperty, Duration = TimeSpan.FromMilliseconds(180) },
        new BrushTransition { Property = ContentPresenter.ForegroundProperty, Duration = TimeSpan.FromMilliseconds(180) }
    });

    private static void RegisterThemeResources(Control host)
    {
        // Fluent looks these brushes up by key, so the stock controls pick up the lyrics palette.
        IBrush Solid(string color) => new SolidColorBrush(Color.Parse(color));
        IBrush White(byte alpha) => new SolidColorBrush(Color.FromArgb(alpha, 255, 255, 255));
        void Set(IBrush brush, params string[] keys)
        {
            foreach (var key in keys)
                host.Resources[key] = brush;
        }

        Set(LyricsTheme.Accent, "ToggleSwitchFillOn", "ToggleSwitchFillOnPointerOver", "ToggleSwitchFillOnPressed",
            "ToggleSwitchStrokeOn", "ToggleSwitchStrokeOnPointerOver", "ToggleSwitchStrokeOnPressed");
        Set(White(0x26), "ToggleSwitchFillOff", "ToggleSwitchFillOffPointerOver", "ToggleSwitchFillOffPressed");
        Set(White(0x55), "ToggleSwitchStrokeOff", "ToggleSwitchStrokeOffPointerOver", "ToggleSwitchStrokeOffPressed");
        Set(LyricsTheme.OnAccent, "ToggleSwitchKnobFillOn", "ToggleSwitchKnobFillOnPointerOver", "ToggleSwitchKnobFillOnPressed");
        Set(LyricsTheme.PrimaryText, "ToggleSwitchKnobFillOff", "ToggleSwitchKnobFillOffPointerOver", "ToggleSwitchKnobFillOffPressed");
        Set(White(0x30), "SliderTrackFill", "SliderTrackFillPointerOver", "SliderTrackFillPressed");
        Set(LyricsTheme.Accent, "SliderTrackValueFill", "SliderTrackValueFillPointerOver", "SliderTrackValueFillPressed");
        Set(Solid("#F2F5F2"), "SliderThumbBackground", "SliderThumbBackgroundPointerOver", "SliderThumbBackgroundPressed");
        Set(LyricsTheme.InputBackground, "ComboBoxBackground", "TextControlBackground");
        Set(LyricsTheme.InputHoverBackground, "ComboBoxBackgroundPointerOver", "ComboBoxBackgroundPressed",
            "ComboBoxBackgroundFocused", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused");
        Set(LyricsTheme.CardBorder, "ComboBoxBorderBrush", "ComboBoxBorderBrushPointerOver", "ComboBoxBorderBrushPressed",
            "TextControlBorderBrush", "TextControlBorderBrushPointerOver");
        Set(LyricsTheme.Accent, "TextControlBorderBrushFocused", "ComboBoxBorderBrushFocused");
        Set(LyricsTheme.DropDownBackground, "ComboBoxDropDownBackground");
        Set(White(0x28), "ComboBoxDropDownBorderBrush");
    }

    private static void RegisterButtonStyles(Control host)
    {
        // Fluent restyles the button template on hover/press, so state colours are set on the presenter.
        void State(string cls, string pseudo, IBrush background, IBrush? foreground = null, IBrush? border = null)
        {
            var style = new Style(x => x.OfType<Button>().Class(cls).Class(pseudo)
                .Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"))
            {
                Setters = { new Setter(ContentPresenter.BackgroundProperty, background), ColourFade() }
            };
            if (foreground is not null)
                style.Setters.Add(new Setter(ContentPresenter.ForegroundProperty, foreground));
            if (border is not null)
                style.Setters.Add(new Setter(ContentPresenter.BorderBrushProperty, border));
            host.Styles.Add(style);
        }

        void Rest(string cls, IBrush background, IBrush? foreground = null)
        {
            var style = new Style(x => x.OfType<Button>().Class(cls)
                .Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"))
            {
                Setters = { new Setter(ContentPresenter.BackgroundProperty, background), ColourFade() }
            };
            if (foreground is not null)
                style.Setters.Add(new Setter(ContentPresenter.ForegroundProperty, foreground));
            host.Styles.Add(style);
        }

        var hover = new SolidColorBrush(Color.FromArgb(0x24, 255, 255, 255));
        var pressed = new SolidColorBrush(Color.FromArgb(0x34, 255, 255, 255));
        State("chrome", ":pointerover", hover, LyricsTheme.PrimaryText);
        State("chrome", ":pressed", pressed, LyricsTheme.PrimaryText);
        var closeHover = new SolidColorBrush(Color.FromArgb(175, 228, 76, 91));
        var closePressed = new SolidColorBrush(Color.FromArgb(215, 228, 76, 91));
        State("chromeclose", ":pointerover", closeHover, Brushes.White);
        State("chromeclose", ":pressed", closePressed, Brushes.White);

        var accentHover = LyricsTheme.AccentHover;
        var accentPressed = LyricsTheme.AccentPressed;
        Rest("accent", LyricsTheme.Accent, LyricsTheme.OnAccent);
        State("accent", ":pointerover", accentHover, LyricsTheme.OnAccent);
        State("accent", ":pressed", accentPressed, LyricsTheme.OnAccent);

        State("glass", ":pointerover", LyricsTheme.InputHoverBackground, LyricsTheme.PrimaryText, LyricsTheme.CardBorder);
        State("glass", ":pressed", pressed, LyricsTheme.PrimaryText, LyricsTheme.CardBorder);

        // Segmented selector.
        Rest("seg", Brushes.Transparent, LyricsTheme.PrimaryText);
        State("seg", ":pointerover", Brushes.Transparent, LyricsTheme.PrimaryText);
        State("seg", ":pressed", Brushes.Transparent, LyricsTheme.PrimaryText);
        Rest("segactive", Brushes.Transparent, LyricsTheme.OnAccent);
        State("segactive", ":pointerover", Brushes.Transparent, LyricsTheme.OnAccent);
        State("segactive", ":pressed", Brushes.Transparent, LyricsTheme.OnAccent);

        // Sidebar entries: transparent until hovered, tinted with the cover accent while current.
        State("nav", ":pointerover", new SolidColorBrush(Color.FromArgb(0x18, 255, 255, 255)), LyricsTheme.PrimaryText);
        State("nav", ":pressed", pressed, LyricsTheme.PrimaryText);
        var navActive = LyricsTheme.NavActive;
        Rest("navactive", navActive, LyricsTheme.PrimaryText);
        State("navactive", ":pointerover", navActive, LyricsTheme.PrimaryText);
        State("navactive", ":pressed", navActive, LyricsTheme.PrimaryText);
    }
}
