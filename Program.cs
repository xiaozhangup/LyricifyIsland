using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using SkiaSharp;
using Tmds.DBus.Protocol;

namespace LyricifyIsland;

internal sealed record LaunchOptions(bool Demo, string? SnapshotPath, double? ExitAfterSeconds)
{
    public static LaunchOptions Parse(string[] args)
    {
        string? ValueAfter(string name)
        {
            var index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }

        var exitAfter = double.TryParse(ValueAfter("--exit-after"), out var seconds) ? (double?)seconds : null;
        return new LaunchOptions(args.Contains("--demo"), ValueAfter("--snapshot"), exitAfter);
    }
}

internal static class Program
{
    internal static LaunchOptions Options { get; private set; } = new(false, null, null);

    [STAThread]
    public static int Main(string[] args)
    {
        Options = LaunchOptions.Parse(args);
        if (args.Contains("--self-test"))
            return SelfCheck.Run();
        if (Options.SnapshotPath is { } bannerPath && args.Contains("--banner"))
        {
            BuildAvaloniaApp().SetupWithoutStarting();
            var track = DemoSource.Track;
            var png = SongBanner.Render(track, track.Lyrics[0], args.Contains("--portrait"), true, null);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(bannerPath))!);
            File.WriteAllBytes(bannerPath, png);
            Console.WriteLine(Path.GetFullPath(bannerPath));
            return 0;
        }
        if (Options.SnapshotPath is not null)
            return WriteSnapshot(Options.SnapshotPath);

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder
        .Configure<App>()
        .UsePlatformDetect();

    private static int WriteSnapshot(string path)
    {
        var track = DemoSource.Track;
        const long position = 2_850;
        var line = track.Lyrics[IslandControl.FindLine(track.Lyrics, position)];
        using var surface = SKSurface.Create(new SKImageInfo(960, 116, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var renderer = new IslandRenderer();
        renderer.Draw(surface.Canvas, 960, 116,
            new IslandFrame(track, line, null, position, true, "演示模式", 99, 1d));
        using var image = surface.Snapshot();
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var output = File.Create(path);
        encoded.SaveTo(output);
        Console.WriteLine(Path.GetFullPath(path));
        return 0;
    }
}

public sealed class App : Application
{
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _playbackRestart = new(1, 1);
    private TrayIcon? _trayIcon;
    private SettingsWindow? _settingsWindow;
    private OverlayWindow? _overlay;
    private WindowIcon? _appIcon;
    private PlaybackStore? _store;
    private IPlaybackSource? _playbackSource;
    private CancellationTokenSource? _playbackCancellation;
    private Task? _playbackTask;
    private IslandSettings _settings = new();

    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        var theme = new FluentTheme();
        theme.Palettes[ThemeVariant.Dark] = new ColorPaletteResources
        {
            Accent = Avalonia.Media.Color.Parse("#1DB954")
        };
        Styles.Add(theme);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _store = new PlaybackStore();
            _settings = SettingsStore.Load();
            if (_settings.StartOnLogin)
                Autostart.SetEnabled(true);
            _overlay = new OverlayWindow(
                _store, _settings, SavePosition, UpdateTrackOffset, ShowSettings, () => desktop.Shutdown());
            desktop.MainWindow = _overlay;
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            CreateTrayIcon(desktop);

            if (Program.Options.Demo)
                _ = DemoSource.RunAsync(_store, _shutdown.Token);
            else
                _ = RestartPlaybackAsync();

            if (Program.Options.ExitAfterSeconds is { } seconds)
                _ = ExitLaterAsync(desktop, seconds, _shutdown.Token);

            // Avalonia 12.1.1's Linux D-Bus tray can throw while Dispose cancels its watcher.
            // Process exit releases the D-Bus registration, so keep the icon alive until then.
            desktop.Exit += (_, _) => _shutdown.Cancel();
        }
        base.OnFrameworkInitializationCompleted();
    }

    private void CreateTrayIcon(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var settings = new NativeMenuItem("设置");
        settings.Click += (_, _) => Dispatcher.UIThread.Post(ShowSettings);
        var exit = new NativeMenuItem("退出");
        exit.Click += (_, _) => Dispatcher.UIThread.Post(() => desktop.Shutdown());
        var menu = new NativeMenu();
        menu.Add(settings);
        menu.Add(exit);

        using var iconStream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("LyricifyIsland.icon.png")
            ?? throw new InvalidOperationException("Embedded tray icon is missing");
        _appIcon = new WindowIcon(iconStream);
        _trayIcon = new TrayIcon
        {
            Icon = _appIcon,
            ToolTipText = "Lyricify Island",
            Menu = menu,
            IsVisible = true
        };
    }

    private void ShowSettings()
    {
        if (_settingsWindow is not null)
        {
            if (_settingsWindow.WindowState == WindowState.Minimized)
                _settingsWindow.WindowState = WindowState.Normal;
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(
            _settings,
            _appIcon!,
            ApplySettings,
            () => _settings,
            RefreshCurrentTrack);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
    }

    private bool ApplySettings(IslandSettings settings, bool restartPlayback)
    {
        var previous = _settings;
        if (settings.RememberPosition && _settings.RememberPosition)
            settings = settings with { WindowX = _settings.WindowX, WindowY = _settings.WindowY };
        settings = SettingsStore.Normalize(settings);
        var autostartChanged = settings.StartOnLogin != _settings.StartOnLogin;
        if (autostartChanged && !Autostart.SetEnabled(settings.StartOnLogin))
            return false;
        if (!SettingsStore.Save(settings))
        {
            if (autostartChanged)
                Autostart.SetEnabled(_settings.StartOnLogin);
            return false;
        }

        _settings = settings;
        _overlay?.ApplySettings(_settings);
        if (Volatile.Read(ref _playbackSource) is { } source)
        {
            source.SetLyricsSource(_settings.LyricsSource);
            if (previous.LyricsSource != _settings.LyricsSource)
                source.RequestCurrentTrackRefresh();
        }
        if ((restartPlayback || previous.PlaybackSource != _settings.PlaybackSource)
            && !Program.Options.Demo)
            _ = RestartPlaybackAsync();
        return true;
    }

    private bool RefreshCurrentTrack() =>
        Volatile.Read(ref _playbackSource)?.RequestCurrentTrackRefresh() == true;

    private bool UpdateTrackOffset(TrackInfo track, int offsetMs) =>
        ApplySettings(
            offsetMs == 0 ? _settings.RemoveTrackOffset(track.Id) : _settings.WithTrackOffset(track, offsetMs),
            restartPlayback: false);

    private void SavePosition(PixelPoint position)
    {
        if (!_settings.RememberPosition)
            return;
        var updated = _settings with { WindowX = position.X, WindowY = position.Y };
        if (SettingsStore.Save(updated))
            _settings = updated;
    }

    private async Task RestartPlaybackAsync()
    {
        await _playbackRestart.WaitAsync();
        try
        {
            _playbackCancellation?.Cancel();
            if (_playbackTask is not null)
            {
                try
                {
                    await _playbackTask;
                }
                catch (OperationCanceledException)
                {
                }
            }
            _playbackCancellation?.Dispose();
            _playbackCancellation = null;
            _playbackTask = null;
            Volatile.Write(ref _playbackSource, null);

            if (_shutdown.IsCancellationRequested)
                return;

            if (_settings.PlaybackSource == PlaybackSourcePreference.Mpris)
            {
                _store!.Update(new PlaybackSnapshot(
                    null, 0, Stopwatch.GetTimestamp(), false, "正在连接 MPRIS…"));
                StartPlaybackSource(new MprisService(_store, _settings.LyricsSource));
                return;
            }

            var configured = _settings.HasSpotifyCredentials;
            _store!.Update(new PlaybackSnapshot(
                null,
                0,
                Stopwatch.GetTimestamp(),
                false,
                configured ? "正在连接 Spotify…" : PlaybackStore.MissingSpotifyCredentialsStatus));
            if (!configured)
                return;

            StartPlaybackSource(new SpotifyService(
                _store,
                _settings.SpotifyClientId,
                _settings.SpotifyClientSecret,
                _settings.LyricsSource));
        }
        finally
        {
            _playbackRestart.Release();
        }
    }

    private void StartPlaybackSource(IPlaybackSource source)
    {
        _playbackCancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        var cancellationToken = _playbackCancellation.Token;
        Volatile.Write(ref _playbackSource, source);
        _playbackTask = Task.Run(() => source.RunAsync(cancellationToken));
    }

    private static async Task ExitLaterAsync(
        IClassicDesktopStyleApplicationLifetime desktop,
        double seconds,
        CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken);
        desktop.Shutdown();
    }
}

internal static class DemoSource
{
    public static TrackInfo Track { get; } = BuildTrack();

    public static async Task RunAsync(PlaybackStore store, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            store.Update(new PlaybackSnapshot(Track, 0, Stopwatch.GetTimestamp(), true, "演示模式"));
            await Task.Delay(TimeSpan.FromMilliseconds(Track.DurationMs), cancellationToken);
        }
    }

    private static TrackInfo BuildTrack()
    {
        var lines = ImmutableArray.Create(
            Line("One step one day for a better future", "一步一个脚印 每一天都为了更美好的未来而战", 0, 6_950,
                ("One ", 0, 580), ("step ", 580, 1_250), ("one ", 1_250, 1_810),
                ("day ", 1_810, 2_650), ("for ", 2_650, 3_180), ("a ", 3_180, 3_470),
                ("better ", 3_470, 4_720), ("future", 4_720, 5_900)),
            Line("All our colors 世界中に溢れ出す", "我们所有的色彩 正在向着全世界溢出", 6_950, 12_850,
                ("All ", 6_950, 7_500), ("our ", 7_500, 8_050), ("colors ", 8_050, 9_170),
                ("世界中に", 9_170, 10_700), ("溢れ出す", 10_700, 12_250)),
            Line("Come together 真っ白なキャンバス彩るよ", "相聚在一起 共同将这纯白的画布装点绚烂", 12_850, 19_000,
                ("Come ", 12_850, 13_570), ("together ", 13_570, 14_720),
                ("真っ白な", 14_720, 16_000), ("キャンバス", 16_000, 17_550), ("彩るよ", 17_550, 18_700)));

        return new TrackInfo(
            "demo",
            "Dream Rig",
            ImmutableArray.Create("Crystal Statues", "Crescent", "kerosene"),
            "D4DJ Groovy Mix Cover Tracks",
            19_000,
            DemoAlbum(),
            lines)
        {
            ArtistImageBytes =
            [
                DemoAvatar("CS", new SKColor(73, 101, 126), new SKColor(205, 155, 130)),
                DemoAvatar("CR", new SKColor(119, 68, 107), new SKColor(233, 166, 105)),
                DemoAvatar("K", new SKColor(41, 95, 74), new SKColor(169, 204, 153))
            ]
        };
    }

    private static LyricLine Line(
        string text,
        string translation,
        long start,
        long end,
        params (string Text, long Start, long End)[] syllables) => new(
            text,
            translation,
            start,
            end,
            syllables.Select(s => new LyricSyllable(s.Text, s.Start, s.End)).ToImmutableArray());

    private static ImmutableArray<byte> DemoAlbum()
    {
        using var surface = SKSurface.Create(new SKImageInfo(96, 96));
        using var background = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, 0), new SKPoint(96, 96),
                [new SKColor(240, 237, 220), new SKColor(89, 115, 132), new SKColor(235, 205, 196)],
                (float[]?)null, SKShaderTileMode.Clamp)
        };
        surface.Canvas.DrawRect(new SKRect(0, 0, 96, 96), background);
        using var font = new SKFont(SKTypeface.FromFamilyName("Montserrat", SKFontStyle.Bold), 18);
        using var text = new SKPaint { IsAntialias = true, Color = new SKColor(24, 29, 34) };
        surface.Canvas.DrawText("Dream", 8, 30, font, text);
        surface.Canvas.DrawText("Rig", 8, 51, font, text);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray().ToImmutableArray();
    }

    private static ImmutableArray<byte> DemoAvatar(string initials, SKColor top, SKColor bottom)
    {
        using var surface = SKSurface.Create(new SKImageInfo(96, 96));
        using var background = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(10, 0), new SKPoint(86, 96), [top, bottom],
                (float[]?)null, SKShaderTileMode.Clamp)
        };
        surface.Canvas.DrawRect(new SKRect(0, 0, 96, 96), background);
        using var font = new SKFont(SKTypeface.FromFamilyName("Montserrat", SKFontStyle.Bold), 31);
        using var text = new SKPaint { IsAntialias = true, Color = new SKColor(255, 255, 255, 235) };
        var width = font.MeasureText(initials);
        surface.Canvas.DrawText(initials, 48f - width / 2f, 59f, font, text);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray().ToImmutableArray();
    }
}

internal static class SelfCheck
{
    public static int Run()
    {
        var track = DemoSource.Track;
        Require(IslandControl.FindLine(track.Lyrics, 0) == 0, "first line lookup");
        Require(IslandControl.FindLine(track.Lyrics, 7_000) == 1, "line transition lookup");
        Require(IslandControl.FindLine(track.Lyrics, -1) == -1, "pre-roll lookup");
        const string spotifyId = "4uLU6hMCjMI75M1A2tKUQC";
        using (var small = SKSurface.Create(new SKImageInfo(16, 16)))
        using (var smallImage = small.Snapshot())
        using (var smallPng = smallImage.Encode(SKEncodedImageFormat.Png, 100))
        {
            var bytes = smallPng.ToArray().ToImmutableArray();
            Require(TrackCache.PreferLargerImage(track.AlbumArtBytes, bytes) == track.AlbumArtBytes
                    && TrackCache.PreferLargerImage(bytes, track.AlbumArtBytes) == track.AlbumArtBytes
                    && TrackCache.PreferLargerImage(track.AlbumArtBytes, [1, 2, 3]) == track.AlbumArtBytes,
                "album cache keeps the larger valid image");
        }
        Require(SongBanner.MeasureHeight(true, 1400, false)
                    < SongBanner.MeasureHeight(true, 1400, true)
                && SongBanner.MeasureHeight(true, 1500, false)
                    > SongBanner.MeasureHeight(true, 1400, false),
            "portrait banner fits text and optional brand footer");
        Require(SongBanner.SpotifyUrl(spotifyId, PlaybackSourcePreference.Spotify)
                    == $"https://open.spotify.com/track/{spotifyId}"
                && SongBanner.SpotifyUrl($"spotify:track:{spotifyId}", PlaybackSourcePreference.Spotify)
                    == $"https://open.spotify.com/track/{spotifyId}"
                && SongBanner.SpotifyUrl(spotifyId, PlaybackSourcePreference.Mpris) is null
                && SongBanner.SpotifyUrl("spotify:local:artist:album:song", PlaybackSourcePreference.Spotify) is null
                && SongBanner.SpotifyUrl("../../../../etc/passwd!", PlaybackSourcePreference.Spotify) is null,
            "banner QR only links valid Spotify tracks from the Spotify source");
        Require(track.Lyrics.All(line => line.Syllables.IsDefaultOrEmpty
            || string.Concat(line.Syllables.Select(syllable => syllable.Text)) == line.Text),
            "syllable text invariant");
        Require(IslandRenderer.DesiredPillHeight(track.Lyrics[0] with { Translation = null })
                < IslandRenderer.DesiredPillHeight(track.Lyrics[0]),
            "single-line island height");
        Require(IslandRenderer.DesiredPillHeight(track.Lyrics[0], showTranslation: false)
                == IslandRenderer.DesiredPillHeight(track.Lyrics[0] with { Translation = null }),
            "hidden translation island height");

        var peak = Enumerable.Range(0, 101).Max(i => IslandRenderer.Spring(i / 100d));
        Require(peak is > 1.15 and < 1.22, "spring overshoot");
        Require(Math.Abs(IslandRenderer.Spring(1) - 1) < 0.0001, "spring settles");

        Require(IslandRenderer.AnimatedCapsuleProgress(1d, 0d, 0d) == 1d
                && Math.Abs(IslandRenderer.AnimatedCapsuleProgress(
                    1d, 0d, IslandRenderer.CapsuleTransitionDuration / 2d) - 0.5d) < 0.0001d
                && IslandRenderer.AnimatedCapsuleProgress(
                    1d, 0d, IslandRenderer.CapsuleTransitionDuration) == 0d
                && IslandRenderer.AnimatedCapsuleProgress(0.4d, 1d, 0d) == 0.4d,
            "capsule pause animation");
        Require(!IslandControl.ShouldShowCapsule(true, PausedDisplayMode.HideImmediately, 0d)
                && IslandControl.ShouldShowCapsule(true, PausedDisplayMode.HideAfterThreeSeconds, 2.99d)
                && !IslandControl.ShouldShowCapsule(true, PausedDisplayMode.HideAfterThreeSeconds, 3d)
                && IslandControl.ShouldShowCapsule(true, PausedDisplayMode.KeepVisible, 99d)
                && IslandControl.ShouldShowCapsule(false, PausedDisplayMode.HideImmediately, 99d),
            "paused display modes");
        Require(PlaybackStore.RequiresAttention(PlaybackStore.MissingSpotifyCredentialsStatus)
                && PlaybackStore.RequiresAttention("Spotify 连接失败：timeout")
                && PlaybackStore.RequiresAttention("MPRIS 连接失败：session bus unavailable")
                && !PlaybackStore.RequiresAttention(PlaybackStore.NoActiveSpotifyPlaybackStatus)
                && !PlaybackStore.RequiresAttention(PlaybackStore.NoActiveMprisPlaybackStatus)
                && !PlaybackStore.RequiresAttention("演示模式"),
            "playback status visibility");
        var fullCapsule = new Rect(100d, 20d, 400d, 90d);
        var halfCapsule = IslandRenderer.ScalePillBounds(fullCapsule, 0.5d);
        var hiddenCapsule = IslandRenderer.ScalePillBounds(fullCapsule, 0d);
        Require(halfCapsule.Center == fullCapsule.Center
                && halfCapsule.Width == 200d && halfCapsule.Height == 45d
                && hiddenCapsule.Center == fullCapsule.Center
                && hiddenCapsule.Width == 0d && hiddenCapsule.Height == 0d
                && !IslandRenderer.HitTest(hiddenCapsule, hiddenCapsule.Center),
            "capsule pause bounds");

        var icon = IslandRenderer.ArtistAvatarState(0, 3);
        var fadingIcon = IslandRenderer.ArtistAvatarState(1.1, 3);
        var blankAvatar = IslandRenderer.ArtistAvatarState(1.23, 3);
        var firstAvatar = IslandRenderer.ArtistAvatarState(1.6, 3);
        var secondAvatar = IslandRenderer.ArtistAvatarState(4.6, 3);
        var restoredIcon = IslandRenderer.ArtistAvatarState(10.6, 3);
        Require(icon == (-1, 1f)
                && fadingIcon.Index == -1 && fadingIcon.Opacity is > 0f and < 1f
                && blankAvatar.Opacity == 0f
                && firstAvatar == (0, 1f)
                && secondAvatar == (1, 1f)
                && restoredIcon == (-1, 1f),
            "artist avatar carousel");

        using var font = new SKFont(SKTypeface.FromFamilyName("Noto Sans"), 32);
        var line = track.Lyrics[0];
        var partial = IslandRenderer.ActiveWidth(line, 300, font);
        Require(partial > 0 && partial < font.MeasureText(line.Text), "syllable progress");
        Require(IslandRenderer.HighlightAgeProgress(0) == 0f
                && IslandRenderer.HighlightAgeProgress(650) is > 0.62f and < 0.64f
                && IslandRenderer.HighlightAgeProgress(650.5) > IslandRenderer.HighlightAgeProgress(650)
                && IslandRenderer.HighlightAgeProgress(2_000) > 0.95f,
            "post-highlight lift timing");
        var reportedAt = Stopwatch.GetTimestamp();
        var previousPlayback = new PlaybackSnapshot(
            track, 10_000, reportedAt - Stopwatch.Frequency, true, "test");
        Require(IslandControl.ApplyLyricsOffset(previousPlayback, 500d, 250) == 10_750d
                && IslandControl.ApplyLyricsOffset(previousPlayback with { ReportedPositionMs = 100 }, 0d, -500) == 0d,
            "lyrics offset");
        Require(IslandControl.ApplyLyricsOffset(
                previousPlayback with { Track = track with { DurationMs = 0 } }, 500d, 0) == 10_500d,
            "unknown track duration");
        Require(SpotifyService.StabilizePosition(
                previousPlayback, track.Id, 8_000, reportedAt, true, true) == 11_000,
            "stale playback position");
        Require(SpotifyService.StabilizePosition(
                previousPlayback, track.Id, 8_000, reportedAt, true, false) == 8_000,
            "forced lyrics position resync");
        Require(SpotifyService.StabilizePosition(
                previousPlayback, track.Id, null, reportedAt, true, true) == 11_000,
            "missing playback position");
        var nearTrackEnd = previousPlayback with
        {
            ReportedPositionMs = track.DurationMs - 4_000,
            ReportedAtTimestamp = reportedAt - Stopwatch.Frequency
        };
        Require(SpotifyService.ParseSpotifyCrossfadeMs(
                    "audio.crossfade_v2=true\naudio.crossfade.time_v2=3000\n") == 3_000
                && SpotifyService.ParseSpotifyCrossfadeMs(
                    "audio.crossfade_v2=false\naudio.crossfade.time_v2=3000\n") == 0
                && SpotifyService.IsAutomaticCrossfadeTransition(
                    nearTrackEnd, reportedAt, true, 3_000)
                && !SpotifyService.IsAutomaticCrossfadeTransition(
                    previousPlayback, reportedAt, true, 3_000),
            "Spotify crossfade transition compensation");
        Require(SettingsStore.NormalizeWidthPercent(double.NaN) == 70d, "invalid width setting");
        Require(SettingsStore.NormalizeWidthPercent(12d) == 40d, "minimum width setting");
        Require(SettingsStore.NormalizeWidthPercent(120d) == 100d, "maximum width setting");
        Require(SettingsStore.NormalizeScalePercent(double.NaN) == 100d, "invalid scale setting");
        Require(SettingsStore.NormalizeScalePercent(12d) == 50d, "minimum scale setting");
        Require(SettingsStore.NormalizeScalePercent(220d) == 200d, "maximum scale setting");
        Require(SettingsStore.NormalizeTopOffset(-1) == 0
                && SettingsStore.NormalizeLyricsOffsetMs(3_000) == 2_000
                && SettingsStore.NormalizeBackgroundOpacityPercent(10d) == 35d
                && SettingsStore.NormalizeTemporaryHideSeconds(7) == 2,
            "custom setting bounds");
        Require(IslandRenderer.BackgroundAlpha(95d) == 242
                && IslandRenderer.BackgroundAlpha(35d) == 89,
            "background opacity");
        var configured = SettingsStore.Normalize(new IslandSettings(70d, 100d, " client ", " secret "));
        Require(configured.HasSpotifyCredentials
                && configured.SpotifyClientId == "client"
                && configured.SpotifyClientSecret == "secret",
            "Spotify credential settings");
        Require(!(configured with { SpotifyClientSecret = string.Empty }).HasSpotifyCredentials,
            "incomplete Spotify credential settings");
        Require(new IslandSettings().PlaybackSource == PlaybackSourcePreference.Spotify
                && SettingsStore.Normalize(configured with
                {
                    PlaybackSource = (PlaybackSourcePreference)999
                }).PlaybackSource == PlaybackSourcePreference.Spotify,
            "playback source setting");
        var trackOffsets = (configured with { EnableTrackOffsets = true }).WithTrackOffset(track, 350);
        var zeroedTrackOffsets = trackOffsets.WithTrackOffset(track, 0);
        var removedTrackOffsets = zeroedTrackOffsets.RemoveTrackOffset(track.Id);
        Require(trackOffsets.TrackOffsetFor(track.Id) == 350
                && trackOffsets.TrackOffsetFor("other") == 0
                && trackOffsets.TrackOffsetTitleFor(track.Id).StartsWith(track.Title, StringComparison.Ordinal)
                && (trackOffsets with { EnableTrackOffsets = false }).TrackOffsetFor(track.Id) == 350
                && zeroedTrackOffsets.TrackOffsetsMs?.ContainsKey(track.Id) == true
                && zeroedTrackOffsets.TrackOffsetFor(track.Id) == 0
                && zeroedTrackOffsets.TrackOffsetTitleFor(track.Id).StartsWith(track.Title, StringComparison.Ordinal)
                && removedTrackOffsets.TrackOffsetsMs is null
                && removedTrackOffsets.TrackOffsetTitles is null,
            "per-track lyrics offset");
        Require(LyricsProvider.ProviderOrder(LyricsSourcePreference.Kugou)
                    .SequenceEqual([
                        LyricsSourcePreference.Kugou,
                        LyricsSourcePreference.Netease,
                        LyricsSourcePreference.Lrclib])
                && LyricsProvider.ProviderOrder(LyricsSourcePreference.Lrclib)[0]
                    == LyricsSourcePreference.Lrclib,
            "lyrics provider preference");
        var refreshStore = new PlaybackStore();
        var refreshService = new SpotifyService(
            refreshStore, string.Empty, string.Empty, LyricsSourcePreference.Automatic);
        Require(!refreshService.RequestCurrentTrackRefresh(), "refresh without current track");
        refreshStore.Update(previousPlayback);
        Require(refreshService.RequestCurrentTrackRefresh(), "refresh current track request");
        CheckMpris();
        CheckFileArtwork();
        CheckSettingsStore();
        CheckTrackCache(track);
        Require(OverlayWindow.CalculateLogicalHeight(150d) == 174d, "island height scaling");
        Require(Math.Abs(OverlayWindow.CalculateLogicalWidth(1_920, 1.25d, 70d) - 1_075.2d) < 0.001d,
            "screen width scaling");
        using var hitTestRenderer = new IslandRenderer();
        var hitTestFrame = new IslandFrame(track, track.Lyrics[0], null, 1_000, true, "test", 99d, 1d);
        var pillBounds = hitTestRenderer.CalculatePillBounds(960, 116, hitTestFrame);
        Require(IslandRenderer.HitTest(pillBounds, new Point(480, 58)), "island hit test");
        Require(!IslandRenderer.HitTest(pillBounds, new Point(0, 0)), "transparent hit test");
        Require(OverlayWindow.CenteredHorizontally(
                new PixelPoint(0, 321), new PixelRect(100, 0, 1_000, 500), 400)
            == new PixelPoint(400, 321), "horizontal-only centering");
        Require(OverlayWindow.ClampPosition(
                new PixelPoint(-500, 900), new PixelRect(100, 50, 1_000, 600), 400, 100)
            == new PixelPoint(100, 550), "screen position clamping");
        var inputRows = NativeOverlay.CapsuleRows(new PixelRect(10, 20, 100, 40));
        Require(inputRows.Length == 40
                && inputRows[0].X > 10
                && inputRows[20] == new PixelRect(10, 40, 100, 1),
            "rounded native input region");

        Console.WriteLine("self-test: ok");
        return 0;
    }

    private static void CheckSettingsStore()
    {
        var previousConfigHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var configHome = Path.Combine(Path.GetTempPath(), $"lyricify-island-self-test-{Guid.NewGuid():N}");
        try
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", configHome);
            var expected = new IslandSettings(
                80d,
                120d,
                "client",
                "secret",
                90,
                -350,
                false,
                55d,
                true,
                true,
                true,
                PausedDisplayMode.KeepVisible,
                10,
                true,
                "display-id",
                123,
                456) with
            {
                LyricsSource = LyricsSourcePreference.Kugou,
                EnableTrackOffsets = true,
                PlaybackSource = PlaybackSourcePreference.Mpris
            };
            expected = expected.WithTrackOffset("track-a", "测试歌曲 — 测试歌手", -275);
            expected = expected.WithTrackOffset("track-zero", "零偏移歌曲", 0);
            Require(SettingsStore.Save(expected), "settings save");
            var loaded = SettingsStore.Load();
            Require(loaded with { TrackOffsetsMs = null, TrackOffsetTitles = null }
                        == expected with { TrackOffsetsMs = null, TrackOffsetTitles = null }
                    && loaded.TrackOffsetFor("track-a") == -275
                    && loaded.TrackOffsetTitleFor("track-a") == "测试歌曲 — 测试歌手"
                    && loaded.TrackOffsetsMs?.ContainsKey("track-zero") == true
                    && loaded.TrackOffsetFor("track-zero") == 0
                    && loaded.TrackOffsetTitleFor("track-zero") == "零偏移歌曲",
                "settings round trip");
            if (!OperatingSystem.IsWindows())
            {
                var directory = Path.Combine(configHome, "lyricify-island");
                Require(File.GetUnixFileMode(directory)
                        == (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute),
                    "settings directory permissions");
                Require(File.GetUnixFileMode(Path.Combine(directory, "settings.json"))
                        == (UnixFileMode.UserRead | UnixFileMode.UserWrite),
                    "settings file permissions");
            }
            if (OperatingSystem.IsLinux())
            {
                Require(Autostart.SetEnabled(true, "/tmp/Lyricify Island/$app"), "autostart enable");
                var autostartPath = Path.Combine(configHome, "autostart", "lyricify-island.desktop");
                var desktopEntry = File.ReadAllText(autostartPath);
                Require(desktopEntry.Contains("Exec=\"/tmp/Lyricify Island/\\$app\""),
                    "autostart executable quoting");
                Require(Autostart.SetEnabled(false) && !File.Exists(autostartPath), "autostart disable");
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previousConfigHome);
            if (Directory.Exists(configHome))
                Directory.Delete(configHome, recursive: true);
        }
    }

    private static void CheckMpris()
    {
        var metadata = new Dict<string, VariantValue>
        {
            ["xesam:title"] = "narrative (feat. LiSA)",
            ["xesam:artist"] = VariantValue.Array(new[] { "SawanoHiroyuki[nZk]" }),
            ["xesam:album"] = "narrative / NOISEofRAIN - EP",
            ["mpris:length"] = 260_000_000L,
            ["mpris:artUrl"] = "file:///tmp/cover.png"
        };
        var properties = new Dictionary<string, VariantValue>
        {
            ["Metadata"] = metadata,
            ["PlaybackStatus"] = "Playing",
            ["Position"] = 122_000_000L,
            ["Rate"] = 1.25d
        };
        var playing = MprisService.ParsePlayer("org.mpris.MediaPlayer2.amberol", properties);
        Require(playing is not null
                && playing.Track.Title == "narrative (feat. LiSA)"
                && playing.Track.Artists.SequenceEqual(["SawanoHiroyuki[nZk]"])
                && playing.Track.DurationMs == 260_000
                && playing.PositionMs == 122_000
                && playing.PlaybackRate == 1.25d,
            "MPRIS metadata");

        var paused = playing! with
        {
            Service = "org.mpris.MediaPlayer2.firefox",
            PlaybackStatus = "Paused"
        };
        Require(MprisService.SelectPlayer([paused, playing], paused.Service) == playing
                && MprisService.SelectPlayer([playing, playing with
                {
                    Service = "org.mpris.MediaPlayer2.other"
                }], playing.Service) == playing,
            "MPRIS player selection");
        Require(MprisService.BuildTrackId("first", "file:///music/song.flac", null, "Song", [], "", 1)
                == MprisService.BuildTrackId("second", "file:///music/song.flac", null, "Other", [], "", 2),
            "MPRIS stable URL identity");
        var reportedAt = Stopwatch.GetTimestamp();
        var anchorTrack = new TrackInfo(
            playing.Track.Id,
            playing.Track.Title,
            playing.Track.Artists,
            playing.Track.Album,
            playing.Track.DurationMs,
            [],
            []);
        var previous = new PlaybackSnapshot(
            anchorTrack, 122_000, reportedAt - Stopwatch.Frequency, true, "test");
        var smoothPlayer = playing with { PlaybackRate = 1d };
        Require(MprisService.StabilizePosition(previous, smoothPlayer, reportedAt, true) == 123_000
                && MprisService.StabilizePosition(
                    previous, smoothPlayer with { PositionMs = 110_000 }, reportedAt, true) == 110_000
                && MprisService.StabilizePosition(previous, smoothPlayer, reportedAt, false) == 122_000,
            "MPRIS continuous position");
    }

    private static void CheckFileArtwork()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lyricify-island-art-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllBytes(path, [1, 2, 3]);
            var bytes = SpotifyService.LoadImageSafeAsync(new Uri(path).AbsoluteUri, CancellationToken.None)
                .GetAwaiter().GetResult();
            Require(bytes.SequenceEqual(new byte[] { 1, 2, 3 }), "MPRIS file artwork");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void CheckTrackCache(TrackInfo track)
    {
        var legacy = JsonSerializer.Deserialize<TrackInfo>(
            """{"Id":"legacy","Title":"Legacy","Artists":[],"Album":"","DurationMs":1,"AlbumArtBytes":[],"Lyrics":[]}""");
        Require(legacy is not null && legacy.ArtistImageBytes.IsEmpty, "legacy track cache");

        var previousCacheHome = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        var cacheHome = Path.Combine(Path.GetTempPath(), $"lyricify-island-cache-test-{Guid.NewGuid():N}");
        try
        {
            Environment.SetEnvironmentVariable("XDG_CACHE_HOME", cacheHome);
            TrackCache.SaveIfChanged(track);
            var loaded = TrackCache.Load(track.Id);
            Require(loaded is not null
                    && loaded.Title == track.Title
                    && loaded.AlbumArtBytes.SequenceEqual(track.AlbumArtBytes)
                    && loaded.ArtistImageBytes.Length == track.ArtistImageBytes.Length
                    && loaded.ArtistImageBytes[0].SequenceEqual(track.ArtistImageBytes[0])
                    && loaded.Lyrics.Length == track.Lyrics.Length
                    && loaded.Lyrics[0].Text == track.Lyrics[0].Text
                    && loaded.Lyrics[0].Syllables.Length == track.Lyrics[0].Syllables.Length,
                "track cache round trip");
            TrackCache.SaveIfChanged(track with { Title = "updated" });
            Require(TrackCache.Load(track.Id)?.Title == "updated", "track cache refresh");
            Require(TrackCache.Size() > 0, "track cache size");
            Require(TrackCache.Clear() && TrackCache.Size() == 0, "track cache clear");
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CACHE_HOME", previousCacheHome);
            if (Directory.Exists(cacheHome))
                Directory.Delete(cacheHome, recursive: true);
        }
    }

    private static void Require(bool condition, string name)
    {
        if (!condition)
            throw new InvalidOperationException($"self-test failed: {name}");
    }
}
