using Avalonia;
using Avalonia.Media;
using SkiaSharp;

namespace LyricifyIsland;

/// <summary>Shared glass surfaces and cover-derived colours for the song panels.</summary>
internal static class LyricsTheme
{
    public static readonly LinearGradientBrush PageBackground = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.Parse("#223242"), 0),
            new GradientStop(Color.Parse("#1B2230"), .55),
            new GradientStop(Color.Parse("#2A1E2D"), 1)
        }
    };

    // Frosted glass layers: white at 8 %, 12 % and 17 % over the gradient.
    public static readonly IBrush CardBackground = new SolidColorBrush(Color.FromArgb(0x16, 255, 255, 255));
    public static readonly IBrush InsetBackground = new SolidColorBrush(Color.FromArgb(0x12, 0, 0, 0));
    public static readonly IBrush InputBackground = new SolidColorBrush(Color.FromArgb(0x1F, 255, 255, 255));
    public static readonly IBrush InputHoverBackground = new SolidColorBrush(Color.FromArgb(0x2C, 255, 255, 255));
    public static readonly IBrush CardBorder = new SolidColorBrush(Color.FromArgb(0x1E, 255, 255, 255));
    public static readonly IBrush Divider = new SolidColorBrush(Color.FromArgb(0x16, 255, 255, 255));
    public static readonly IBrush PrimaryText = new SolidColorBrush(Color.FromArgb(0xF2, 255, 255, 255));
    public static readonly IBrush MutedText = new SolidColorBrush(Color.FromArgb(0xA5, 255, 255, 255));
    public static readonly SolidColorBrush Accent = new SolidColorBrush(Color.Parse("#89D4A2"));
    public static readonly SolidColorBrush OnAccent = new SolidColorBrush(Color.Parse("#0C1A12"));
    public static readonly SolidColorBrush AccentHover = new(Color.Parse("#A3E0B8"));
    public static readonly SolidColorBrush AccentPressed = new(Color.Parse("#76C28F"));
    public static readonly SolidColorBrush NavActive = new(Color.FromArgb(0x30, 0x89, 0xD4, 0xA2));
    public static readonly SolidColorBrush DropDownBackground = new(Color.Parse("#262F3D"));

    public static void ApplyCover(SKBitmap? cover)
    {
        var a = new SKColor(57, 80, 102);
        var b = new SKColor(76, 51, 75);
        var accent = new SKColor(137, 212, 162);
        if (cover is not null)
        {
            using var small = SKSurface.Create(new SKImageInfo(64, 64));
            small.Canvas.DrawBitmap(cover, new SKRect(0, 0, 64, 64));
            using var image = small.Snapshot();
            using var pixels = SKBitmap.FromImage(image);
            a = Average(pixels, 0, 32);
            b = Average(pixels, 32, 64);
            accent = CoverAccent(a, b);
        }
        Accent.Color = ToColor(accent);
        AccentHover.Color = Tint(accent, .22f);
        AccentPressed.Color = Tint(accent, -.12f);
        OnAccent.Color = Color.FromRgb((byte)(accent.Red * .08f), (byte)(accent.Green * .08f), (byte)(accent.Blue * .08f));
        NavActive.Color = Color.FromArgb(0x30, accent.Red, accent.Green, accent.Blue);
        DropDownBackground.Color = Color.FromRgb((byte)(a.Red * .22f), (byte)(a.Green * .22f), (byte)(a.Blue * .22f));
        PageBackground.GradientStops[0].Color = Tint(a, -.45f);
        PageBackground.GradientStops[1].Color = Tint(a, -.65f);
        PageBackground.GradientStops[2].Color = Tint(b, -.66f);
    }

    internal static SKColor Average(SKBitmap bitmap, int from, int to)
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

    internal static SKColor CoverAccent(SKColor a, SKColor b)
    {
        // Lift the cover colour into a light accent so dark text stays legible on selected controls.
        var colour = new SKColor((byte)((a.Red + b.Red) / 2), (byte)((a.Green + b.Green) / 2),
            (byte)((a.Blue + b.Blue) / 2));
        var lifted = Tint(colour, .58f);
        return new SKColor(lifted.R, lifted.G, lifted.B);
    }

    private static Color ToColor(SKColor colour) => Color.FromRgb(colour.Red, colour.Green, colour.Blue);

    private static Color Tint(SKColor colour, float amount)
    {
        byte Channel(byte value) => (byte)(amount >= 0 ? value + (255 - value) * amount : value * (1 + amount));
        return Color.FromRgb(Channel(colour.Red), Channel(colour.Green), Channel(colour.Blue));
    }

    public static readonly IBrush Warning = new SolidColorBrush(Color.Parse("#E0B46B"));
    public static readonly IBrush ErrorText = new SolidColorBrush(Color.Parse("#F2A5A5"));

    public static readonly IBrush WindowRim = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255));

    /// <summary>Soft drop shadow around the borderless window, as in the lyrics window.</summary>
    public static readonly BoxShadows WindowShadow = new(new BoxShadow
    {
        OffsetX = 0, OffsetY = 3, Blur = 14, Spread = 0, Color = Color.FromArgb(110, 0, 0, 0)
    });

    public const double CardRadius = 18;
    public const double ControlRadius = 12;
    public const double PillRadius = 20;

    /// <summary>A card shadow similar to the cover shadow in the lyrics window.</summary>
    public static readonly BoxShadows CardShadow = new(new BoxShadow
    {
        OffsetX = 0, OffsetY = 8, Blur = 24, Spread = 0, Color = Color.FromArgb(60, 0, 0, 0)
    });
}
