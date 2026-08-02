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
    public SettingsWindow(
        IslandSettings initialSettings,
        WindowIcon icon,
        Func<IslandSettings, bool, bool> settingsChanged)
    {
        var settings = SettingsStore.Normalize(initialSettings);
        Title = "Lyricify Island 设置";
        Icon = icon;
        Width = 420;
        Height = 620;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        RequestedThemeVariant = ThemeVariant.Dark;

        var scale = CreateSlider(
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
        var width = CreateSlider(
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
            PlaceholderText = "Spotify Developer Dashboard 中的 Client ID"
        };
        AutomationProperties.SetName(clientId, "Spotify Client ID");
        var clientSecret = new TextBox
        {
            Text = settings.SpotifyClientSecret,
            PlaceholderText = "Spotify Developer Dashboard 中的 Client Secret",
            PasswordChar = '●',
            RevealPassword = false
        };
        AutomationProperties.SetName(clientSecret, "Spotify Client Secret");
        var credentialStatus = new TextBlock { Foreground = Brushes.Gray };
        var saveCredentials = new Button
        {
            Content = "保存并重新连接",
            HorizontalAlignment = HorizontalAlignment.Stretch
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
        var spotify = new StackPanel
        {
            Spacing = 7,
            Children =
            {
                new TextBlock
                {
                    Text = "Spotify",
                    FontSize = 18,
                    FontWeight = FontWeight.SemiBold
                },
                new TextBlock
                {
                    Text = "用于读取当前播放状态，Secret 会遮蔽显示",
                    Foreground = Brushes.Gray
                },
                new TextBlock { Text = "Client ID" },
                clientId,
                new TextBlock { Text = "Client Secret" },
                clientSecret,
                saveCredentials,
                credentialStatus
            }
        };

        Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 18,
            Children = { scale, width, spotify }
        };
    }

    private static StackPanel CreateSlider(
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
            FontSize = 22,
            FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        var slider = new Slider
        {
            Minimum = minimum,
            Maximum = maximum,
            Value = value,
            TickFrequency = 1,
            SmallChange = 1,
            LargeChange = largeChange,
            IsSnapToTickEnabled = true
        };
        AutomationProperties.SetName(slider, accessibleName);
        slider.ValueChanged += (_, args) =>
        {
            valueLabel.Text = $"{args.NewValue:0}%";
            changed(args.NewValue);
        };
        return new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new TextBlock
                {
                    Text = title,
                    FontSize = 18,
                    FontWeight = FontWeight.SemiBold
                },
                new TextBlock { Text = description, Foreground = Brushes.Gray },
                slider,
                valueLabel
            }
        };
    }
}
