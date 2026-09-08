using System.Collections.Immutable;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;

namespace LyricifyIsland;

internal enum PausedDisplayMode
{
    HideImmediately,
    HideAfterThreeSeconds,
    KeepVisible
}

internal enum LyricsSourcePreference
{
    Automatic,
    Netease,
    Kugou,
    Lrclib
}

internal enum PlaybackSourcePreference
{
    Spotify,
    Mpris
}

internal readonly record struct IslandSettings(
    double WidthPercent = SettingsStore.DefaultWidthPercent,
    double ScalePercent = SettingsStore.DefaultScalePercent,
    string SpotifyClientId = "",
    string SpotifyClientSecret = "",
    int TopOffset = SettingsStore.DefaultTopOffset,
    int LyricsOffsetMs = 0,
    bool ShowTranslation = true,
    double BackgroundOpacityPercent = SettingsStore.DefaultBackgroundOpacityPercent,
    bool RememberPosition = true,
    bool LockPosition = false,
    bool ClickThrough = false,
    PausedDisplayMode PausedDisplay = PausedDisplayMode.HideImmediately,
    int TemporaryHideSeconds = SettingsStore.DefaultTemporaryHideSeconds,
    bool StartOnLogin = false,
    string? DisplayId = null,
    int? WindowX = null,
    int? WindowY = null,
    LyricsSourcePreference LyricsSource = LyricsSourcePreference.Automatic,
    bool EnableTrackOffsets = false,
    ImmutableDictionary<string, int>? TrackOffsetsMs = null,
    ImmutableDictionary<string, string>? TrackOffsetTitles = null,
    PlaybackSourcePreference PlaybackSource = PlaybackSourcePreference.Spotify)
{
    public bool HasSpotifyCredentials =>
        !string.IsNullOrWhiteSpace(SpotifyClientId)
        && !string.IsNullOrWhiteSpace(SpotifyClientSecret);

    public int TrackOffsetFor(string? trackId) => trackId is not null
        && TrackOffsetsMs?.TryGetValue(trackId, out var offset) == true
            ? offset
            : 0;

    public string TrackOffsetTitleFor(string trackId) =>
        TrackOffsetTitles?.TryGetValue(trackId, out var title) == true ? title : trackId;

    public IslandSettings WithTrackOffset(TrackInfo track, int offsetMs) => WithTrackOffset(
        track.Id,
        track.Artists.IsDefaultOrEmpty
            ? track.Title
            : $"{track.Title} — {string.Join(", ", track.Artists)}",
        offsetMs);

    public IslandSettings WithTrackOffset(string trackId, string? title, int offsetMs)
    {
        var offsets = TrackOffsetsMs ?? ImmutableDictionary<string, int>.Empty;
        var titles = TrackOffsetTitles ?? ImmutableDictionary<string, string>.Empty;
        offsetMs = SettingsStore.NormalizeLyricsOffsetMs(offsetMs);
        offsets = offsets.SetItem(trackId, offsetMs);
        if (!string.IsNullOrWhiteSpace(title))
            titles = titles.SetItem(trackId, title.Trim());
        return this with
        {
            TrackOffsetsMs = offsets.IsEmpty ? null : offsets,
            TrackOffsetTitles = titles.IsEmpty ? null : titles
        };
    }

    public IslandSettings RemoveTrackOffset(string trackId)
    {
        var offsets = TrackOffsetsMs?.Remove(trackId);
        var titles = TrackOffsetTitles?.Remove(trackId);
        return this with
        {
            TrackOffsetsMs = offsets is null || offsets.IsEmpty ? null : offsets,
            TrackOffsetTitles = titles is null || titles.IsEmpty ? null : titles
        };
    }
}

internal static class SettingsStore
{
    public const double DefaultWidthPercent = 70d;
    public const double MinimumWidthPercent = 40d;
    public const double MaximumWidthPercent = 100d;
    public const double DefaultScalePercent = 100d;
    public const double MinimumScalePercent = 50d;
    public const double MaximumScalePercent = 200d;
    public const int DefaultTopOffset = 58;
    public const int MinimumTopOffset = 0;
    public const int MaximumTopOffset = 300;
    public const int MinimumLyricsOffsetMs = -2_000;
    public const int MaximumLyricsOffsetMs = 2_000;
    public const double DefaultBackgroundOpacityPercent = 67d;
    public const double MinimumBackgroundOpacityPercent = 35d;
    public const double MaximumBackgroundOpacityPercent = 95d;
    public const int DefaultTemporaryHideSeconds = 2;

    public static IslandSettings Load()
    {
        var path = SettingsPath();
        try
        {
            var directory = Path.GetDirectoryName(path)!;
            if (Directory.Exists(directory))
                Secure(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            SettingsData? data = null;
            if (File.Exists(path))
            {
                Secure(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                data = JsonSerializer.Deserialize<SettingsData>(File.ReadAllText(path));
            }
            return Normalize(new IslandSettings(
                data?.IslandWidthPercent ?? DefaultWidthPercent,
                data?.IslandScalePercent ?? DefaultScalePercent,
                data?.SpotifyClientId ?? string.Empty,
                data?.SpotifyClientSecret ?? string.Empty,
                data?.TopOffset ?? EnvironmentInt("LYRICIFY_Y") ?? DefaultTopOffset,
                data?.LyricsOffsetMs ?? EnvironmentInt("LYRICIFY_OFFSET_MS") ?? 0,
                data?.ShowTranslation ?? true,
                data?.BackgroundOpacityPercent ?? DefaultBackgroundOpacityPercent,
                data?.RememberPosition ?? true,
                data?.LockPosition ?? false,
                data?.ClickThrough ?? Environment.GetEnvironmentVariable("LYRICIFY_CLICK_THROUGH") == "1",
                data?.PausedDisplay ?? PausedDisplayMode.HideImmediately,
                data?.TemporaryHideSeconds ?? DefaultTemporaryHideSeconds,
                data?.StartOnLogin ?? false,
                data?.DisplayId,
                data?.WindowX,
                data?.WindowY,
                data?.LyricsSource ?? LyricsSourcePreference.Automatic,
                data?.EnableTrackOffsets ?? data?.TrackOffsetsMs is { Count: > 0 },
                data?.TrackOffsetsMs?.ToImmutableDictionary(StringComparer.Ordinal),
                data?.TrackOffsetTitles?.ToImmutableDictionary(StringComparer.Ordinal),
                data?.PlaybackSource ?? PlaybackSourcePreference.Spotify));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            Console.Error.WriteLine($"[settings] load failed: {exception.Message}");
            return Normalize(new IslandSettings(
                TopOffset: EnvironmentInt("LYRICIFY_Y") ?? DefaultTopOffset,
                LyricsOffsetMs: EnvironmentInt("LYRICIFY_OFFSET_MS") ?? 0,
                ClickThrough: Environment.GetEnvironmentVariable("LYRICIFY_CLICK_THROUGH") == "1"));
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
                new SettingsData
                {
                    IslandWidthPercent = settings.WidthPercent,
                    IslandScalePercent = settings.ScalePercent,
                    SpotifyClientId = settings.SpotifyClientId,
                    SpotifyClientSecret = settings.SpotifyClientSecret,
                    TopOffset = settings.TopOffset,
                    LyricsOffsetMs = settings.LyricsOffsetMs,
                    ShowTranslation = settings.ShowTranslation,
                    BackgroundOpacityPercent = settings.BackgroundOpacityPercent,
                    RememberPosition = settings.RememberPosition,
                    LockPosition = settings.LockPosition,
                    ClickThrough = settings.ClickThrough,
                    PausedDisplay = settings.PausedDisplay,
                    TemporaryHideSeconds = settings.TemporaryHideSeconds,
                    StartOnLogin = settings.StartOnLogin,
                    DisplayId = settings.DisplayId,
                    WindowX = settings.WindowX,
                    WindowY = settings.WindowY,
                    LyricsSource = settings.LyricsSource,
                    EnableTrackOffsets = settings.EnableTrackOffsets,
                    TrackOffsetsMs = settings.TrackOffsetsMs?.ToDictionary(),
                    TrackOffsetTitles = settings.TrackOffsetTitles?.ToDictionary(),
                    PlaybackSource = settings.PlaybackSource
                }));
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

    internal static int NormalizeTopOffset(int value) => Math.Clamp(value, MinimumTopOffset, MaximumTopOffset);

    internal static int NormalizeLyricsOffsetMs(int value) =>
        Math.Clamp(value, MinimumLyricsOffsetMs, MaximumLyricsOffsetMs);

    internal static double NormalizeBackgroundOpacityPercent(double value) => double.IsFinite(value)
        ? Math.Clamp(Math.Round(value), MinimumBackgroundOpacityPercent, MaximumBackgroundOpacityPercent)
        : DefaultBackgroundOpacityPercent;

    internal static int NormalizeTemporaryHideSeconds(int value) => value is 2 or 5 or 10 or 30
        ? value
        : DefaultTemporaryHideSeconds;

    internal static IslandSettings Normalize(IslandSettings settings)
    {
        var offsets = NormalizeTrackOffsets(settings.TrackOffsetsMs);
        return settings with
        {
            WidthPercent = NormalizeWidthPercent(settings.WidthPercent),
            ScalePercent = NormalizeScalePercent(settings.ScalePercent),
            SpotifyClientId = settings.SpotifyClientId?.Trim() ?? string.Empty,
            SpotifyClientSecret = settings.SpotifyClientSecret?.Trim() ?? string.Empty,
            TopOffset = NormalizeTopOffset(settings.TopOffset),
            LyricsOffsetMs = NormalizeLyricsOffsetMs(settings.LyricsOffsetMs),
            BackgroundOpacityPercent = NormalizeBackgroundOpacityPercent(settings.BackgroundOpacityPercent),
            PausedDisplay = Enum.IsDefined(settings.PausedDisplay)
                ? settings.PausedDisplay
                : PausedDisplayMode.HideImmediately,
            LyricsSource = Enum.IsDefined(settings.LyricsSource)
                ? settings.LyricsSource
                : LyricsSourcePreference.Automatic,
            PlaybackSource = Enum.IsDefined(settings.PlaybackSource)
                ? settings.PlaybackSource
                : PlaybackSourcePreference.Spotify,
            TemporaryHideSeconds = NormalizeTemporaryHideSeconds(settings.TemporaryHideSeconds),
            DisplayId = string.IsNullOrWhiteSpace(settings.DisplayId) ? null : settings.DisplayId.Trim(),
            WindowX = settings.RememberPosition ? settings.WindowX : null,
            WindowY = settings.RememberPosition ? settings.WindowY : null,
            TrackOffsetsMs = offsets,
            TrackOffsetTitles = NormalizeTrackOffsetTitles(settings.TrackOffsetTitles, offsets)
        };
    }

    private static ImmutableDictionary<string, int>? NormalizeTrackOffsets(
        ImmutableDictionary<string, int>? offsets)
    {
        if (offsets is null || offsets.Count == 0)
            return null;

        var normalized = ImmutableDictionary.CreateBuilder<string, int>(StringComparer.Ordinal);
        foreach (var (trackId, offset) in offsets)
        {
            if (!string.IsNullOrWhiteSpace(trackId))
                normalized[trackId] = NormalizeLyricsOffsetMs(offset);
        }
        return normalized.Count == 0 ? null : normalized.ToImmutable();
    }

    private static ImmutableDictionary<string, string>? NormalizeTrackOffsetTitles(
        ImmutableDictionary<string, string>? titles,
        ImmutableDictionary<string, int>? offsets)
    {
        if (titles is null || offsets is null)
            return null;

        var normalized = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var (trackId, title) in titles)
        {
            if (offsets.ContainsKey(trackId) && !string.IsNullOrWhiteSpace(title))
                normalized[trackId] = title.Trim();
        }
        return normalized.Count == 0 ? null : normalized.ToImmutable();
    }

    internal static string ConfigHome()
    {
        var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(configHome) || !Path.IsPathRooted(configHome))
            configHome = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        return configHome;
    }

    private static string ConfigDirectory() => Path.Combine(ConfigHome(), "lyricify-island");
    private static string SettingsPath() => Path.Combine(ConfigDirectory(), "settings.json");

    private static int? EnvironmentInt(string name) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var value) ? value : null;

    private static void Secure(string path, UnixFileMode mode)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, mode);
    }

    private sealed class SettingsData
    {
        public double? IslandWidthPercent { get; init; }
        public double? IslandScalePercent { get; init; }
        public string? SpotifyClientId { get; init; }
        public string? SpotifyClientSecret { get; init; }
        public int? TopOffset { get; init; }
        public int? LyricsOffsetMs { get; init; }
        public bool? ShowTranslation { get; init; }
        public double? BackgroundOpacityPercent { get; init; }
        public bool? RememberPosition { get; init; }
        public bool? LockPosition { get; init; }
        public bool? ClickThrough { get; init; }
        public PausedDisplayMode? PausedDisplay { get; init; }
        public int? TemporaryHideSeconds { get; init; }
        public bool? StartOnLogin { get; init; }
        public string? DisplayId { get; init; }
        public int? WindowX { get; init; }
        public int? WindowY { get; init; }
        public LyricsSourcePreference? LyricsSource { get; init; }
        public bool? EnableTrackOffsets { get; init; }
        public Dictionary<string, int>? TrackOffsetsMs { get; init; }
        public Dictionary<string, string>? TrackOffsetTitles { get; init; }
        public PlaybackSourcePreference? PlaybackSource { get; init; }
    }
}

internal static class Autostart
{
    internal static bool SetEnabled(bool enabled, string? executable = null)
    {
        if (!OperatingSystem.IsLinux())
            return !enabled;

        var path = Path.Combine(SettingsStore.ConfigHome(), "autostart", "lyricify-island.desktop");
        try
        {
            if (!enabled)
            {
                File.Delete(path);
                return true;
            }

            executable ??= File.Exists("/usr/bin/LyricifyIsland")
                ? "/usr/bin/LyricifyIsland"
                : Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable))
                throw new IOException("无法确定程序路径");

            var directory = Path.GetDirectoryName(path)!;
            var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
            Directory.CreateDirectory(directory);
            try
            {
                File.WriteAllText(temporary, DesktopEntry(executable));
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Console.Error.WriteLine($"[autostart] update failed: {exception.Message}");
            return false;
        }
    }

    internal static string DesktopEntry(string executable) =>
        $"[Desktop Entry]\nType=Application\nName=Lyricify Island\nExec={Quote(executable)}\nTerminal=false\nX-GNOME-Autostart-enabled=true\n";

    private static string Quote(string value) =>
        $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("`", "\\`").Replace("$", "\\$").Replace("%", "%%")}\"";
}

internal sealed class SettingsWindow : Window
{
    private const string ScaleIcon = "M7 14H5V19H10V17H7V14M5 10H7V7H10V5H5V10M17 17H14V19H19V14H17V17M14 5V7H17V10H19V5H14Z";
    private const string MusicIcon = "M12 3V13.55A4 4 0 1 0 14 17V7H20V3H12Z";
    private const string PositionIcon = "M12 2C8.13 2 5 5.13 5 9C5 14.25 12 22 12 22S19 14.25 19 9C19 5.13 15.87 2 12 2M12 11.5A2.5 2.5 0 1 1 12 6.5A2.5 2.5 0 0 1 12 11.5Z";
    private const string BehaviorIcon = "M12 2A10 10 0 1 0 22 12A10 10 0 0 0 12 2M13 7V11.59L16.2 14.79L14.79 16.2L11 12.41V7H13Z";
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
        Func<IslandSettings, bool, bool> settingsChanged,
        Func<IslandSettings> currentSettings,
        Func<bool> refreshCurrentTrack)
    {
        var settings = SettingsStore.Normalize(initialSettings);
        Title = "Lyricify Island 设置";
        Icon = icon;
        Width = 840;
        Height = 650;
        MinWidth = 620;
        MinHeight = 360;
        CanResize = true;
        Background = PageBackground;
        FontFamily = new FontFamily("Noto Sans CJK SC");
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        RequestedThemeVariant = ThemeVariant.Dark;

        bool Commit(
            IslandSettings updated,
            bool restartPlayback = false,
            bool trackOffsetsChanged = false)
        {
            if (!trackOffsetsChanged)
            {
                var latest = currentSettings();
                updated = updated with
                {
                    TrackOffsetsMs = latest.TrackOffsetsMs,
                    TrackOffsetTitles = latest.TrackOffsetTitles
                };
            }
            updated = SettingsStore.Normalize(updated);
            if (!settingsChanged(updated, restartPlayback))
                return false;
            settings = updated;
            return true;
        }

        var appearance = CreateCard(
            ScaleIcon,
            "外观",
            "调整岛屿的尺寸和背景",
            body: CreateSettingsBody(
                CreateSettingRow(
                    "整体缩放",
                    "同步缩放字体、图标、胶囊、间距和光效",
                    CreateSlider(
                        "岛屿整体缩放百分比",
                        SettingsStore.MinimumScalePercent,
                        SettingsStore.MaximumScalePercent,
                        settings.ScalePercent,
                        10,
                        value => $"{value:0}%",
                        value => Commit(settings with
                        {
                            ScalePercent = SettingsStore.NormalizeScalePercent(value)
                        }))),
                CreateSettingRow(
                    "最大宽度",
                    "占所在显示器可用宽度的百分比",
                    CreateSlider(
                        "岛屿最大宽度百分比",
                        SettingsStore.MinimumWidthPercent,
                        SettingsStore.MaximumWidthPercent,
                        settings.WidthPercent,
                        5,
                        value => $"{value:0}%",
                        value => Commit(settings with
                        {
                            WidthPercent = SettingsStore.NormalizeWidthPercent(value)
                        }))),
                CreateSettingRow(
                    "背景不透明度",
                    "降低后更容易融入桌面，但文字仍保持清晰",
                    CreateSlider(
                        "岛屿背景不透明度百分比",
                        SettingsStore.MinimumBackgroundOpacityPercent,
                        SettingsStore.MaximumBackgroundOpacityPercent,
                        settings.BackgroundOpacityPercent,
                        5,
                        value => $"{value:0}%",
                        value => Commit(settings with
                        {
                            BackgroundOpacityPercent = SettingsStore.NormalizeBackgroundOpacityPercent(value)
                        })))));

        var sourceChoices = new[]
        {
            new Choice<LyricsSourcePreference>(LyricsSourcePreference.Automatic, "自动（逐字优先）"),
            new Choice<LyricsSourcePreference>(LyricsSourcePreference.Netease, "网易云"),
            new Choice<LyricsSourcePreference>(LyricsSourcePreference.Kugou, "酷狗"),
            new Choice<LyricsSourcePreference>(LyricsSourcePreference.Lrclib, "LRCLIB")
        };
        var trackOffsetManagerTitle = new TextBlock
        {
            Foreground = PrimaryText,
            FontWeight = FontWeight.SemiBold
        };
        var trackOffsetList = new StackPanel();
        var trackOffsetManagerCard = new Border
        {
            IsVisible = false,
            Margin = new Thickness(0, 4, 0, 0),
            Padding = new Thickness(0, 4, 0, 0),
            BorderBrush = CardBorder,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = trackOffsetList
        };
        var trackOffsetManagerIndicator = new TextBlock
        {
            Text = "▾",
            Foreground = MutedText,
            FontSize = 15,
            VerticalAlignment = VerticalAlignment.Center
        };
        var trackOffsetManagerHeaderContent = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Children = { trackOffsetManagerTitle, trackOffsetManagerIndicator }
        };
        Grid.SetColumn(trackOffsetManagerIndicator, 1);
        var trackOffsetManagerHeader = new Button
        {
            MinHeight = 32,
            Padding = new Thickness(0, 4),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = trackOffsetManagerHeaderContent
        };
        var trackOffsetManager = new Border
        {
            Margin = new Thickness(0, 4, 0, 0),
            Padding = new Thickness(12, 6),
            Background = InputBackground,
            BorderBrush = CardBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = new StackPanel
            {
                Children = { trackOffsetManagerHeader, trackOffsetManagerCard }
            }
        };

        void SetTrackOffsetManagerExpanded(bool expanded)
        {
            trackOffsetManagerCard.IsVisible = expanded;
            trackOffsetManagerIndicator.Text = expanded ? "▴" : "▾";
            AutomationProperties.SetName(
                trackOffsetManagerHeader,
                expanded ? "收起已设置歌曲" : "展开已设置歌曲");
        }

        SetTrackOffsetManagerExpanded(false);
        trackOffsetManagerHeader.Click += (_, _) =>
            SetTrackOffsetManagerExpanded(!trackOffsetManagerCard.IsVisible);

        void RefreshTrackOffsetManager()
        {
            trackOffsetManager.IsVisible = settings.EnableTrackOffsets;
            if (!settings.EnableTrackOffsets)
            {
                SetTrackOffsetManagerExpanded(false);
                return;
            }

            var offsets = settings.TrackOffsetsMs;
            trackOffsetManagerTitle.Text = $"已设置歌曲（{offsets?.Count ?? 0}）";
            trackOffsetList.Children.Clear();
            if (offsets is null)
            {
                trackOffsetList.Children.Add(new TextBlock
                {
                    Text = "还没有保存过歌曲偏移。播放歌曲后，在灵动岛上右键即可添加。",
                    Foreground = MutedText,
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap
                });
                return;
            }

            foreach (var (trackId, value) in offsets.OrderBy(
                         entry => settings.TrackOffsetTitleFor(entry.Key),
                         StringComparer.Ordinal))
            {
                var title = settings.TrackOffsetTitleFor(trackId);
                var slider = new Slider
                {
                    Minimum = SettingsStore.MinimumLyricsOffsetMs,
                    Maximum = SettingsStore.MaximumLyricsOffsetMs,
                    TickFrequency = 50,
                    SmallChange = 50,
                    LargeChange = 100,
                    IsSnapToTickEnabled = true,
                    Value = value,
                    MinWidth = 130,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = Accent
                };
                AutomationProperties.SetName(slider, $"{title} 歌词偏移滑杆");
                var editorBackground = Brush("#353535");
                var editorBorder = Brush("#404040");
                var editor = new NumericUpDown
                {
                    Minimum = SettingsStore.MinimumLyricsOffsetMs,
                    Maximum = SettingsStore.MaximumLyricsOffsetMs,
                    ClipValueToMinMax = true,
                    ShowButtonSpinner = false,
                    AllowSpin = false,
                    FormatString = "0",
                    Value = value,
                    Width = 78,
                    Height = 36,
                    Padding = new Thickness(8, 4),
                    Background = editorBackground,
                    BorderBrush = editorBorder,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Foreground = PrimaryText,
                    FontSize = 15,
                    FontWeight = FontWeight.SemiBold,
                    TextAlignment = TextAlignment.Right,
                    VerticalContentAlignment = VerticalAlignment.Center
                };
                editor.Resources["TextControlBackground"] = editorBackground;
                editor.Resources["TextControlBackgroundPointerOver"] = editorBackground;
                editor.Resources["TextControlBackgroundFocused"] = editorBackground;
                editor.Resources["TextControlBorderBrush"] = editorBorder;
                editor.Resources["TextControlBorderBrushPointerOver"] = editorBorder;
                editor.Resources["TextControlBorderBrushFocused"] = Accent;
                editor.Resources["TextControlThemePadding"] = new Thickness(8, 4);
                AutomationProperties.SetName(editor, $"{title} 歌词偏移毫秒");

                var syncing = false;
                void SyncControls(int updatedValue)
                {
                    syncing = true;
                    slider.Value = updatedValue;
                    editor.Value = updatedValue;
                    syncing = false;
                }

                void UpdateOffset(int updatedValue)
                {
                    if (syncing)
                        return;
                    updatedValue = SettingsStore.NormalizeLyricsOffsetMs(updatedValue);
                    var latest = currentSettings();
                    var previousValue = latest.TrackOffsetFor(trackId);
                    SyncControls(updatedValue);
                    if (!Commit(
                            latest.WithTrackOffset(trackId, title, updatedValue),
                            trackOffsetsChanged: true))
                        SyncControls(previousValue);
                }

                slider.ValueChanged += (_, args) =>
                    UpdateOffset((int)Math.Round(args.NewValue));
                editor.ValueChanged += (_, _) =>
                {
                    if (editor.Value is not { } updatedValue)
                        return;
                    UpdateOffset((int)updatedValue);
                };

                var remove = new Button
                {
                    Width = 32,
                    Height = 32,
                    Padding = new Thickness(7),
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    CornerRadius = new CornerRadius(16),
                    Content = new PathIcon
                    {
                        Data = Geometry.Parse(CacheIcon),
                        Foreground = MutedText
                    }
                };
                AutomationProperties.SetName(remove, $"删除 {title} 的歌词偏移");
                ToolTip.SetTip(remove, "删除歌曲偏移");
                remove.Click += (_, _) =>
                {
                    if (Commit(
                            currentSettings().RemoveTrackOffset(trackId),
                            trackOffsetsChanged: true))
                        RefreshTrackOffsetManager();
                };
                var controls = new Grid
                {
                    Width = 286,
                    ColumnDefinitions = new ColumnDefinitions("78,4,24,10,130,8,32"),
                    Children = { editor, slider, remove }
                };
                var unit = new TextBlock
                {
                    Text = "ms",
                    Foreground = MutedText,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(unit, 2);
                controls.Children.Add(unit);
                Grid.SetColumn(slider, 4);
                Grid.SetColumn(remove, 6);
                var rowContent = new Grid
                {
                    MinHeight = 40,
                    ColumnDefinitions = new ColumnDefinitions("*,18,Auto"),
                    Children =
                    {
                        new TextBlock
                        {
                            Text = title,
                            Foreground = PrimaryText,
                            FontWeight = FontWeight.SemiBold,
                            TextTrimming = TextTrimming.CharacterEllipsis,
                            VerticalAlignment = VerticalAlignment.Center
                        },
                        controls
                    }
                };
                Grid.SetColumn(controls, 2);
                var row = new Border
                {
                    Padding = new Thickness(0, 2),
                    BorderBrush = CardBorder,
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    Child = rowContent
                };
                trackOffsetList.Children.Add(row);
            }
        }

        var enableTrackOffsets = CreateToggle(
            "启用每首歌曲歌词偏移",
            settings.EnableTrackOffsets,
            value =>
            {
                if (Commit(settings with { EnableTrackOffsets = value }))
                    RefreshTrackOffsetManager();
            });
        RefreshTrackOffsetManager();
        Activated += (_, _) =>
        {
            var latest = currentSettings();
            settings = settings with
            {
                TrackOffsetsMs = latest.TrackOffsetsMs,
                TrackOffsetTitles = latest.TrackOffsetTitles
            };
            RefreshTrackOffsetManager();
        };

        var refreshStatus = new TextBlock
        {
            Foreground = MutedText,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center
        };
        var refreshTrack = SecondaryButton("重新获取");
        refreshTrack.Click += (_, _) => refreshStatus.Text = refreshCurrentTrack()
            ? "已开始"
            : "当前没有歌曲";
        var refreshAction = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { refreshStatus, refreshTrack }
        };

        var lyrics = CreateCard(
            MusicIcon,
            "歌词",
            "控制歌词内容和时间同步",
            body: CreateSettingsBody(
                CreateSettingRow(
                    "显示翻译",
                    "有翻译时在原文下方显示",
                    CreateToggle("显示歌词翻译", settings.ShowTranslation,
                        value => Commit(settings with { ShowTranslation = value }))),
                CreateSettingRow(
                    "时间偏移",
                    "正值让歌词提前，负值让歌词延后",
                    CreateSlider(
                        "歌词时间偏移毫秒",
                        SettingsStore.MinimumLyricsOffsetMs,
                        SettingsStore.MaximumLyricsOffsetMs,
                        settings.LyricsOffsetMs,
                        50,
                        OffsetLabel,
                        value => Commit(settings with
                        {
                            LyricsOffsetMs = SettingsStore.NormalizeLyricsOffsetMs((int)value)
                        }))),
                CreateSettingRow(
                    "每首歌曲偏移",
                    "开启后可在灵动岛右键菜单中调整当前歌曲",
                    enableTrackOffsets),
                trackOffsetManager,
                CreateSettingRow(
                    "首选歌词源",
                    "首选源没有结果时继续尝试其他来源",
                    CreateChoice(
                        "首选歌词源",
                        sourceChoices,
                        sourceChoices.First(choice => choice.Value == settings.LyricsSource),
                        value => Commit(settings with { LyricsSource = value }))),
                CreateSettingRow(
                    "重新获取当前歌曲",
                    "忽略本次缓存结果，重新请求歌词和歌曲信息",
                    refreshAction)));

        var displayChoices = new List<Choice<string?>>
        {
            new(null, "主显示器（自动）")
        };
        foreach (var screen in Screens.All)
        {
            var name = string.IsNullOrWhiteSpace(screen.DisplayName)
                ? $"显示器 {screen.Bounds.Width}×{screen.Bounds.Height}"
                : screen.DisplayName;
            displayChoices.Add(new Choice<string?>(
                OverlayWindow.ScreenId(screen),
                screen.IsPrimary ? $"{name}（主屏）" : name));
        }

        var position = CreateCard(
            PositionIcon,
            "位置与交互",
            "选择显示器，并控制拖动和鼠标穿透",
            body: CreateSettingsBody(
                CreateSettingRow(
                    "显示器",
                    "未指定或显示器断开时自动使用主屏",
                    CreateChoice(
                        "岛屿所在显示器",
                        displayChoices,
                        displayChoices.FirstOrDefault(choice => choice.Value == settings.DisplayId)
                            ?? displayChoices[0],
                        value => Commit(settings with { DisplayId = value }))),
                CreateSettingRow(
                    "顶部距离",
                    "未记住拖动位置时使用",
                    CreateSlider(
                        "岛屿距离屏幕顶部",
                        SettingsStore.MinimumTopOffset,
                        SettingsStore.MaximumTopOffset,
                        settings.TopOffset,
                        10,
                        value => $"{value:0} px",
                        value => Commit(settings with
                        {
                            TopOffset = SettingsStore.NormalizeTopOffset((int)value)
                        }))),
                CreateSettingRow(
                    "记住拖动位置",
                    "下次启动回到最后一次拖动的位置",
                    CreateToggle("记住岛屿位置", settings.RememberPosition,
                        value => Commit(settings with
                        {
                            RememberPosition = value,
                            WindowX = value ? settings.WindowX : null,
                            WindowY = value ? settings.WindowY : null
                        }))),
                CreateSettingRow(
                    "锁定位置",
                    "禁止鼠标拖动，双击隐藏仍可使用",
                    CreateToggle("锁定岛屿位置", settings.LockPosition,
                        value => Commit(settings with { LockPosition = value }))),
                CreateSettingRow(
                    "鼠标完全穿透",
                    "开启后岛屿不接收鼠标操作，可从设置窗口关闭",
                    CreateToggle("鼠标完全穿透", settings.ClickThrough,
                        value => Commit(settings with { ClickThrough = value })))));

        var pausedChoices = new[]
        {
            new Choice<PausedDisplayMode>(PausedDisplayMode.HideImmediately, "立即隐藏"),
            new Choice<PausedDisplayMode>(PausedDisplayMode.HideAfterThreeSeconds, "3 秒后隐藏"),
            new Choice<PausedDisplayMode>(PausedDisplayMode.KeepVisible, "保持显示")
        };
        var hideChoices = new[]
        {
            new Choice<int>(2, "2 秒"),
            new Choice<int>(5, "5 秒"),
            new Choice<int>(10, "10 秒"),
            new Choice<int>(30, "30 秒")
        };
        var behavior = CreateCard(
            BehaviorIcon,
            "行为",
            "控制暂停、临时隐藏和系统启动行为",
            body: CreateSettingsBody(
                CreateSettingRow(
                    "暂停时",
                    "只影响暂停或当前没有播放，不隐藏错误提示",
                    CreateChoice(
                        "暂停时显示方式",
                        pausedChoices,
                        pausedChoices.First(choice => choice.Value == settings.PausedDisplay),
                        value => Commit(settings with { PausedDisplay = value }))),
                CreateSettingRow(
                    "临时隐藏时长",
                    "双击岛屿或使用右键菜单时生效",
                    CreateChoice(
                        "临时隐藏时长",
                        hideChoices,
                        hideChoices.First(choice => choice.Value == settings.TemporaryHideSeconds),
                        value => Commit(settings with { TemporaryHideSeconds = value }))),
                CreateSettingRow(
                    "登录后自动启动",
                    "使用桌面环境的自启动目录",
                    CreateToggle("登录后自动启动", settings.StartOnLogin,
                        value => Commit(settings with { StartOnLogin = value })))));
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
            if (!Commit(updated, restartPlayback: true))
            {
                credentialStatus.Text = "保存失败，请查看终端错误";
                return;
            }

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
        var playbackSourceChoices = new[]
        {
            new Choice<PlaybackSourcePreference>(PlaybackSourcePreference.Spotify, "Spotify"),
            new Choice<PlaybackSourcePreference>(PlaybackSourcePreference.Mpris, "本地 MPRIS")
        };
        var playbackSourceStatus = new TextBlock
        {
            Foreground = MutedText,
            FontSize = 12
        };
        var resettingPlaybackSource = false;
        ComboBox playbackSourceChoice = null!;
        playbackSourceChoice = CreateChoice(
            "播放信息源",
            playbackSourceChoices,
            playbackSourceChoices.First(choice => choice.Value == settings.PlaybackSource),
            value =>
            {
                if (resettingPlaybackSource)
                    return;
                var previous = settings.PlaybackSource;
                if (Commit(settings with { PlaybackSource = value }))
                {
                    spotifyForm.IsVisible = value == PlaybackSourcePreference.Spotify;
                    playbackSourceStatus.Text = string.Empty;
                    return;
                }

                resettingPlaybackSource = true;
                playbackSourceChoice.SelectedItem = playbackSourceChoices
                    .First(choice => choice.Value == previous);
                resettingPlaybackSource = false;
                playbackSourceStatus.Text = "保存失败，请查看终端错误";
            });
        spotifyForm.IsVisible = settings.PlaybackSource == PlaybackSourcePreference.Spotify;
        var playbackSource = CreateCard(
            MusicIcon,
            "播放信息源",
            "选择 Spotify Web API 或本机播放器提供的 MPRIS 信息",
            body: new StackPanel
            {
                Children =
                {
                    CreateSettingsBody(CreateSettingRow(
                        "当前来源",
                        "MPRIS 不需要 Spotify 参数",
                        playbackSourceChoice),
                    playbackSourceStatus),
                    spotifyForm
                }
            });

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
                Children = { appearance, lyrics, position, behavior, playbackSource, cache }
            }
        };
    }

    private static Grid CreateSlider(
        string accessibleName,
        double minimum,
        double maximum,
        double value,
        double largeChange,
        Func<double, string> formatter,
        Action<double> changed)
    {
        var valueLabel = new TextBlock
        {
            Text = formatter(value),
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
            Foreground = PrimaryText,
            MinWidth = 75,
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
            valueLabel.Text = formatter(args.NewValue);
            changed(args.NewValue);
        };
        var control = new Grid
        {
            Width = 285,
            ColumnDefinitions = new ColumnDefinitions("75,12,*"),
            VerticalAlignment = VerticalAlignment.Center,
            Children = { valueLabel, slider }
        };
        Grid.SetColumn(slider, 2);
        return control;
    }

    private static ToggleSwitch CreateToggle(string accessibleName, bool value, Action<bool> changed)
    {
        var toggle = new ToggleSwitch
        {
            IsChecked = value,
            OnContent = "开",
            OffContent = "关",
            VerticalAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetName(toggle, accessibleName);
        toggle.IsCheckedChanged += (_, _) => changed(toggle.IsChecked == true);
        return toggle;
    }

    private static ComboBox CreateChoice<T>(
        string accessibleName,
        IReadOnlyList<Choice<T>> choices,
        Choice<T> selected,
        Action<T> changed)
    {
        var combo = new ComboBox
        {
            ItemsSource = choices,
            SelectedItem = selected,
            MinWidth = 220,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        AutomationProperties.SetName(combo, accessibleName);
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is Choice<T> choice)
                changed(choice.Value);
        };
        return combo;
    }

    private static StackPanel CreateSettingsBody(params Control[] rows)
    {
        var body = new StackPanel
        {
            Margin = new Thickness(37, 12, 0, 2),
            Spacing = 12
        };
        foreach (var row in rows)
            body.Children.Add(row);
        return body;
    }

    private static Grid CreateSettingRow(string title, string description, Control action)
    {
        var labels = new StackPanel
        {
            Spacing = 1,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock
                {
                    Text = title,
                    Foreground = PrimaryText,
                    FontWeight = FontWeight.SemiBold,
                    TextTrimming = TextTrimming.CharacterEllipsis
                },
                new TextBlock
                {
                    Text = description,
                    Foreground = MutedText,
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap
                }
            }
        };
        var row = new Grid
        {
            MinHeight = 45,
            ColumnDefinitions = new ColumnDefinitions("*,18,Auto"),
            Children = { labels, action }
        };
        Grid.SetColumn(action, 2);
        return row;
    }

    private static string OffsetLabel(double value) => value switch
    {
        > 0 => $"提前 {value:0} ms",
        < 0 => $"延后 {-value:0} ms",
        _ => "0 ms"
    };

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

    private sealed record Choice<T>(T Value, string Label)
    {
        public override string ToString() => Label;
    }
}
