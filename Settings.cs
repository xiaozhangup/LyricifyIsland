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

public enum LyricsSourcePreference
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
    PlaybackSourcePreference PlaybackSource = PlaybackSourcePreference.Spotify,
    bool ShowIsland = true,
    int MaximumFrameRate = 0,
    VerticalSyncMode VerticalSync = VerticalSyncMode.Enabled,
    bool BackgroundBlur = false)
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
    public const int MinimumFrameRate = 30;
    public const int MaximumFrameRate = 360;
    public const int UnlimitedFrameRateSliderValue = MaximumFrameRate + 1;

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
                data?.PlaybackSource ?? PlaybackSourcePreference.Spotify,
                data?.ShowIsland ?? true,
                data?.MaximumFrameRate ?? 0,
                data?.VerticalSync ?? VerticalSyncMode.Enabled,
                data?.BackgroundBlur ?? false));
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
                    PlaybackSource = settings.PlaybackSource,
                    ShowIsland = settings.ShowIsland,
                    MaximumFrameRate = settings.MaximumFrameRate,
                    VerticalSync = settings.VerticalSync,
                    BackgroundBlur = settings.BackgroundBlur
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

    internal static int NormalizeFrameRate(int value) => value <= 0 ? 0
        : Math.Clamp(value, MinimumFrameRate, MaximumFrameRate);

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
            MaximumFrameRate = NormalizeFrameRate(settings.MaximumFrameRate),
            VerticalSync = Enum.IsDefined(settings.VerticalSync) ? settings.VerticalSync : VerticalSyncMode.Enabled,
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
        public bool? ShowIsland { get; init; }
        public int? MaximumFrameRate { get; init; }
        public VerticalSyncMode? VerticalSync { get; init; }
        public bool? BackgroundBlur { get; init; }
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
