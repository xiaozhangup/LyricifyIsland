using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;

namespace LyricifyIsland;

internal readonly record struct IslandSettings(
    double WidthPercent,
    double ScalePercent,
    string SpotifyClientId,
    string SpotifyClientSecret)
{
    public bool HasSpotifyCredentials =>
        !string.IsNullOrWhiteSpace(SpotifyClientId)
        && !string.IsNullOrWhiteSpace(SpotifyClientSecret);
}

internal static class SettingsStore
{
    public const double DefaultWidthPercent = 70d;
    public const double MinimumWidthPercent = 40d;
    public const double MaximumWidthPercent = 100d;
    public const double DefaultScalePercent = 100d;
    public const double MinimumScalePercent = 50d;
    public const double MaximumScalePercent = 200d;

    public static IslandSettings Load()
    {
        var path = SettingsPath();
        try
        {
            var directory = Path.GetDirectoryName(path)!;
            if (Directory.Exists(directory))
                Secure(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            if (File.Exists(path))
                Secure(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            var data = JsonSerializer.Deserialize<SettingsData>(File.ReadAllText(path));
            return Normalize(new IslandSettings(
                data?.IslandWidthPercent ?? DefaultWidthPercent,
                data?.IslandScalePercent ?? DefaultScalePercent,
                data?.SpotifyClientId ?? string.Empty,
                data?.SpotifyClientSecret ?? string.Empty));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new IslandSettings(DefaultWidthPercent, DefaultScalePercent, string.Empty, string.Empty);
        }
    }

    public static bool Save(IslandSettings settings)
    {
        var path = SettingsPath();
        var directory = Path.GetDirectoryName(path)!;
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            settings = Normalize(settings);
            Directory.CreateDirectory(directory);
            Secure(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.WriteAllText(temporary, JsonSerializer.Serialize(
                new SettingsData(
                    settings.WidthPercent,
                    settings.ScalePercent,
                    settings.SpotifyClientId,
                    settings.SpotifyClientSecret)));
            Secure(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temporary, path, overwrite: true);
            Secure(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[settings] save failed: {exception.Message}");
            return false;
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    internal static double NormalizeWidthPercent(double value) => double.IsFinite(value)
        ? Math.Clamp(Math.Round(value), MinimumWidthPercent, MaximumWidthPercent)
        : DefaultWidthPercent;

    internal static double NormalizeScalePercent(double value) => double.IsFinite(value)
        ? Math.Clamp(Math.Round(value), MinimumScalePercent, MaximumScalePercent)
        : DefaultScalePercent;

    internal static IslandSettings Normalize(IslandSettings settings) => new(
        NormalizeWidthPercent(settings.WidthPercent),
        NormalizeScalePercent(settings.ScalePercent),
        settings.SpotifyClientId?.Trim() ?? string.Empty,
        settings.SpotifyClientSecret?.Trim() ?? string.Empty);

    private static string ConfigDirectory()
    {
        var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(configHome) || !Path.IsPathRooted(configHome))
            configHome = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        return Path.Combine(configHome, "lyricify-island");
    }

    private static string SettingsPath() => Path.Combine(ConfigDirectory(), "settings.json");

    private static void Secure(string path, UnixFileMode mode)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, mode);
    }

    private sealed record SettingsData(
        double? IslandWidthPercent,
        double? IslandScalePercent,
        string? SpotifyClientId,
        string? SpotifyClientSecret);
}

internal sealed class SettingsWindow : Window
{
    private const string ScaleIcon = "M7 14H5V19H10V17H7V14M5 10H7V7H10V5H5V10M17 17H14V19H19V14H17V17M14 5V7H17V10H19V5H14Z";
    private const string WidthIcon = "M8 7L3 12L8 17V14H11V10H8V7M16 7V10H13V14H16V17L21 12L16 7Z";
    private const string MusicIcon = "M12 3V13.55A4 4 0 1 0 14 17V7H20V3H12Z";
    private const string CacheIcon = "M15.5 4L14.5 3H9.5L8.5 4H5V6H19V4M6 19C6 20.1 6.9 21 8 21H16C17.1 21 18 20.1 18 19V7H6V19Z";
    private static readonly IBrush PageBackground = Brush("#121212");
    private static readonly IBrush CardBackground = Brush("#202020");
    private static readonly IBrush CardBorder = Brush("#292929");
    private static readonly IBrush InputBackground = Brush("#292929");
    private static readonly IBrush PrimaryText = Brush("#F5F5F5");
    private static readonly IBrush MutedText = Brush("#B3B3B3");
    private static readonly IBrush Accent = Brush("#1DB954");

    public SettingsWindow(
        IslandSettings initialSettings,
        WindowIcon icon,
        Func<IslandSettings, bool, bool> settingsChanged)
    {
        var settings = SettingsStore.Normalize(initialSettings);
        Title = "Lyricify Island 设置";
        Icon = icon;
        Width = 840;
        Height = 520;
        MinWidth = 620;
        MinHeight = 360;
        CanResize = true;
        Background = PageBackground;
        FontFamily = new FontFamily("Noto Sans CJK SC");
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        RequestedThemeVariant = ThemeVariant.Dark;

        var scale = CreateSliderCard(
            ScaleIcon,
            "整体缩放",
            "同步缩放字体、图标、胶囊、间距和光效",
            "岛屿整体缩放百分比",
            SettingsStore.MinimumScalePercent,
            SettingsStore.MaximumScalePercent,
            settings.ScalePercent,
            10,
            value =>
            {
                settings = settings with { ScalePercent = SettingsStore.NormalizeScalePercent(value) };
                settingsChanged(settings, false);
            });
        var width = CreateSliderCard(
            WidthIcon,
            "最大宽度",
            "占当前屏幕可用宽度的百分比",
            "岛屿最大宽度百分比",
            SettingsStore.MinimumWidthPercent,
            SettingsStore.MaximumWidthPercent,
            settings.WidthPercent,
            5,
            value =>
            {
                settings = settings with { WidthPercent = SettingsStore.NormalizeWidthPercent(value) };
                settingsChanged(settings, false);
            });
        var clientId = new TextBox
        {
            Text = settings.SpotifyClientId,
            PlaceholderText = "Spotify Developer Dashboard 中的 Client ID",
            MinHeight = 40,
            Padding = new Thickness(11, 7),
            Background = InputBackground,
            BorderBrush = CardBorder,
            CornerRadius = new CornerRadius(6)
        };
        AutomationProperties.SetName(clientId, "Spotify Client ID");
        var clientSecret = new TextBox
        {
            Text = settings.SpotifyClientSecret,
            PlaceholderText = "Spotify Developer Dashboard 中的 Client Secret",
            PasswordChar = '●',
            RevealPassword = false,
            MinHeight = 40,
            Padding = new Thickness(11, 7),
            Background = InputBackground,
            BorderBrush = CardBorder,
            CornerRadius = new CornerRadius(6)
        };
        AutomationProperties.SetName(clientSecret, "Spotify Client Secret");
        var credentialStatus = new TextBlock
        {
            Foreground = MutedText,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        };
        var saveCredentials = new Button
        {
            Content = "保存并重新连接",
            HorizontalAlignment = HorizontalAlignment.Right,
            MinHeight = 36,
            Padding = new Thickness(16, 7),
            Background = Accent,
            Foreground = Brushes.Black,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            FontWeight = FontWeight.SemiBold
        };
        saveCredentials.Click += (_, _) =>
        {
            var id = clientId.Text?.Trim() ?? string.Empty;
            var secret = clientSecret.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(id) != string.IsNullOrEmpty(secret))
            {
                credentialStatus.Text = "请同时填写 Client ID 和 Client Secret";
                return;
            }

            var updated = settings with { SpotifyClientId = id, SpotifyClientSecret = secret };
            if (!settingsChanged(updated, true))
            {
                credentialStatus.Text = "保存失败，请查看终端错误";
                return;
            }

            settings = updated;
            credentialStatus.Text = updated.HasSpotifyCredentials
                ? "已保存，正在重新连接 Spotify…"
                : "已清除 Spotify 参数";
        };
        var credentialActions = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(0, 5, 0, 0),
            Children = { credentialStatus, saveCredentials }
        };
        Grid.SetColumn(saveCredentials, 1);
        var spotifyForm = new StackPanel
        {
            Margin = new Thickness(37, 12, 0, 0),
            Spacing = 7,
            Children =
            {
                new TextBlock
                {
                    Text = "Client ID",
                    Foreground = PrimaryText,
                    FontWeight = FontWeight.SemiBold
                },
                clientId,
                new TextBlock
                {
                    Text = "Client Secret",
                    Foreground = PrimaryText,
                    FontWeight = FontWeight.SemiBold,
                    Margin = new Thickness(0, 3, 0, 0)
                },
                clientSecret,
                credentialActions
            }
        };
        var spotify = CreateCard(
            MusicIcon,
            "Spotify",
            "用于读取当前播放状态，Client Secret 会遮蔽显示",
            body: spotifyForm);

        var cacheStatus = new TextBlock
        {
            Foreground = MutedText,
            VerticalAlignment = VerticalAlignment.Center
        };
        var clearCache = SecondaryButton(string.Empty);
        void RefreshCacheSize() => clearCache.Content = $"清理缓存（{TrackCache.FormatSize(TrackCache.Size())}）";
        RefreshCacheSize();
        clearCache.Click += (_, _) =>
        {
            cacheStatus.Text = TrackCache.Clear() ? "缓存已清理" : "清理失败，请查看终端错误";
            RefreshCacheSize();
        };
        var cacheActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { cacheStatus, clearCache }
        };
        var cache = CreateCard(
            CacheIcon,
            "缓存",
            "缓存歌词、封面和歌曲信息",
            cacheActions);

        Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = new StackPanel
            {
                Margin = new Thickness(10),
                Spacing = 8,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Children = { scale, width, spotify, cache }
            }
        };
    }

    private static Border CreateSliderCard(
        string icon,
        string title,
        string description,
        string accessibleName,
        double minimum,
        double maximum,
        double value,
        double largeChange,
        Action<double> changed)
    {
        var valueLabel = new TextBlock
        {
            Text = $"{value:0}%",
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
            Foreground = PrimaryText,
            Width = 48,
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
            MinWidth = 150,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Accent
        };
        AutomationProperties.SetName(slider, accessibleName);
        slider.ValueChanged += (_, args) =>
        {
            valueLabel.Text = $"{args.NewValue:0}%";
            changed(args.NewValue);
        };
        var control = new Grid
        {
            Width = 253,
            ColumnDefinitions = new ColumnDefinitions("48,12,*"),
            VerticalAlignment = VerticalAlignment.Center,
            Children = { valueLabel, slider }
        };
        Grid.SetColumn(slider, 2);
        return CreateCard(icon, title, description, control);
    }

    private static Border CreateCard(
        string icon,
        string title,
        string description,
        Control? action = null,
        Control? body = null)
    {
        var iconView = new PathIcon
        {
            Data = Geometry.Parse(icon),
            Width = 22,
            Height = 22,
            Foreground = PrimaryText,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var labels = new StackPanel
        {
            Spacing = 1,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock
                {
                    Text = title,
                    FontSize = 16,
                    LineHeight = 20,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = PrimaryText
                },
                new TextBlock
                {
                    Text = description,
                    FontSize = 13,
                    LineHeight = 18,
                    Foreground = MutedText,
                    TextWrapping = TextWrapping.Wrap
                }
            }
        };
        var header = new Grid
        {
            MinHeight = 36,
            ColumnDefinitions = new ColumnDefinitions("24,13,*,Auto"),
            Children = { iconView, labels }
        };
        Grid.SetColumn(labels, 2);
        if (action is not null)
        {
            Grid.SetColumn(action, 3);
            header.Children.Add(action);
        }

        Control content = header;
        if (body is not null)
        {
            content = new StackPanel
            {
                Children =
                {
                    header,
                    new Border
                    {
                        Height = 1,
                        Margin = new Thickness(37, 12, 0, 0),
                        Background = CardBorder
                    },
                    body
                }
            };
        }

        return new Border
        {
            MinHeight = 68,
            Padding = new Thickness(16, 10),
            Background = CardBackground,
            BorderBrush = CardBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = content
        };
    }

    private static Button SecondaryButton(string text) => new()
    {
        Content = text,
        MinHeight = 32,
        Padding = new Thickness(13, 5),
        Background = InputBackground,
        Foreground = PrimaryText,
        BorderBrush = CardBorder,
        CornerRadius = new CornerRadius(5),
        FontWeight = FontWeight.Medium
    };

    private static SolidColorBrush Brush(string color) => new(Color.Parse(color));
}
