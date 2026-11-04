using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace LyricifyIsland;

internal sealed class HeadphonePage : PanelPage
{
    private const string HeadphoneIcon = "M12 3A9 9 0 0 0 3 12V19A2 2 0 0 0 5 21H8V13H5V12A7 7 0 0 1 19 12V13H16V21H19A2 2 0 0 0 21 19V12A9 9 0 0 0 12 3Z";
    private const string SoundIcon = "M3 9V15H7L12 20V4L7 9H3M16.5 12A4.5 4.5 0 0 0 14 7.97V16.02A4.5 4.5 0 0 0 16.5 12M14 3.23V5.29A7 7 0 0 1 14 18.71V20.77A9 9 0 0 0 14 3.23Z";

    private readonly HeadphoneService _service;
    private readonly ComboBox _devices = Ui.Combo("选择已配对的耳机", 0);
    private readonly Button _refresh = Ui.Button("刷新");
    private readonly Button _connect = Ui.Button("连接耳机", accent: true);
    private readonly Button _refreshState = Ui.Button("更新状态");
    private readonly TextBlock _deviceName = Ui.Text("选择你的耳机", 21, FontWeight.SemiBold);
    private readonly TextBlock _deviceDetail = Ui.Text("先在系统蓝牙设置中完成配对", 13, muted: true);
    private readonly TextBlock _connectionLabel = Ui.Text("未连接", 12, muted: true);
    private readonly Border _connectionDot = new()
    {
        Width = 7, Height = 7, CornerRadius = new CornerRadius(3.5),
        Background = LyricsTheme.MutedText, VerticalAlignment = VerticalAlignment.Center
    };
    private readonly TextBlock _status = Ui.Text("正在读取已配对的设备…", 12, muted: true);
    private readonly TextBlock _deviceMetadata = Ui.Text("", 11, muted: true);
    private readonly BatteryView _leftBattery = new("左耳");
    private readonly BatteryView _rightBattery = new("右耳");
    private readonly BatteryView _caseBattery = new("充电盒");
    private readonly Grid _batteries;

    private readonly Segmented<HeadphoneNoiseMode> _noiseModes = new("聆听模式", fill: true);
    private readonly Segmented<HeadphoneNoiseLevel> _noiseLevel = new("降噪强度");
    private readonly TextBlock _noiseLevelDescription = Ui.Text("选择适合周围环境的降噪程度", 12, muted: true);
    private readonly Border _noiseNotice;
    private readonly Segmented<int> _spatial = new("空间音频");
    private readonly TextBlock _spatialTitle = Ui.Text("空间音频", 14, FontWeight.SemiBold);
    private readonly TextBlock _spatialDescription = Ui.Text("调整声音的空间呈现", 12, muted: true);
    private readonly ComboBox _equalizer = Ui.Combo("音效预设");
    private readonly ToggleSwitch _game = Ui.Toggle("游戏模式", false, "开", "关");
    private readonly TextBlock _gameTitle = Ui.Text("游戏模式", 14, FontWeight.SemiBold);
    private readonly TextBlock _gameDescription = Ui.Text("降低声音延迟", 12, muted: true);
    private readonly ToggleSwitch _gameSound = Ui.Toggle("游戏音效", false, "开", "关");
    private readonly TextBlock _gameSoundDescription = Ui.Text("增强游戏中的声音表现", 12, muted: true);
    private readonly ToggleSwitch _dualDevice = Ui.Toggle("双设备连接", false, "开", "关");
    private readonly ToggleSwitch _wearDetection = Ui.Toggle("佩戴检测", false, "开", "关");
    private readonly Segmented<HeadphoneConnectionMethod> _connectionMethod = new("耳机连接方式");
    private readonly ComboBox _huaweiModel = Ui.Combo("自动识别设备型号");
    private readonly ToggleSwitch _autoConnect = Ui.Toggle("蓝牙耳机连接后自动接入");

    private readonly Control _noiseCard;
    private readonly Control _soundCard;
    private readonly Control _deviceCard;
    private readonly Control _connectedDevicesCard;
    private readonly Border _emptyCard;
    private readonly StackPanel _connectedDevices = new();
    private readonly Border _automaticDevicePriorityBlock;
    private readonly Button _automaticDevicePriority = Ui.Button("恢复自动选择");
    private readonly Border _noiseLevelRow;
    private readonly Border _spatialRow;
    private readonly Border _equalizerRow;
    private readonly Border _gameRow;
    private readonly Border _gameSoundRow;
    private readonly Border _dualDeviceRow;
    private readonly Border _wearDetectionRow;
    private readonly Border _huaweiModelRow;
    private readonly TextBlock _emptyTitle = Ui.Text("连接后，即可调整聆听体验", 16, FontWeight.SemiBold);
    private readonly TextBlock _emptyDescription = Ui.Text("选择已配对的耳机，查看电量并调整聆听设置。", 13, muted: true);
    private bool _syncing;
    private bool _closed;
    private string? _operationError;
    private IReadOnlyList<HeadphoneConnectedDevice>? _shownConnectedDevices;
    private bool _shownCanSetDevicePriority;

    private IReadOnlyList<Control> _entrance = [];
    private bool _loaded;

    public override IReadOnlyList<Control> EntranceTargets => _entrance;

    public override void OnShown() => _ = RunAsync(_service.RefreshDevicesAsync);

    public HeadphonePage(HeadphoneService service)
    {
        _service = service;

        // ── 设备卡片 ────────────────────────────────────────────────────────────
        _devices.HorizontalAlignment = HorizontalAlignment.Stretch;
        _devices.VerticalAlignment = VerticalAlignment.Center;
        _deviceDetail.TextWrapping = TextWrapping.Wrap;
        _status.TextWrapping = TextWrapping.Wrap;
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);

        var avatar = new Border
        {
            Width = 60, Height = 60,
            CornerRadius = new CornerRadius(30),
            Background = LyricsTheme.InsetBackground,
            BorderBrush = LyricsTheme.CardBorder,
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = new PathIcon
            {
                Data = Geometry.Parse(HeadphoneIcon), Width = 28, Height = 28,
                Foreground = LyricsTheme.Accent,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        var connectionBadge = new Border
        {
            Background = LyricsTheme.InsetBackground,
            BorderBrush = LyricsTheme.CardBorder,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 6),
            CornerRadius = new CornerRadius(LyricsTheme.PillRadius),
            VerticalAlignment = VerticalAlignment.Top,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children = { _connectionDot, _connectionLabel }
            }
        };
        connectionBadge.HorizontalAlignment = HorizontalAlignment.Left;
        connectionBadge.Background = Brushes.Transparent;
        connectionBadge.BorderThickness = new Thickness(0);
        connectionBadge.Padding = new Thickness(0);
        var deviceHeader = new StackPanel
        {
            Spacing = 8,
            Children = { avatar, _deviceName, connectionBadge, _deviceDetail }
        };
        _deviceName.TextWrapping = TextWrapping.Wrap;
        _deviceName.TextTrimming = TextTrimming.None;
        var deviceActions = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,10,*"),
            Children = { _refresh, _connect }
        };
        Grid.SetColumn(_connect, 2);
        _refresh.HorizontalAlignment = HorizontalAlignment.Stretch;
        _connect.HorizontalAlignment = HorizontalAlignment.Stretch;
        _batteries = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,12,*,12,*"),
            Children = { _leftBattery.Root, _rightBattery.Root, _caseBattery.Root }
        };
        Grid.SetColumn(_rightBattery.Root, 2);
        Grid.SetColumn(_caseBattery.Root, 4);
        _refreshState.HorizontalAlignment = HorizontalAlignment.Left;
        var hero = new StackPanel
        {
            Spacing = 18,
            Children = { deviceHeader, _batteries, _devices, deviceActions, _status, _refreshState }
        };

        // ── 聆听模式 ────────────────────────────────────────────────────────────
        _noiseLevelRow = ConsoleRow(Ui.Text("降噪强度", 14, FontWeight.SemiBold), _noiseLevelDescription, _noiseLevel);
        _noiseNotice = Ui.Block(Ui.Text("此型号可发送降噪指令，但无法读取当前模式。", 12, muted: true));
        _noiseCard = ConsoleSection("聆听模式", "让声音适应此刻的环境",
            Ui.Block(_noiseModes), _noiseLevelRow, _noiseNotice);

        // ── 声音 ────────────────────────────────────────────────────────────────
        _spatialRow = ConsoleRow(_spatialTitle, _spatialDescription, _spatial);
        _equalizerRow = ConsoleRow(Ui.Text("音效预设", 14, FontWeight.SemiBold), Ui.Text("使用耳机内置的调音风格", 12, muted: true), _equalizer);
        _gameRow = ConsoleRow(_gameTitle, _gameDescription, _game);
        _gameSoundRow = ConsoleRow(Ui.Text("游戏音效", 14, FontWeight.SemiBold), _gameSoundDescription, _gameSound);
        _soundCard = ConsoleSection("声音", "调整耳机支持的音效",
            _spatialRow, _equalizerRow, _gameRow, _gameSoundRow);

        // ── 耳机偏好 ────────────────────────────────────────────────────────────
        _dualDeviceRow = ConsoleRow("双设备连接", "允许耳机同时连接两台设备", _dualDevice);
        _wearDetectionRow = ConsoleRow("佩戴检测", "随佩戴状态控制播放", _wearDetection);
        _deviceCard = ConsoleSection("耳机偏好", "这些设置会保存在耳机中", _dualDeviceRow, _wearDetectionRow);

        // ── 多设备连接 ──────────────────────────────────────────────────────────
        _automaticDevicePriority.HorizontalAlignment = HorizontalAlignment.Right;
        ToolTip.SetTip(_automaticDevicePriority, "由耳机自动选择优先连接的设备");
        _automaticDevicePriorityBlock = Ui.Block(_automaticDevicePriority);
        _connectedDevicesCard = ConsoleSection("多设备连接", "管理耳机记住的设备与连接优先级",
            _connectedDevices, _automaticDevicePriorityBlock);

        _emptyDescription.TextWrapping = TextWrapping.Wrap;
        _emptyCard = new Border
        {
            Padding = new Thickness(28, 64),
            Child = new StackPanel
            {
                Spacing = 8,
                Children = { _emptyTitle, _emptyDescription }
            }
        };

        // ── 连接偏好 ────────────────────────────────────────────────────────────
        _huaweiModel.ItemsSource = new[] { new Choice<string>("", "自动识别") }
            .Concat(HuaweiHeadphoneCatalog.Profiles.Select(profile => new Choice<string>(profile.ProductId, profile.Name))).ToArray();
        _huaweiModelRow = SettingBlock(Ui.Text("设备型号", 14, FontWeight.SemiBold),
            Ui.Text("改名后无法识别时选择实际型号；断开控制连接后可更改", 12, muted: true), _huaweiModel);
        ToolTip.SetTip(_connectionMethod, "自动选择服务连接；标准连接使用服务 UUID，兼容连接使用通道 15。更改后下次连接生效。");
        var connectionOptions = new StackPanel
        {
            IsVisible = false,
            Spacing = 12,
            Children =
            {
                _huaweiModelRow,
                SettingBlock(Ui.Text("连接方式", 14, FontWeight.SemiBold),
                    Ui.Text("更改后下次连接生效", 12, muted: true), _connectionMethod),
                SettingBlock(Ui.Text("自动接入", 14, FontWeight.SemiBold),
                    Ui.Text("蓝牙连接后自动接入；手动断开后，本次不再自动接入", 12, muted: true), _autoConnect)
            }
        };
        var connectionToggle = Ui.Button("连接偏好 ▾");
        connectionToggle.HorizontalAlignment = HorizontalAlignment.Stretch;
        connectionToggle.HorizontalContentAlignment = HorizontalAlignment.Left;
        connectionToggle.Click += (_, _) =>
        {
            connectionOptions.IsVisible = !connectionOptions.IsVisible;
            connectionToggle.Content = connectionOptions.IsVisible ? "连接偏好 ▴" : "连接偏好 ▾";
        };
        var connectionCard = new StackPanel
        {
            Spacing = 12,
            Children = { connectionToggle, connectionOptions }
        };
        _autoConnect.HorizontalAlignment = HorizontalAlignment.Left;
        _connectionMethod.HorizontalAlignment = HorizontalAlignment.Stretch;
        _huaweiModel.MinWidth = 0;

        // Keep device information in a compact sidebar, with the listening controls on the right.
        var sidebar = new StackPanel
        {
            Spacing = 24,
            Margin = new Thickness(0, 0, 16, 0),
            Children = { hero, connectionCard, _deviceMetadata }
        };
        var listening = new StackPanel
        {
            Spacing = 28,
            Margin = new Thickness(0, 0, 28, 0),
            Children = { _emptyCard, _noiseCard, _soundCard, _deviceCard, _connectedDevicesCard }
        };
        var deviceScroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = sidebar
        };
        var controlsScroller = new ScrollViewer
        {
            Margin = new Thickness(0, 0, -12, 0),
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = listening
        };
        var columns = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*"),
            RowDefinitions = new RowDefinitions("2*,24,3*"),
            Margin = new Thickness(28, 12, 28, 24),
            MaxWidth = 1120,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Children = { deviceScroller, controlsScroller }
        };
        Grid.SetRow(controlsScroller, 2);
        var wideLayout = false;
        void ApplyLayout(double width)
        {
            var wide = width >= 820;
            if (wide == wideLayout) return;
            wideLayout = wide;
            columns.ColumnDefinitions = new ColumnDefinitions(wide ? "300,40,*" : "*");
            columns.RowDefinitions = new RowDefinitions(wide ? "*" : "2*,24,3*");
            Grid.SetColumn(controlsScroller, wide ? 2 : 0);
            Grid.SetRow(controlsScroller, wide ? 0 : 2);
        }
        columns.SizeChanged += (_, args) => ApplyLayout(args.NewSize.Width);
        _entrance = [hero, _emptyCard, _noiseCard, _soundCard, _deviceCard, _connectedDevicesCard, connectionCard];
        Content = columns;

        // ── 事件 ────────────────────────────────────────────────────────────────
        _refresh.Click += async (_, _) => await RunAsync(_service.RefreshDevicesAsync);
        _refreshState.Click += async (_, _) => await RunAsync(_service.RefreshStateAsync);
        _automaticDevicePriority.Click += async (_, _) => await RunAsync(_service.SetAutomaticDevicePriorityAsync);
        _connect.Click += async (_, _) => await RunAsync(
            _service.State.IsConnected ? _service.DisconnectAsync : _service.ConnectAsync);
        _devices.SelectionChanged += async (_, _) =>
        {
            if (!_syncing && _devices.SelectedItem is HeadphoneDevice device)
                await RunAsync(() => _service.SelectDeviceAsync(device.Address));
        };
        _noiseModes.Changed += async mode => await RunAsync(() => _service.SetNoiseModeAsync(mode));
        _noiseLevel.Changed += async level => await RunAsync(() => _service.SetNoiseLevelAsync(level));
        _spatial.Changed += async mode => await RunAsync(() => _service.SetSpatialModeAsync(mode));
        BindChoice<int>(_equalizer, _service.SetEqualizerAsync);
        BindToggle(_game, _service.SetGameModeAsync);
        BindToggle(_gameSound, _service.SetGameSoundAsync);
        BindToggle(_dualDevice, _service.SetDualDeviceAsync);
        BindToggle(_wearDetection, _service.SetWearDetectionAsync);
        _connectionMethod.Changed += method => SavePreference(() => _service.SetConnectionMethod(method));
        _huaweiModel.SelectionChanged += async (_, _) =>
        {
            if (!_syncing && _huaweiModel.SelectedItem is Choice<string> choice)
                await RunAsync(() => _service.SetHuaweiModelAsync(choice.Value));
        };
        _autoConnect.IsCheckedChanged += (_, _) =>
        {
            if (!_syncing)
                SavePreference(() => _service.SetAutoConnect(_autoConnect.IsChecked == true));
        };
        AttachedToVisualTree += (_, _) =>
        {
            _closed = false;
            _service.StateChanged += OnStateChanged;
            UpdateState();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _closed = true;
            _service.StateChanged -= OnStateChanged;
        };
        UpdateState();
        _loaded = true;
    }

    private static Border ConsoleRow(string title, string description, Control action) =>
        ConsoleRow(Ui.Text(title, 14, FontWeight.SemiBold), Ui.Text(description, 12, muted: true), action);

    private static Border ConsoleRow(Control title, Control description, Control action)
    {
        var row = Ui.Row(title, description, action);
        row.BorderThickness = new Thickness(0);
        return row;
    }

    private static StackPanel ConsoleSection(string title, string description, params Control[] rows)
    {
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new StackPanel
        {
            Spacing = 4,
            Children = { Ui.Text(title, 19, FontWeight.SemiBold), Ui.Text(description, 12, muted: true) }
        });
        foreach (var row in rows)
        {
            if (row is Border border) border.BorderThickness = new Thickness(0);
            content.Children.Add(row);
        }
        return content;
    }

    private static Border SettingBlock(Control title, Control description, Control action)
    {
        action.HorizontalAlignment = HorizontalAlignment.Stretch;
        var row = Ui.Block(new StackPanel
        {
            Spacing = 10,
            Children =
            {
                new StackPanel { Spacing = 3, Children = { title, description } },
                action
            }
        });
        row.BorderThickness = new Thickness(0);
        return row;
    }

    /// <summary>Cards that become available slide in instead of popping.</summary>
    private void SetVisible(Control control, bool visible)
    {
        var was = control.IsVisible;
        control.IsVisible = visible;
        if (visible && !was && _loaded)
            Motion.In(control, 0, 16, TimeSpan.FromMilliseconds(320));
    }

    private void OnStateChanged(object? sender, EventArgs args) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (!_closed)
                UpdateState();
        });

    private void UpdateState()
    {
        _syncing = true;
        try
        {
            var state = _service.State;
            var capabilities = state.Capabilities;
            var connected = state.IsConnected;
            var available = connected && !_service.IsBusy;
            if (_devices.ItemsSource is not IReadOnlyList<HeadphoneDevice> devices
                || !devices.SequenceEqual(_service.Devices))
                _devices.ItemsSource = _service.Devices;
            _devices.SelectedItem = _service.Devices.FirstOrDefault(device => device.Address == _service.SelectedDevice?.Address);
            _devices.IsEnabled = !_service.IsBusy && !connected && _service.Devices.Count > 0;
            _devices.PlaceholderText = _service.Devices.Count == 0 ? "未找到已配对的耳机" : "选择耳机";
            _refresh.IsEnabled = !_service.IsBusy;
            _connect.IsEnabled = !_service.IsBusy && _service.SelectedDevice is not null;
            _connect.Content = connected ? "断开连接" : "连接耳机";
            _connect.Background = connected ? LyricsTheme.InputBackground : LyricsTheme.Accent;
            _connect.Foreground = connected ? LyricsTheme.PrimaryText : LyricsTheme.OnAccent;
            _connect.BorderThickness = new Thickness(connected ? 1 : 0);
            _connect.Classes.Set("accent", !connected);
            _connect.Classes.Set("glass", connected);
            _refreshState.IsVisible = connected;
            _refreshState.IsEnabled = available;
            _deviceName.Text = _service.SelectedDevice?.Name ?? "选择你的耳机";
            _deviceDetail.Text = connected
                ? string.IsNullOrWhiteSpace(state.ModelName) ? "已连接耳机" : state.ModelName
                : _service.Devices.Count == 0 ? "先在系统蓝牙设置中完成配对，然后刷新设备列表" : "选择耳机后连接，即可读取设备状态";
            _connectionLabel.Text = _service.IsBusy ? "处理中…" : connected ? "已连接" : "未连接";
            _connectionLabel.Foreground = connected ? LyricsTheme.Accent : LyricsTheme.MutedText;
            _connectionDot.Background = connected ? LyricsTheme.Accent : LyricsTheme.MutedText;
            _status.Text = _operationError ?? _service.StatusMessage;
            _status.Foreground = _operationError is null ? LyricsTheme.MutedText : LyricsTheme.ErrorText;
            _leftBattery.SetName(capabilities.IsEyewear ? "左镜腿" : "左耳");
            _rightBattery.SetName(capabilities.IsEyewear ? "右镜腿" : "右耳");
            _leftBattery.Update(connected ? state.LeftBattery : null, connected ? state.LeftWearing : null, connected);
            _rightBattery.Update(connected ? state.RightBattery : null, connected ? state.RightWearing : null, connected);
            _caseBattery.Update(connected ? state.CaseBattery : null, null, connected);
            _batteries.IsVisible = capabilities.HasBattery;
            _caseBattery.Root.IsVisible = capabilities.HasChargingCase;
            _batteries.ColumnDefinitions = new ColumnDefinitions(capabilities.HasChargingCase ? "*,12,*,12,*" : "*,12,*");
            if (connected && !capabilities.HasBattery)
                _deviceDetail.Text += " · 此型号暂不支持电量读取";
            _deviceMetadata.Text = string.Join("   ·   ", new[]
            {
                string.IsNullOrWhiteSpace(state.FirmwareVersion) ? null : $"固件 {state.FirmwareVersion}",
                string.IsNullOrWhiteSpace(state.CodecName) ? null : $"音频编码 {state.CodecName}"
            }.Where(value => value is not null));
            _deviceMetadata.IsVisible = connected && !string.IsNullOrEmpty(_deviceMetadata.Text);

            SetVisible(_noiseCard, connected && capabilities.NoiseModes.Count > 0);
            _noiseCard.IsEnabled = available;
            _noiseNotice.IsVisible = !capabilities.CanReadNoiseMode;
            _noiseModes.Set(
                Enum.GetValues<HeadphoneNoiseMode>()
                    .Where(mode => capabilities.NoiseModes.Contains(mode))
                    .Select(mode => (mode, NoiseModeLabel(mode))).ToList(),
                mode => state.NoiseMode == mode);
            _noiseLevelRow.IsVisible = capabilities.NoiseLevels.Count > 0;
            _noiseLevelRow.IsEnabled = state.NoiseMode is HeadphoneNoiseMode.NoiseCancellation or HeadphoneNoiseMode.Adaptive;
            _noiseLevel.Set(
                capabilities.NoiseLevels.Select(value => (value, NoiseLevelLabel(value))).ToList(),
                value => state.NoiseLevel == value);
            _noiseLevelDescription.Text = state.NoiseLevel == HeadphoneNoiseLevel.Smart
                ? state.RealtimeNoiseLevel is { } level
                    ? $"当前为{NoiseLevelLabel(level)}降噪，耳机会随环境调整"
                    : "耳机会随环境自动调整降噪强度"
                : "选择适合周围环境的降噪程度";
            _spatialRow.IsVisible = capabilities.SpatialModes.Count > 0;
            _spatialTitle.Text = capabilities.HasSpatialSound ? "空间音效" : "空间音频";
            _spatialDescription.Text = capabilities.HasSpatialSound ? "拓宽声音的空间感" : "调整声音的空间呈现";
            AutomationProperties.SetName(_spatial, _spatialTitle.Text);
            _spatial.Set(
                capabilities.SpatialModes.Select(value => (value,
                    capabilities.HasSpatialSound ? (value == 0 ? "关" : "开") : SpatialLabel(value))).ToList(),
                value => state.SpatialMode == value);
            _equalizerRow.IsVisible = capabilities.Equalizers.Count > 0;
            SyncChoice(_equalizer, capabilities.Equalizers.Select(value => new Choice<int>(value.Id, value.Name)).ToArray(), state.EqualizerId);
            _gameRow.IsVisible = capabilities.HasGameMode;
            _gameTitle.Text = capabilities.CanReadGameMode ? "游戏模式" : "低时延";
            _gameDescription.Text = capabilities.CanReadGameMode ? "降低声音延迟"
                : "不支持状态回读；开关显示本次连接最近发送的设置";
            AutomationProperties.SetName(_game, _gameTitle.Text);
            _game.IsChecked = state.GameMode;
            _game.OnContent = state.GameMode is null ? capabilities.CanReadGameMode ? "读取中" : "未设置" : "开";
            _game.OffContent = state.GameMode is null ? capabilities.CanReadGameMode ? "读取中" : "未设置" : "关";
            _gameSoundRow.IsVisible = capabilities.HasGameSound;
            _gameSound.IsChecked = state.GameSound;
            _gameSound.OnContent = state.GameSound is null ? "读取中" : "开";
            _gameSound.OffContent = state.GameSound is null ? "读取中" : "关";
            _gameSoundDescription.Text = (capabilities.GameSoundMutexSpatial, capabilities.GameSoundMutexEqualizer) switch
            {
                (true, true) => $"开启后会关闭{_spatialTitle.Text}；选择音效预设会先关闭游戏音效",
                (true, false) => $"开启后会关闭{_spatialTitle.Text}",
                (false, true) => "选择音效预设会先关闭游戏音效",
                _ => "增强游戏中的声音表现"
            };
            _dualDeviceRow.IsVisible = capabilities.HasDualDevice;
            _dualDevice.IsChecked = state.DualDevice;
            _dualDevice.OnContent = state.DualDevice is null ? "读取中" : "开";
            _dualDevice.OffContent = state.DualDevice is null ? "读取中" : "关";
            _wearDetectionRow.IsVisible = capabilities.HasWearDetection;
            _wearDetection.IsChecked = state.WearDetection;
            _wearDetection.OnContent = state.WearDetection is null ? "读取中" : "开";
            _wearDetection.OffContent = state.WearDetection is null ? "读取中" : "关";
            SetVisible(_soundCard, connected && (_spatialRow.IsVisible || _equalizerRow.IsVisible || _gameRow.IsVisible || _gameSoundRow.IsVisible));
            _soundCard.IsEnabled = available;
            SetVisible(_deviceCard, connected && (_dualDeviceRow.IsVisible || _wearDetectionRow.IsVisible));
            _deviceCard.IsEnabled = available;
            SetVisible(_connectedDevicesCard, connected && capabilities.HasMultiDeviceSwitch);
            _connectedDevicesCard.IsEnabled = available;
            _automaticDevicePriorityBlock.IsVisible = capabilities.CanSetDevicePriority;
            _automaticDevicePriority.IsEnabled = available && capabilities.CanSetDevicePriority;
            UpdateConnectedDevices(state.ConnectedDevices, capabilities.CanSetDevicePriority);
            _emptyCard.IsVisible = !_noiseCard.IsVisible && !_soundCard.IsVisible && !_deviceCard.IsVisible && !_connectedDevicesCard.IsVisible;
            _emptyTitle.Text = connected ? "耳机已连接" : "连接后，即可调整聆听体验";
            _emptyDescription.Text = connected
                ? _service.UsesHuaweiProtocol ? "此型号目前可查看电量；若型号未识别，可断开控制连接后手动选择设备型号。"
                    : "正在读取耳机支持的功能。若长时间没有更新，请点击“更新状态”；不同型号可用的功能有所不同。"
                : "选择已配对的耳机，查看电量并调整聆听设置。";
            var method = _service.UsesHuaweiProtocol ? HeadphoneConnectionMethod.Uuid : _service.ConnectionMethod;
            _connectionMethod.Set(
                [
                    (HeadphoneConnectionMethod.Automatic, "自动"),
                    (HeadphoneConnectionMethod.Uuid, "标准连接"),
                    (HeadphoneConnectionMethod.Channel15, "兼容连接")
                ],
                value => value == method);
            _connectionMethod.IsEnabled = !_service.IsBusy && !_service.UsesHuaweiProtocol;
            _huaweiModelRow.IsVisible = _service.UsesHuaweiProtocol;
            _huaweiModel.IsEnabled = !_service.IsBusy && !connected && _service.SelectedDevice is not null;
            _huaweiModel.SelectedItem = ((IEnumerable<Choice<string>>)_huaweiModel.ItemsSource!)
                .FirstOrDefault(choice => choice.Value == _service.HuaweiModelOverride);
            _autoConnect.IsChecked = _service.AutoConnect;
        }
        finally
        {
            _syncing = false;
        }
    }

    private void UpdateConnectedDevices(IReadOnlyList<HeadphoneConnectedDevice> devices, bool canSetDevicePriority)
    {
        // Preserve keyboard focus when unrelated battery or feature updates arrive.
        if (_shownConnectedDevices?.SequenceEqual(devices) == true
            && _shownCanSetDevicePriority == canSetDevicePriority)
            return;
        _shownConnectedDevices = devices;
        _shownCanSetDevicePriority = canSetDevicePriority;
        _connectedDevices.Children.Clear();
        if (devices.Count == 0)
        {
            _connectedDevices.Children.Add(Ui.Block(Ui.Text(
                "耳机尚未返回设备列表。开启双设备连接后，可查看并管理已配对设备。", 12, muted: true)));
            return;
        }
        foreach (var device in devices.OrderByDescending(device => device.IsAudioActive)
                     .ThenByDescending(device => device.ConnectionState))
        {
            var status = device.ConnectionState switch
            {
                2 => device.IsAudioActive ? "当前音频设备" : "已连接",
                1 => "正在连接",
                _ => "未连接"
            };
            if (device.IsCurrentDevice)
                status += " · 当前电脑";
            var buttonText = device.ConnectionState switch
            {
                0 => "连接设备",
                1 => "正在连接",
                2 when device.IsAudioActive => "正在使用",
                2 when canSetDevicePriority => "设为优先设备",
                _ => "已连接"
            };
            var switchDevice = Ui.Button(buttonText);
            switchDevice.IsEnabled = device.ConnectionState == 0
                || (device.ConnectionState == 2 && !device.IsAudioActive && canSetDevicePriority);
            switchDevice.Click += async (_, _) => await RunAsync(() => _service.SwitchActiveDeviceAsync(device.Address));
            _connectedDevices.Children.Add(ConsoleRow(device.Name, status, switchDevice));
        }
    }

    private async Task RunAsync(Func<Task> action)
    {
        _operationError = null;
        try
        {
            await action();
        }
        catch (Exception exception)
        {
            _operationError = $"操作未完成：{exception.Message}";
            Console.Error.WriteLine($"[headphones] window operation failed: {exception.Message}");
        }
        if (!_closed)
            UpdateState();
    }

    private void SavePreference(Action action)
    {
        _operationError = null;
        try
        {
            action();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _operationError = $"设置保存失败：{exception.Message}";
        }
        UpdateState();
    }

    private void BindChoice<T>(ComboBox control, Func<T, Task> changed) where T : struct =>
        control.SelectionChanged += async (_, _) =>
        {
            if (!_syncing && control.SelectedItem is Choice<T> choice)
                await RunAsync(() => changed(choice.Value));
        };

    private void BindToggle(ToggleSwitch control, Func<bool, Task> changed) =>
        control.IsCheckedChanged += async (_, _) =>
        {
            if (!_syncing)
                await RunAsync(() => changed(control.IsChecked == true));
        };

    private static void SyncChoice<T>(ComboBox control, IReadOnlyList<Choice<T>> choices, T? value) where T : struct
    {
        if (control.ItemsSource is not IReadOnlyList<Choice<T>> current || !current.SequenceEqual(choices))
            control.ItemsSource = choices;
        control.SelectedItem = ((IEnumerable<Choice<T>>)control.ItemsSource)
            .FirstOrDefault(choice => EqualityComparer<T?>.Default.Equals(choice.Value, value));
        control.PlaceholderText = "等待耳机状态";
    }

    private static string NoiseModeLabel(HeadphoneNoiseMode mode) => mode switch
    {
        HeadphoneNoiseMode.Off => "关闭",
        HeadphoneNoiseMode.NoiseCancellation => "降噪",
        HeadphoneNoiseMode.Transparency => "通透",
        HeadphoneNoiseMode.Adaptive => "自适应",
        _ => mode.ToString()
    };

    private static string NoiseLevelLabel(HeadphoneNoiseLevel level) => level switch
    {
        HeadphoneNoiseLevel.Smart => "智能",
        HeadphoneNoiseLevel.Deep => "深度",
        HeadphoneNoiseLevel.Moderate => "中度",
        HeadphoneNoiseLevel.Light => "轻度",
        _ => level.ToString()
    };

    private static string SpatialLabel(int value) => value switch
    {
        0 => "关闭",
        1 => "固定",
        2 => "头部追踪",
        _ => value.ToString()
    };

    private sealed record Choice<T>(T Value, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed class BatteryView
    {
        private readonly TextBlock _name;
        private readonly TextBlock _value = Ui.Text("—", 20, FontWeight.SemiBold);
        private readonly TextBlock _charging = Ui.Text("等待连接", 11, muted: true);
        private readonly ProgressBar _level = new()
        {
            Minimum = 0, Maximum = 100, Value = 0, Height = 4,
            Foreground = LyricsTheme.Accent, Background = LyricsTheme.InputBackground,
            CornerRadius = new CornerRadius(2), ClipToBounds = true
        };

        public BatteryView(string name)
        {
            _name = Ui.Text(name, 12, muted: true);
            Root = new Border
            {
                Padding = new Thickness(0),
                ClipToBounds = true,
                Child = new StackPanel
                {
                    Spacing = 5,
                    Children = { _name, _value, _charging, _level }
                }
            };
            _level.Margin = new Thickness(0, 6, 0, 0);
            AutomationProperties.SetName(Root, $"{name}电量");
        }

        public Border Root { get; }

        public void SetName(string name) => _name.Text = name;

        public void Update(HeadphoneBattery? battery, string? wearing, bool connected)
        {
            _value.Text = battery is null ? "—" : $"{battery.Level}%";
            _charging.Text = string.Join(" · ", new[]
            {
                wearing,
                battery?.IsCharging == true ? "充电中" : null
            }.Where(value => !string.IsNullOrWhiteSpace(value)));
            if (string.IsNullOrEmpty(_charging.Text))
                _charging.Text = !connected ? "等待连接" : battery is null ? "暂无数据" : "剩余电量";
            _charging.Foreground = battery?.IsCharging == true ? LyricsTheme.Accent : LyricsTheme.MutedText;
            _level.Value = battery?.Level ?? 0;
            _level.Foreground = battery?.Level <= 15 ? LyricsTheme.Warning : LyricsTheme.Accent;
            AutomationProperties.SetName(Root, battery is null
                ? $"{_name.Text}电量暂无数据"
                : $"{_name.Text}电量 {battery.Level}%{(battery.IsCharging ? "，充电中" : "")}");
        }
    }
}
