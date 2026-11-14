using Avalonia;
using System.Diagnostics;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace LyricifyIsland;

internal sealed class SettingsPage : PanelPage
{
    private const string SlidersIcon = "M3 6V8H14V6H3M18 4V6H21V8H18V10H16V4H18M3 16V18H6V20H8V14H6V16H3M10 16V18H21V16H10Z";
    private const string ScaleIcon = "M7 14H5V19H10V17H7V14M5 10H7V7H10V5H5V10M17 17H14V19H19V14H17V17M14 5V7H17V10H19V5H14Z";
    private const string MusicIcon = "M12 3V13.55A4 4 0 1 0 14 17V7H20V3H12Z";
    private const string LyricsIcon = "M4 6H20V8H4V6M4 11H20V13H4V11M4 16H14V18H4V16Z";
    private const string PositionIcon = "M12 2C8.13 2 5 5.13 5 9C5 14.25 12 22 12 22S19 14.25 19 9C19 5.13 15.87 2 12 2M12 11.5A2.5 2.5 0 1 1 12 6.5A2.5 2.5 0 0 1 12 11.5Z";
    private const string BehaviorIcon = "M12 2A10 10 0 1 0 22 12A10 10 0 0 0 12 2M13 7V11.59L16.2 14.79L14.79 16.2L11 12.41V7H13Z";
    private const string RenderIcon = "M13 2L3 14H12L11 22L21 10H12L13 2Z";
    private const string CacheIcon = "M15.5 4L14.5 3H9.5L8.5 4H5V6H19V4M6 19C6 20.1 6.9 21 8 21H16C17.1 21 18 20.1 18 19V7H6V19Z";

    private sealed record Choice<T>(T Value, string Label)
    {
        public override string ToString() => Label;
    }

    private Action? _onShown;
    private IReadOnlyList<Control> _entrance = [];

    public override void OnShown() => _onShown?.Invoke();

    public override IReadOnlyList<Control> EntranceTargets => _entrance;

    public SettingsPage(
        IslandSettings initialSettings,
        Func<IslandSettings, bool, bool> settingsChanged,
        Func<IslandSettings> currentSettings,
        Func<bool> refreshCurrentTrack)
    {
        var settings = SettingsStore.Normalize(initialSettings);

        bool Commit(
            IslandSettings updated,
            bool restartPlayback = false,
            bool trackOffsetsChanged = false)
        {
            var latest = currentSettings();
            updated = updated with { ShowIsland = latest.ShowIsland };
            if (!trackOffsetsChanged)
            {
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

        Segmented<T> Pick<T>(string name, (T Value, string Label)[] choices, T current, Action<T> changed)
        {
            var segmented = new Segmented<T>(name);
            segmented.Set(choices, value => EqualityComparer<T>.Default.Equals(value, current));
            segmented.Changed += changed;
            return segmented;
        }

        ToggleSwitch Switch(string name, bool value, Action<bool> changed)
        {
            var toggle = Ui.Toggle(name, value);
            toggle.IsCheckedChanged += (_, _) => changed(toggle.IsChecked == true);
            return toggle;
        }

        // ── 播放信息源 ────────────────────────────────────────────────────────────
        var clientId = Ui.Input("Spotify Client ID", "Spotify Developer Dashboard 中的 Client ID", settings.SpotifyClientId);
        var clientSecret = Ui.Input("Spotify Client Secret", "Spotify Developer Dashboard 中的 Client Secret",
            settings.SpotifyClientSecret, password: true);
        var credentialStatus = Ui.Text(string.Empty, 12, muted: true);
        credentialStatus.VerticalAlignment = VerticalAlignment.Center;
        var saveCredentials = Ui.Button("保存并重新连接", accent: true);
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
            ColumnDefinitions = new ColumnDefinitions("*,12,Auto"),
            Margin = new Thickness(0, 4, 0, 0),
            Children = { credentialStatus, saveCredentials }
        };
        Grid.SetColumn(saveCredentials, 2);
        var spotifyForm = Ui.Block(new StackPanel
        {
            Spacing = 8,
            Children =
            {
                Ui.Text("Spotify 应用参数", 14, FontWeight.SemiBold),
                Ui.Text("在 Spotify Developer Dashboard 创建应用后填写，密钥只保存在本机。", 12, muted: true),
                Ui.Text("Client ID", 12, FontWeight.Medium, true),
                clientId,
                Ui.Text("Client Secret", 12, FontWeight.Medium, true),
                clientSecret,
                credentialActions
            }
        });
        spotifyForm.IsVisible = settings.PlaybackSource == PlaybackSourcePreference.Spotify;
        var playbackSourceChoices = new[]
        {
            (PlaybackSourcePreference.Spotify, "Spotify"),
            (PlaybackSourcePreference.Mpris, "本地 MPRIS")
        };
        var playbackSourceStatus = Ui.Text(string.Empty, 12, muted: true);
        var playbackSource = new Segmented<PlaybackSourcePreference>("播放信息源");
        playbackSource.Set(playbackSourceChoices, value => value == settings.PlaybackSource);
        playbackSource.Changed += value =>
        {
            var previous = settings.PlaybackSource;
            if (Commit(settings with { PlaybackSource = value }))
            {
                spotifyForm.IsVisible = value == PlaybackSourcePreference.Spotify;
                playbackSourceStatus.Text = string.Empty;
                return;
            }

            playbackSource.Set(playbackSourceChoices, candidate => candidate == previous);
            playbackSourceStatus.Text = "保存失败，请查看终端错误";
        };
        var playbackSection = Ui.Section(
            "播放信息源",
            "选择 Spotify Web API 或本机播放器提供的 MPRIS 信息",
            Ui.Row(Ui.Text("当前来源", 14, FontWeight.SemiBold),
                playbackSourceStatus,
                playbackSource),
            spotifyForm);

        // ── 歌词 ────────────────────────────────────────────────────────────────
        var trackOffsetManagerTitle = Ui.Text(string.Empty, 14, FontWeight.SemiBold);
        var trackOffsetList = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        trackOffsetList.IsVisible = false;
        var trackOffsetManagerIndicator = Ui.Text("▾", 15, muted: true);
        trackOffsetManagerIndicator.VerticalAlignment = VerticalAlignment.Center;
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
            Content = trackOffsetManagerHeaderContent,
            Classes = { "nav" }
        };
        var trackOffsetManager = Ui.Block(new StackPanel
        {
            Children = { trackOffsetManagerHeader, trackOffsetList }
        });

        void SetTrackOffsetManagerExpanded(bool expanded)
        {
            trackOffsetList.IsVisible = expanded;
            trackOffsetManagerIndicator.Text = expanded ? "▴" : "▾";
            AutomationProperties.SetName(
                trackOffsetManagerHeader,
                expanded ? "收起已设置歌曲" : "展开已设置歌曲");
        }

        SetTrackOffsetManagerExpanded(false);
        trackOffsetManagerHeader.Click += (_, _) =>
            SetTrackOffsetManagerExpanded(!trackOffsetList.IsVisible);

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
                trackOffsetList.Children.Add(Ui.Text(
                    "还没有保存过歌曲偏移。播放歌曲后，在灵动岛上右键即可添加。", 12, muted: true));
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
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = LyricsTheme.Accent
                };
                AutomationProperties.SetName(slider, $"{title} 歌词偏移滑杆");
                var editor = new NumericUpDown
                {
                    Minimum = SettingsStore.MinimumLyricsOffsetMs,
                    Maximum = SettingsStore.MaximumLyricsOffsetMs,
                    ClipValueToMinMax = true,
                    ShowButtonSpinner = false,
                    AllowSpin = false,
                    FormatString = "0",
                    Value = value,
                    Width = 84,
                    Height = 36,
                    Padding = new Thickness(8, 4),
                    CornerRadius = new CornerRadius(10),
                    Foreground = LyricsTheme.PrimaryText,
                    FontSize = 14,
                    FontWeight = FontWeight.SemiBold,
                    TextAlignment = TextAlignment.Right,
                    VerticalContentAlignment = VerticalAlignment.Center
                };
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
                    Width = 34,
                    Height = 34,
                    Padding = new Thickness(8),
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    CornerRadius = new CornerRadius(17),
                    Content = new PathIcon { Data = Geometry.Parse(CacheIcon), Foreground = LyricsTheme.MutedText },
                    Classes = { "chrome" }
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
                var unit = Ui.Text("ms", 12, muted: true);
                unit.VerticalAlignment = VerticalAlignment.Center;
                var controls = new Grid
                {
                    Width = 330,
                    ColumnDefinitions = new ColumnDefinitions("84,6,22,10,*,8,34"),
                    Children = { editor, unit, slider, remove }
                };
                Grid.SetColumn(unit, 2);
                Grid.SetColumn(slider, 4);
                Grid.SetColumn(remove, 6);
                var name = Ui.Text(title, 13, FontWeight.Medium);
                name.TextTrimming = TextTrimming.CharacterEllipsis;
                name.TextWrapping = TextWrapping.NoWrap;
                name.VerticalAlignment = VerticalAlignment.Center;
                var rowContent = new Grid
                {
                    MinHeight = 44,
                    ColumnDefinitions = new ColumnDefinitions("*,18,Auto"),
                    Children = { name, controls }
                };
                Grid.SetColumn(controls, 2);
                trackOffsetList.Children.Add(new Border
                {
                    BorderBrush = LyricsTheme.Divider,
                    BorderThickness = new Thickness(0, 1, 0, 0),
                    Child = rowContent
                });
            }
        }

        var enableTrackOffsets = Switch("启用每首歌曲歌词偏移", settings.EnableTrackOffsets, value =>
        {
            if (Commit(settings with { EnableTrackOffsets = value }))
                RefreshTrackOffsetManager();
        });
        RefreshTrackOffsetManager();
        var refreshStatus = Ui.Text(string.Empty, 12, muted: true);
        refreshStatus.VerticalAlignment = VerticalAlignment.Center;
        var refreshTrack = Ui.Button("重新获取");
        refreshTrack.Click += (_, _) => refreshStatus.Text = refreshCurrentTrack()
            ? "已开始"
            : "当前没有歌曲";
        var refreshAction = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            Children = { refreshStatus, refreshTrack }
        };

        var lyricsSection = Ui.Section(
            "歌词",
            "控制歌词内容和时间同步",
            Ui.Row("显示翻译", "有翻译时在原文下方显示",
                Switch("显示歌词翻译", settings.ShowTranslation,
                    value => Commit(settings with { ShowTranslation = value }))),
            Ui.Row("英文整词特效", "开启后英文歌词按整个单词产生跳字与发光特效，而非逐个字母",
                Switch("英文整词特效", settings.EnglishWordEffects,
                    value => Commit(settings with { EnglishWordEffects = value }))),
            Ui.Row("首选歌词源", "自动模式优先使用逐字歌词；首选源没有结果时继续尝试其他来源",
                Pick("首选歌词源",
                    [
                        (LyricsSourcePreference.Automatic, "自动"),
                        (LyricsSourcePreference.Netease, "网易云"),
                        (LyricsSourcePreference.Kugou, "酷狗"),
                        (LyricsSourcePreference.Lrclib, "LRCLIB")
                    ],
                    settings.LyricsSource,
                    value => Commit(settings with { LyricsSource = value }))),
            Ui.Row("时间偏移", "正值让歌词提前，负值让歌词延后",
                Ui.Slider("歌词时间偏移毫秒",
                    SettingsStore.MinimumLyricsOffsetMs,
                    SettingsStore.MaximumLyricsOffsetMs,
                    settings.LyricsOffsetMs,
                    50,
                    OffsetLabel,
                    value => Commit(settings with
                    {
                        LyricsOffsetMs = SettingsStore.NormalizeLyricsOffsetMs((int)value)
                    }))),
            Ui.Row("每首歌曲偏移", "开启后可在灵动岛右键菜单中调整当前歌曲", enableTrackOffsets),
            trackOffsetManager,
            Ui.Row("重新获取当前歌曲", "忽略本次缓存结果，重新请求歌词和歌曲信息", refreshAction));

        // ── 外观 ────────────────────────────────────────────────────────────────
        var appearanceSection = Ui.Section(
            "外观",
            "调整岛屿的尺寸和背景",
            Ui.Row("整体缩放", "同步缩放字体、图标、胶囊、间距和光效",
                Ui.Slider("岛屿整体缩放百分比",
                    SettingsStore.MinimumScalePercent,
                    SettingsStore.MaximumScalePercent,
                    settings.ScalePercent,
                    10,
                    value => $"{value:0}%",
                    value => Commit(settings with
                    {
                        ScalePercent = SettingsStore.NormalizeScalePercent(value)
                    }))),
            Ui.Row("最大宽度", "按可用显示器的宽度计算",
                Ui.Slider("岛屿最大宽度百分比",
                    SettingsStore.MinimumWidthPercent,
                    SettingsStore.MaximumWidthPercent,
                    settings.WidthPercent,
                    5,
                    value => $"{value:0}%",
                    value => Commit(settings with
                    {
                        WidthPercent = SettingsStore.NormalizeWidthPercent(value)
                    }))),
            Ui.Row("背景不透明度", "降低后更容易融入桌面，但文字仍保持清晰",
                Ui.Slider("岛屿背景不透明度百分比",
                    SettingsStore.MinimumBackgroundOpacityPercent,
                    SettingsStore.MaximumBackgroundOpacityPercent,
                    settings.BackgroundOpacityPercent,
                    5,
                    value => $"{value:0}%",
                    value => Commit(settings with
                    {
                        BackgroundOpacityPercent = SettingsStore.NormalizeBackgroundOpacityPercent(value)
                    }))),
            Ui.Row("背景模糊", "在支持背景模糊协议的 Wayland 桌面上模糊岛屿后方",
                Switch("岛屿背景模糊", settings.BackgroundBlur,
                    value => Commit(settings with { BackgroundBlur = value }))));

        // ── 交互 ────────────────────────────────────────────────────────────────
        var iconActionChoices = new[]
        {
            (IslandIconAction.None, "关闭"),
            (IslandIconAction.LyricsWindow, "歌词窗口"),
            (IslandIconAction.Favorite, "收藏歌曲"),
            (IslandIconAction.PlayPause, "播放/暂停"),
            (IslandIconAction.Previous, "上一首"),
            (IslandIconAction.Next, "下一首"),
            (IslandIconAction.Banner, "分享图"),
            (IslandIconAction.TemporaryHide, "临时隐藏")
        };

        Border SideIconActionRow(
            string title,
            string description,
            string automationName,
            IslandIconAction initial,
            IslandIconAction fallback,
            Action<IslandIconAction> apply)
        {
            var lastActive = initial != IslandIconAction.None ? initial : fallback;
            var syncing = false;
            var toggle = Ui.Toggle($"启用{automationName}", initial != IslandIconAction.None);
            var segmented = new Segmented<IslandIconAction>(automationName, fill: true);
            segmented.Set(iconActionChoices, candidate => candidate == initial);

            void Select(IslandIconAction next)
            {
                if (syncing)
                    return;
                if (next != IslandIconAction.None)
                    lastActive = next;
                syncing = true;
                toggle.IsChecked = next != IslandIconAction.None;
                segmented.Set(iconActionChoices, candidate => candidate == next);
                syncing = false;
                apply(next);
            }

            toggle.IsCheckedChanged += (_, _) =>
                Select(toggle.IsChecked == true ? lastActive : IslandIconAction.None);
            segmented.Changed += Select;

            var labels = new StackPanel
            {
                Spacing = 3,
                VerticalAlignment = VerticalAlignment.Center,
                Children = { Ui.Text(title, 14, FontWeight.SemiBold), Ui.Text(description, 12, muted: true) }
            };
            var header = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,24,Auto"),
                Children = { labels, toggle }
            };
            Grid.SetColumn(toggle, 2);
            return Ui.Block(new StackPanel
            {
                Spacing = 12,
                Children = { header, segmented }
            });
        }

        var positionSection = Ui.Section(
            "交互",
            "控制拖动、鼠标穿透和两侧图标悬浮快捷操作",
            Ui.Row("锁定位置", "禁止鼠标拖动，双击隐藏仍可使用",
                Switch("锁定岛屿位置", settings.LockPosition,
                    value => Commit(settings with { LockPosition = value }))),
            Ui.Row("鼠标完全穿透", "开启后岛屿不接收鼠标操作，可从设置窗口关闭",
                Switch("鼠标完全穿透", settings.ClickThrough,
                    value => Commit(settings with { ClickThrough = value }))),
            SideIconActionRow(
                "左侧图标悬浮操作",
                "鼠标悬停在灵动岛左侧图标时切换为快捷按钮",
                "左侧图标悬浮操作",
                settings.LeftIconAction,
                IslandIconAction.LyricsWindow,
                value => Commit(settings with { LeftIconAction = value })),
            SideIconActionRow(
                "右侧图标悬浮操作",
                "鼠标悬停在灵动岛右侧封面时切换为快捷按钮",
                "右侧图标悬浮操作",
                settings.RightIconAction,
                IslandIconAction.Favorite,
                value => Commit(settings with { RightIconAction = value })));

        // ── 行为 ────────────────────────────────────────────────────────────────
        var behaviorSection = Ui.Section(
            "行为",
            "控制暂停、临时隐藏和系统启动行为",
            Ui.Row("暂停时", "岛屿单击暂停时保留并收窄；其他暂停按此设置，不隐藏错误提示",
                Pick("暂停时显示方式",
                    [
                        (PausedDisplayMode.HideImmediately, "立即隐藏"),
                        (PausedDisplayMode.HideAfterThreeSeconds, "3 秒后隐藏"),
                        (PausedDisplayMode.KeepVisible, "保持显示")
                    ],
                    settings.PausedDisplay,
                    value => Commit(settings with { PausedDisplay = value }))),
            Ui.Row("临时隐藏时长", "双击岛屿或使用右键菜单时生效",
                Pick("临时隐藏时长",
                    [(2, "2 秒"), (5, "5 秒"), (10, "10 秒"), (30, "30 秒")],
                    settings.TemporaryHideSeconds,
                    value => Commit(settings with { TemporaryHideSeconds = value }))),
            Ui.Row("登录后自动启动", "使用桌面环境的自启动目录",
                Switch("登录后自动启动", settings.StartOnLogin,
                    value => Commit(settings with { StartOnLogin = value }))));

        // ── 渲染 ────────────────────────────────────────────────────────────────
        var renderingSection = Ui.Section(
            "渲染",
            "同时应用于歌词窗口和灵动岛，修改后立即生效",
            Ui.Row("最大刷新率", "限制歌词窗口和岛屿的动画刷新，滑到最右侧为无限制",
                Ui.Slider("最大刷新率",
                    SettingsStore.MinimumFrameRate,
                    SettingsStore.UnlimitedFrameRateSliderValue,
                    settings.MaximumFrameRate == 0
                        ? SettingsStore.UnlimitedFrameRateSliderValue
                        : settings.MaximumFrameRate,
                    10,
                    value => value >= SettingsStore.UnlimitedFrameRateSliderValue ? "无限制" : $"{value:0} FPS",
                    value => Commit(settings with
                    {
                        MaximumFrameRate = value >= SettingsStore.UnlimitedFrameRateSliderValue ? 0 : (int)value
                    }))),
            Ui.Row("垂直同步", "EGL 支持时调整交换间隔；实际呈现由桌面合成器控制",
                Pick("垂直同步方式",
                    [
                        (VerticalSyncMode.Disabled, "关闭"),
                        (VerticalSyncMode.Enabled, "垂直同步"),
                        (VerticalSyncMode.Adaptive, "自适应")
                    ],
                    settings.VerticalSync,
                    value => Commit(settings with { VerticalSync = value }))));

        // ── 缓存 ────────────────────────────────────────────────────────────────
        var cacheStatus = Ui.Text(string.Empty, 12, muted: true);
        cacheStatus.VerticalAlignment = VerticalAlignment.Center;
        var clearCache = Ui.Button(string.Empty);
        void RefreshCacheSize() => clearCache.Content = $"清理缓存（{TrackCache.FormatSize(TrackCache.Size())}）";
        RefreshCacheSize();
        clearCache.Click += (_, _) =>
        {
            cacheStatus.Text = TrackCache.Clear() ? "缓存已清理" : "清理失败，请查看终端错误";
            RefreshCacheSize();
        };
        var cacheSection = Ui.Section(
            "缓存",
            "缓存歌词、封面和歌曲信息",
            Ui.Row("本地缓存", "清理后会在下次播放时重新获取",
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 12,
                    Children = { cacheStatus, clearCache }
                }));

        _onShown = () =>
        {
            var latest = currentSettings();
            settings = settings with
            {
                TrackOffsetsMs = latest.TrackOffsetsMs,
                TrackOffsetTitles = latest.TrackOffsetTitles
            };
            RefreshTrackOffsetManager();
        };

        // ── 布局：左侧导航 + 右侧内容 ───────────────────────────────────────────
        var sections = new (string Icon, string Title, Control Section)[]
        {
            (MusicIcon, "播放信息源", playbackSection),
            (LyricsIcon, "歌词", lyricsSection),
            (ScaleIcon, "外观", appearanceSection),
            (PositionIcon, "交互", positionSection),
            (BehaviorIcon, "行为", behaviorSection),
            (RenderIcon, "渲染", renderingSection),
            (CacheIcon, "缓存", cacheSection)
        };
        var contentPanel = new StackPanel
        {
            Margin = new Thickness(8, 4, 28, 32),
            Spacing = 30,
            MaxWidth = 760,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        foreach (var section in sections)
            contentPanel.Children.Add(section.Section);
        var scroller = new ScrollViewer
        {
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = contentPanel
        };

        var scrollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        var scrollClock = new Stopwatch();
        var scrollStart = 0d;
        var scrollTarget = 0d;
        scrollTimer.Tick += (_, _) =>
        {
            var progress = Math.Min(1, scrollClock.Elapsed.TotalMilliseconds / 320);
            var eased = 1 - Math.Pow(1 - progress, 3);
            var maximum = Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height);
            scroller.Offset = new Vector(0, Math.Clamp(scrollStart + (scrollTarget - scrollStart) * eased, 0, maximum));
            if (progress >= 1)
                scrollTimer.Stop();
        };
        scroller.AddHandler(Avalonia.Input.InputElement.PointerWheelChangedEvent,
            (_, _) => scrollTimer.Stop(), Avalonia.Interactivity.RoutingStrategies.Tunnel);
        scroller.AddHandler(Avalonia.Input.InputElement.PointerPressedEvent,
            (_, _) => scrollTimer.Stop(), Avalonia.Interactivity.RoutingStrategies.Tunnel);
        scroller.AddHandler(Avalonia.Input.InputElement.KeyDownEvent,
            (_, _) => scrollTimer.Stop(), Avalonia.Interactivity.RoutingStrategies.Tunnel);
        DetachedFromVisualTree += (_, _) => scrollTimer.Stop();
        PropertyChanged += (_, args) =>
        {
            if (args.Property == IsVisibleProperty && !IsVisible)
                scrollTimer.Stop();
        };

        var navButtons = new List<Button>();
        var navList = new StackPanel { Spacing = 4, Margin = new Thickness(20, 4, 8, 16) };
        foreach (var section in sections)
        {
            var nav = new Button
            {
                Height = 42,
                Padding = new Thickness(14, 0),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(LyricsTheme.ControlRadius),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                VerticalContentAlignment = VerticalAlignment.Center,
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 12,
                    Children =
                    {
                        new PathIcon
                        {
                            Data = Geometry.Parse(section.Icon),
                            Width = 17,
                            Height = 17,
                            VerticalAlignment = VerticalAlignment.Center
                        },
                        new TextBlock
                        {
                            Text = section.Title,
                            FontSize = 14,
                            FontWeight = FontWeight.Medium,
                            VerticalAlignment = VerticalAlignment.Center
                        }
                    }
                },
                Classes = { "nav" }
            };
            AutomationProperties.SetName(nav, $"跳转到{section.Title}");
            var target = section.Section;
            nav.Click += (_, _) =>
            {
                var y = target.TranslatePoint(new Point(0, 0), contentPanel)?.Y ?? 0;
                scrollStart = scroller.Offset.Y;
                scrollTarget = Math.Clamp(y - 4, 0, Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height));
                scrollClock.Restart();
                scrollTimer.Start();
            };
            navButtons.Add(nav);
            navList.Children.Add(nav);
        }

        void UpdateActiveSection()
        {
            var active = 0;
            var atEnd = scroller.Extent.Height > scroller.Viewport.Height
                && scroller.Offset.Y >= scroller.Extent.Height - scroller.Viewport.Height - 1;
            if (atEnd)
                active = sections.Length - 1;
            else
            {
                for (var i = 0; i < sections.Length; i++)
                {
                    var y = sections[i].Section.TranslatePoint(new Point(0, 0), contentPanel)?.Y ?? 0;
                    if (y <= scroller.Offset.Y + 60)
                        active = i;
                }
            }

            for (var i = 0; i < navButtons.Count; i++)
            {
                navButtons[i].Classes.Set("navactive", i == active);
                navButtons[i].Classes.Set("nav", i != active);
            }
        }

        scroller.ScrollChanged += (_, _) => UpdateActiveSection();
        scroller.LayoutUpdated += (_, _) => UpdateActiveSection();

        var sidebar = new ScrollViewer
        {
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = navList
        };
        var layout = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("220,*"),
            Children = { sidebar, scroller }
        };
        Grid.SetColumn(scroller, 1);
        // Narrow windows drop the sidebar; the sections simply stack.
        layout.SizeChanged += (_, args) =>
        {
            var wide = args.NewSize.Width >= 800;
            sidebar.IsVisible = wide;
            layout.ColumnDefinitions[0].Width = wide ? new GridLength(220) : new GridLength(0);
        };
        _entrance = sections.Select(section => section.Section).ToList();
        Content = layout;
    }

    private static string OffsetLabel(double value) => value switch
    {
        > 0 => $"提前 {value:0} ms",
        < 0 => $"延后 {-value:0} ms",
        _ => "0 ms"
    };
}
