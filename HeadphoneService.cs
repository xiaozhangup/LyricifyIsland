using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Tmds.DBus.Protocol;

namespace LyricifyIsland;

internal sealed partial class HeadphoneService : IDisposable
{
    private readonly SemaphoreSlim _operations = new(1, 1);
    private readonly object _stateLock = new();
    private readonly object _monitorLock = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Channel<BluetoothChange> _bluetoothChanges = Channel.CreateUnbounded<BluetoothChange>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly List<IDisposable> _bluetoothObservers = [];
    private readonly HashSet<string> _attemptedConnections = [];
    private readonly string _settingsPath = Path.Combine(SettingsStore.ConfigHome(), "lyricify-island", "headphones.json");
    private HeadphoneState _state = new();
    private HeadphoneProfile? _profile;
    private Session? _session;
    private Preferences _preferences = new();
    private CancellationTokenSource? _deviceCancellation;
    private string? _devicePath;
    private Task _bluetoothMonitor = Task.CompletedTask;
    private bool _disposed;

    public event EventHandler? StateChanged;
    public IReadOnlyList<HeadphoneDevice> Devices { get; private set; } = [];
    public HeadphoneDevice? SelectedDevice { get; private set; }
    public HeadphoneState State => Volatile.Read(ref _state);
    public bool IsBusy { get; private set; }
    public string StatusMessage { get; private set; } = "选择已配对的耳机";
    public HeadphoneConnectionMethod ConnectionMethod => _preferences.ConnectionMethod;
    public bool AutoConnect => _preferences.AutoDetect;

    public async Task InitializeAsync()
    {
        await PerformAsync(async () =>
        {
            if (File.Exists(_settingsPath))
                _preferences = JsonSerializer.Deserialize<Preferences>(await File.ReadAllTextAsync(_settingsPath, _lifetime.Token)) ?? new();
            await SubscribeBluetoothAsync();
            _bluetoothMonitor = MonitorBluetoothAsync();
            _bluetoothChanges.Writer.TryWrite(new BluetoothChange());
        }, "读取耳机设置失败");
    }

    public Task RefreshDevicesAsync() => PerformAsync(RefreshDevicesCoreAsync, "查找耳机失败");

    private async Task RefreshDevicesCoreAsync()
    {
        Devices = await ReadDevicesAsync().WaitAsync(TimeSpan.FromSeconds(5), _lifetime.Token);
        var address = SelectedDevice?.Address ?? _preferences.Address;
        var selected = Devices.FirstOrDefault(device => device.Address == address) ?? Devices.FirstOrDefault();
        if (_session is null && (selected?.Address != SelectedDevice?.Address ||
            selected?.Name != SelectedDevice?.Name || selected?.HasHuaweiSerialService != SelectedDevice?.HasHuaweiSerialService))
            ApplySelection(selected);
        else if (selected?.Address == SelectedDevice?.Address)
            SelectedDevice = selected;
        if (Devices.Count == 0) StatusMessage = "未找到耳机，请先在系统蓝牙设置中配对";
        Publish();
    }

    public Task SelectDeviceAsync(string address) => PerformAsync(async () =>
    {
        var device = Devices.FirstOrDefault(candidate => candidate.Address == address)
            ?? throw new InvalidOperationException("该耳机已从配对列表移除，请刷新设备");
        if (device.Address == SelectedDevice?.Address) return;
        SuppressCurrentConnection();
        await DisconnectCoreAsync();
        ApplySelection(device);
        _preferences.Address = device.Address;
        SavePreferences();
        _bluetoothChanges.Writer.TryWrite(new BluetoothChange());
    });

    private void ApplySelection(HeadphoneDevice? device)
    {
        SelectedDevice = device;
        _profile = ProfileForDevice(device);
        Update(_ => new HeadphoneState
        {
            ModelName = _profile?.Name ?? device?.Name,
            Capabilities = _profile?.Capabilities ?? new()
        });
        StatusMessage = device is null ? "选择已配对的耳机" : "未连接 · 打开耳机盒或戴上耳机后连接";
    }

    public Task ConnectAsync() => PerformAsync(ConnectCoreAsync, "连接失败");

    private async Task ConnectCoreAsync()
    {
        if (State.IsConnected) return;
        var device = SelectedDevice ?? throw new InvalidOperationException("请先选择耳机");
        if (!State.Capabilities.SupportsRfcomm)
            throw new InvalidOperationException("该型号不提供 RFCOMM 控制服务");
        _attemptedConnections.Add(device.ObjectPath);
        await DisconnectCoreAsync();
        StatusMessage = "正在连接耳机…";
        Publish();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        lock (_monitorLock)
        {
            _devicePath = device.ObjectPath;
            _deviceCancellation = cancellation;
        }
        try
        {
            var transport = await HeadphoneTransport.ConnectAsync(device.Address, ConnectionMethod, _profile?.ServiceUuid, cancellation.Token);
            var session = new Session(transport, cancellation, _profile?.Huawei is not null);
            _session = session;
            session.Reader = ReadLoopAsync(session);
            if (session.IsHuawei)
            {
                await InitializeHuaweiAsync(session);
                session.Poller = PollLoopAsync(session);
                return;
            }
            // Some firmwares require the product request to be the first application packet.
            var identity = await RequestAsync(session, 0x0103, []);
            RequireSuccess(identity);
            if (identity.Length != 4) throw new IOException("耳机未返回有效的产品标识");
            await OptionalQueryAsync(session, 0x0100, []);
            Update(state => state with { IsConnected = true });
            StatusMessage = $"已连接 · {State.ModelName ?? device.Name}";
            _preferences.Address = device.Address;
            SavePreferences();
            var notifications = await OptionalQueryAsync(session, 0x0200, []);
            if (notifications is { Length: >= 2 } && notifications[0] == 0 &&
                notifications.Length >= 2 + notifications[1])
            {
                var codes = notifications.AsSpan(2, notifications[1]).ToArray();
                if (session.BatchNotifications)
                    await OptionalQueryAsync(session, 0x0205, [(byte)codes.Length, ..codes]);
                else
                    foreach (var code in codes.Intersect(new byte[] { 1, 2, 3, 6 }))
                        await OptionalQueryAsync(session, 0x0201, [code]);
            }
            await RefreshCoreAsync(session);
            session.Poller = PollLoopAsync(session);
        }
        catch
        {
            await DisconnectCoreAsync();
            ReleaseDeviceCancellation(cancellation);
            throw;
        }
    }

    public Task DisconnectAsync() => PerformAsync(async () =>
    {
        SuppressCurrentConnection();
        await DisconnectCoreAsync();
        StatusMessage = "耳机控制已断开";
    });

    private async Task DisconnectCoreAsync()
    {
        var session = _session;
        if (session is null) return;
        _session = null;
        session.Cancellation.Cancel();
        await session.Reader;
        await session.Poller;
        session.Transport.Dispose();
        ReleaseDeviceCancellation(session.Cancellation);
        ClearConnectionState();
    }

    private void SuppressCurrentConnection()
    {
        if (SelectedDevice is { } device) _attemptedConnections.Add(device.ObjectPath);
    }

    private void ReleaseDeviceCancellation(CancellationTokenSource cancellation)
    {
        lock (_monitorLock)
        {
            if (ReferenceEquals(_deviceCancellation, cancellation))
            {
                _deviceCancellation = null;
                _devicePath = null;
            }
            cancellation.Dispose();
        }
    }

    private void ClearConnectionState() => Update(_ => new HeadphoneState
    {
        ModelName = _profile?.Name ?? SelectedDevice?.Name,
        Capabilities = _profile?.Capabilities ?? new()
    });

    public Task RefreshStateAsync() => PerformAsync(async () =>
    {
        await RefreshCoreAsync(ConnectedSession());
        StatusMessage = $"已连接 · {State.ModelName}";
    }, "刷新耳机状态失败");

    public Task SetNoiseModeAsync(HeadphoneNoiseMode mode) => PerformAsync(async () =>
    {
        if (_profile is null || !_profile.ModeIndices.TryGetValue(mode, out var index))
            throw new InvalidOperationException("该耳机不支持此降噪模式");
        await SetAncAsync(index, mode, null);
    });

    public Task SetNoiseLevelAsync(HeadphoneNoiseLevel level) => PerformAsync(async () =>
    {
        if (_profile is null || !_profile.LevelIndices.TryGetValue(level, out var index))
            throw new InvalidOperationException("该耳机不支持此降噪强度");
        await SetAncAsync(index, HeadphoneNoiseMode.NoiseCancellation, level);
    });

    private async Task SetAncAsync(int index, HeadphoneNoiseMode mode, HeadphoneNoiseLevel? level)
    {
        var session = ConnectedSession();
        if (session.IsHuawei)
        {
            await SetHuaweiNoiseAsync(session, mode, level);
            return;
        }
        // Air4 Pro firmware may apply ANC without a successful 8404 acknowledgement.
        // Read back once; never repeat a mode change merely because its ACK is absent.
        await OptionalQueryAsync(session, 0x0404, HeadphoneProtocol.AncPayload(index));
        var response = await RequestAsync(session, 0x010c, [1, 1]);
        var confirmed = HeadphoneProtocol.ReadAnc(new HeadphoneState(), response, _profile);
        if (confirmed.NoiseMode != mode || level is not null && confirmed.NoiseLevel != level)
            throw new IOException("耳机尚未确认所选降噪设置，请刷新状态");
        StatusMessage = $"已连接 · {State.ModelName}";
    }

    public Task SetGameModeAsync(bool enabled) => PerformAsync(async () =>
    {
        if (!State.Capabilities.HasGameMode) throw new InvalidOperationException("该耳机未报告游戏模式支持");
        if (ConnectedSession().IsHuawei)
        {
            await SendHuaweiAsync(ConnectedSession(), 0x2b6c, [1, 1, enabled ? (byte)1 : (byte)0]);
            Update(state => state with { GameMode = enabled });
            StatusMessage = "低时延指令已发送；此型号不提供低时延状态回读";
            return;
        }
        await SetFeatureAsync(ConnectedSession().GameFeature, enabled);
    });

    public Task SetWearDetectionAsync(bool enabled) => PerformAsync(async () =>
    {
        if (!State.Capabilities.HasWearDetection) throw new InvalidOperationException("该耳机不支持佩戴检测开关");
        if (ConnectedSession().IsHuawei)
        {
            await SetHuaweiAndReadAsync(0x2b10, [1, 1, enabled ? (byte)1 : (byte)0], 0x2b11, [1, 0],
                state => state.WearDetection == enabled);
            return;
        }
        await SetFeatureAsync(4, enabled);
    });

    public Task SetDualDeviceAsync(bool enabled) => PerformAsync(async () =>
    {
        if (!State.Capabilities.HasDualDevice) throw new InvalidOperationException("该耳机不支持双设备连接");
        if (ConnectedSession().IsHuawei)
        {
            await SetHuaweiAndReadAsync(0x2b2e, [1, 1, enabled ? (byte)1 : (byte)0], 0x2b2f, [1, 0],
                state => state.DualDevice == enabled);
            return;
        }
        await SetFeatureAsync(0x11, enabled);
        await OptionalQueryAsync(ConnectedSession(), 0x0112, []);
    });

    public Task SetSpatialModeAsync(int mode) => PerformAsync(async () =>
    {
        var capabilities = State.Capabilities;
        if (!capabilities.SpatialModes.Contains(mode)) throw new InvalidOperationException("该耳机不支持此空间音频模式");
        if (ConnectedSession().IsHuawei)
        {
            var wireMode = _profile!.Huawei!.ReversedSpatialModes && mode != 0 ? 3 - mode : mode;
            await SetHuaweiAndReadAsync(0x2bb4, [1, 1, 0x18, 2, 1, (byte)wireMode], 0x2bb4, [1, 1, 0x18, 2, 0, 3, 0],
                state => state.SpatialMode == mode);
            return;
        }
        if (capabilities.HasSpatialSound)
        {
            if (mode != 0 && capabilities.GameSoundMutexSpatial && State.GameSound != false)
                await SetGameSoundCoreAsync(false);
            await SetFeatureAsync(0x1b, mode != 0);
        }
        else await SetAndQueryAsync(0x0422, [(byte)mode], 0x012a, []);
    });

    public Task SetGameSoundAsync(bool enabled) => PerformAsync(async () =>
    {
        var capabilities = State.Capabilities;
        if (!capabilities.HasGameSound) throw new InvalidOperationException("该耳机不支持游戏音效");
        if (enabled && capabilities.HasSpatialSound && capabilities.GameSoundMutexSpatial && State.SpatialMode != 0)
            await SetFeatureAsync(0x1b, false);
        await SetGameSoundCoreAsync(enabled);
    });

    private Task SetGameSoundCoreAsync(bool enabled) => SetAndQueryAsync(0x0423,
        [enabled ? (byte)State.Capabilities.GameSoundType : (byte)0, 1], 0x012b, []);

    public Task SetEqualizerAsync(int id) => PerformAsync(async () =>
    {
        if (!State.Capabilities.Equalizers.Any(preset => preset.Id == id))
            throw new InvalidOperationException("该均衡器预设不在耳机列表中");
        if (ConnectedSession().IsHuawei)
        {
            await SetHuaweiAndReadAsync(0x2b49, HuaweiHeadphoneProtocol.EqualizerPayload(id), 0x2b4a, [2, 0],
                state => state.EqualizerId == id);
            return;
        }
        if (State.Capabilities.GameSoundMutexEqualizer && State.GameSound != false)
            await SetGameSoundCoreAsync(false);
        await SetAndQueryAsync(0x0406, [checked((byte)id)], 0x010f, []);
    });

    public Task SwitchActiveDeviceAsync(string address) => PerformAsync(async () =>
    {
        var device = State.ConnectedDevices.FirstOrDefault(candidate => candidate.Address == address);
        if (!State.Capabilities.HasMultiDeviceSwitch || device is null)
            throw new InvalidOperationException("该设备不支持连接管理或已离开耳机设备列表");
        if (device.ConnectionState == 2 && !State.Capabilities.CanSetDevicePriority)
            throw new InvalidOperationException("该耳机未提供优先设备设置");
        var mac = Convert.FromHexString(address.Replace(":", ""));
        byte[] payload = device.ConnectionState == 2 ? [4, 1, ..mac] : [1, ..mac];
        await SetAndQueryAsync(0x0429, payload, 0x0112, []);
    });

    public Task SetAutomaticDevicePriorityAsync() => PerformAsync(async () =>
    {
        if (!State.Capabilities.HasMultiDeviceSwitch || !State.Capabilities.CanSetDevicePriority)
            throw new InvalidOperationException("该耳机未提供优先设备设置");
        await SetAndQueryAsync(0x0429, [4, 0], 0x0112, []);
    });

    private Task SetFeatureAsync(int feature, bool enabled) =>
        SetAndQueryAsync(0x0403, [(byte)feature, enabled ? (byte)1 : (byte)0], 0x010d, FeatureQuery);

    private async Task SetAndQueryAsync(ushort command, byte[] payload, ushort query, byte[] queryPayload)
    {
        var session = ConnectedSession();
        RequireSuccess(await RequestAsync(session, command, payload));
        await RequestAsync(session, query, queryPayload);
        StatusMessage = $"已连接 · {State.ModelName}";
    }

    private static void RequireSuccess(byte[] payload)
    {
        if (payload.Length == 0) throw new IOException("耳机未返回操作结果");
        if (payload[0] != 0) throw new IOException($"耳机拒绝了操作（状态 {payload[0]}）");
    }

    private Session ConnectedSession() => _session is { } session && State.IsConnected ? session :
        throw new InvalidOperationException("耳机尚未连接");

    private static readonly byte[] FeatureQuery = [11, 5, 4, 0x0b, 0x11, 0x13, 0x18, 6, 0x1b, 0x1c, 0x27, 0x28];

    private async Task RefreshCoreAsync(Session session)
    {
        if (session.IsHuawei)
        {
            await RefreshHuaweiAsync(session);
            return;
        }
        // The batch status query also wakes sleeping earbuds before battery and other reads.
        await OptionalQueryAsync(session, 0x010d, FeatureQuery);
        await RequestAsync(session, 0x0106, []);
        if (State.Capabilities.NoiseModes.Count != 0) await OptionalQueryAsync(session, 0x010c, [1, 1]);
        if (State.NoiseLevel == HeadphoneNoiseLevel.Smart) await OptionalQueryAsync(session, 0x010c, [4, 1]);
        await OptionalQueryAsync(session, 0x0105, []);
        await OptionalQueryAsync(session, 0x0114, []);
        if (State.Capabilities.SpatialModes.Count != 0 && !State.Capabilities.HasSpatialSound)
            await OptionalQueryAsync(session, 0x012a, []);
        if (State.Capabilities.HasGameSound) await OptionalQueryAsync(session, 0x012b, []);
        if (State.Capabilities.HasDualDevice) await OptionalQueryAsync(session, 0x0112, []);
        if (State.Capabilities.Equalizers.Count != 0)
        {
            await OptionalQueryAsync(session, 0x010f, []);
            await OptionalQueryAsync(session, 0x0122, [1, 5]);
        }
    }

    private async Task PollLoopAsync(Session session)
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), session.Cancellation.Token);
                await _operations.WaitAsync(session.Cancellation.Token);
                try { await RefreshCoreAsync(session); }
                finally { _operations.Release(); }
            }
        }
        catch (OperationCanceledException) when (session.Cancellation.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (session.Cancellation.IsCancellationRequested) { }
        catch (Exception exception) when (IsIoFailure(exception))
        { ConnectionLost(session, $"耳机连接中断：{exception.Message}"); }
    }

    private async Task<byte[]?> OptionalQueryAsync(Session session, ushort command, byte[] payload)
    {
        try { return await RequestAsync(session, command, payload); }
        catch (TimeoutException) { return null; } // Optional commands are absent on older firmware.
    }

    private static async Task<byte[]> RequestAsync(Session session, ushort command, byte[] payload)
    {
        byte sequence;
        TaskCompletionSource<byte[]> completion;
        await session.Writes.WaitAsync(session.Cancellation.Token);
        try
        {
            sequence = session.IsHuawei ? (byte)0 : session.Sequence == 0xfe ? (byte)1 : ++session.Sequence;
            session.Sequence = sequence;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            session.Pending[(command, sequence)] = completion;
            try { await session.Transport.WriteAsync(session.IsHuawei
                ? HuaweiHeadphoneProtocol.Encode(command, payload)
                : HeadphoneProtocol.Encode(command, sequence, payload), session.Cancellation.Token); }
            catch { session.Pending.TryRemove((command, sequence), out _); throw; }
        }
        finally { session.Writes.Release(); }
        try { return await completion.Task.WaitAsync(TimeSpan.FromSeconds(3), session.Cancellation.Token); }
        catch (TimeoutException) { throw new TimeoutException($"耳机未回应命令 {command:X4}"); }
        finally { session.Pending.TryRemove((command, sequence), out _); }
    }

    private async Task ReadLoopAsync(Session session)
    {
        var framer = new HeadphoneProtocol();
        var huaweiFramer = session.IsHuawei ? new HuaweiHeadphoneProtocol() : null;
        var bytes = new byte[4096];
        try
        {
            while (true)
            {
                var count = await session.Transport.ReadAsync(bytes, session.Cancellation.Token);
                if (count == 0) throw new IOException("耳机关闭了控制连接");
                foreach (var packet in huaweiFramer is not null
                    ? huaweiFramer.Append(bytes.AsSpan(0, count)) : framer.Append(bytes.AsSpan(0, count)))
                {
                    if (session.IsHuawei)
                    {
                        ApplyHuaweiPacket(packet);
                        var queryCommand = packet.Command == 0x0127 ? (ushort)0x0108 : packet.Command;
                        if (session.Pending.TryGetValue((queryCommand, 0), out var huaweiCompletion) &&
                            HuaweiHeadphoneProtocol.IsQueryResponse(packet))
                            huaweiCompletion.TrySetResult(packet.Payload);
                        continue;
                    }
                    ApplyPacket(session, packet);
                    if (packet.Sequence != 0xff && (packet.Command & 0x8000) != 0 &&
                        session.Pending.TryGetValue(((ushort)(packet.Command & 0x7fff), packet.Sequence), out var completion))
                        completion.TrySetResult(packet.Payload);
                }
            }
        }
        catch (OperationCanceledException) when (session.Cancellation.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (session.Cancellation.IsCancellationRequested) { }
        catch (Exception exception) when (IsIoFailure(exception))
        { ConnectionLost(session, $"耳机连接中断：{exception.Message}"); }
    }

    private void ConnectionLost(Session session, string message)
    {
        if (!ReferenceEquals(_session, session)) return;
        session.Cancellation.Cancel();
        session.Transport.Dispose();
        Update(state => state with { IsConnected = false });
        StatusMessage = message;
        Publish();
    }

    private void ApplyPacket(Session session, HeadphonePacket packet)
    {
        var payload = packet.Payload;
        lock (_stateLock)
        {
            var state = _state;
            switch (packet.Command)
            {
                case 0x8103 when payload.Length == 4 && payload[0] == 0:
                    var product = $"{payload[3]:X2}{payload[2]:X2}{payload[1]:X2}";
                    _profile = HeadphoneCatalog.Find(product, SelectedDevice?.Name);
                    session.GameFeature = _profile?.GameFeature ?? 6;
                    state = state with { ProductId = product, ModelName = _profile?.Name ?? SelectedDevice?.Name,
                        Capabilities = _profile?.Capabilities ?? new() };
                    break;
                case 0x8100 when payload.Length > 1 && payload[0] == 0:
                    session.BatchNotifications = HasCapability(payload, 22);
                    var hasSpatial = state.Capabilities.SpatialModes.Count != 0;
                    var modernSpatial = hasSpatial && HasCapability(payload, 47);
                    state = state with { Capabilities = state.Capabilities with
                    {
                        HasSpatialSound = hasSpatial && !modernSpatial,
                        CanSetDevicePriority = state.Capabilities.MultiDeviceVersion >= 2 && HasCapability(payload, 59),
                        SpatialModes = hasSpatial ? modernSpatial ? state.Capabilities.SpatialModes : new[] { 0, 1 } : []
                    }};
                    break;
                case 0x8106:
                    state = HeadphoneProtocol.ReadBattery(state, payload);
                    break;
                case 0x810c:
                    state = payload.Length >= 4 && payload[1] == 4 && payload[2] == 1
                        ? ReadRealtimeNoise(state, payload[3])
                        : HeadphoneProtocol.ReadAnc(state, payload, _profile);
                    break;
                case 0x810d:
                    if (HeadphoneProtocol.ReadFeatures(payload) is { } features)
                    {
                        session.GameFeature = state.Capabilities.HasGameSound && features.ContainsKey(0x28) ? 0x28
                            : features.ContainsKey(6) ? 6 : 0x28;
                        state = state with
                        {
                            GameMode = Feature(features, (byte)session.GameFeature),
                            WearDetection = Feature(features, 4), DualDevice = Feature(features, 0x11),
                            SpatialMode = state.Capabilities.HasSpatialSound ? Feature(features, 0x1b) is { } enabled ? enabled ? 1 : 0 : null : state.SpatialMode,
                            Capabilities = state.Capabilities with
                            {
                                HasGameMode = features.ContainsKey(6) || features.ContainsKey(0x28),
                                HasWearDetection = state.Capabilities.HasWearDetection && features.ContainsKey(4),
                                HasDualDevice = state.Capabilities.HasDualDevice && features.ContainsKey(0x11),
                                HasSpatialSound = state.Capabilities.HasSpatialSound && features.ContainsKey(0x1b),
                                SpatialModes = state.Capabilities.HasSpatialSound && !features.ContainsKey(0x1b)
                                    ? [] : state.Capabilities.SpatialModes
                            }
                        };
                    }
                    break;
                case 0x812a when payload.Length >= 2 && payload[0] == 0 && payload[1] <= 2:
                    state = state with { SpatialMode = payload[1] };
                    break;
                case 0x0510 when payload.Length >= 1 && payload[0] <= 2:
                    state = state with { SpatialMode = payload[0] };
                    break;
                case 0x812b when payload.Length >= 2 && payload[0] == 0:
                    state = state with { GameSound = payload[1] != 0 };
                    break;
                case 0x810f or 0x0504 when payload.Length >= 2 && payload[0] == 0:
                    state = state with { EqualizerId = payload[1] };
                    break;
                case 0x8122:
                    if (HeadphoneProtocol.ReadEqualizers(payload) is { } equalizers)
                        state = state with { EqualizerId = equalizers.Selected ?? state.EqualizerId,
                            Capabilities = state.Capabilities with { Equalizers = state.Capabilities.Equalizers
                                .Concat(equalizers.Entries).GroupBy(entry => entry.Id).Select(group => group.Last()).ToArray() } };
                    break;
                case 0x8112:
                    if (HeadphoneProtocol.ReadConnectedDevices(payload, false) is { } devices)
                        state = state with { ConnectedDevices = devices };
                    break;
                case 0x8105 when payload.Length >= 2 && payload[0] == 0:
                    state = state with { FirmwareVersion = ReadFirmware(payload.AsSpan(2)) };
                    break;
                case 0x8114 when payload.Length >= 2 && payload[0] == 0:
                    int? codec = payload.Length == 2 ? payload[1] : null;
                    if (payload.Length >= 2 + payload[1] * 2)
                        for (var i = 0; i < payload[1]; i++)
                            if (payload[3 + i * 2] != 0) { codec = payload[2 + i * 2]; break; }
                    if (codec is not null) state = state with { CodecName = codec switch
                    { 0 => "SBC", 1 => "LDAC", 2 => "AAC", 3 or 8 => "LHDC", 4 => "LC3",
                        5 => "aptX", 6 => "aptX HD", 7 => "aptX Adaptive", _ => $"编解码 {codec}" } };
                    break;
                case 0x0204:
                    state = ReadNotification(state, payload);
                    if (payload.Length > 0 && payload[0] == 6)
                        _ = RefreshConnectedDevicesAsync(session);
                    break;
            }
            Volatile.Write(ref _state, state);
        }
        Publish();
    }

    private HeadphoneState ReadNotification(HeadphoneState state, byte[] payload)
    {
        if (payload.Length < 2) return state;
        switch (payload[0])
        {
            case 1:
                if (payload.Length >= 9 && payload[1] == 0 && payload[2] == 4)
                    return HeadphoneProtocol.ReadBattery(state, payload.AsSpan(1));
                if (payload.Length == 2 + payload[1] * 2)
                    return HeadphoneProtocol.ReadBattery(state, payload.AsSpan(2));
                break;
            case 2:
                if (payload.Length != 2 + payload[1] * 2) break;
                for (var i = 0; i < payload[1]; i++)
                {
                    var text = payload[3 + 2 * i] switch { 1 or 5 => "已摘下", 3 or 7 => "佩戴中", 4 => "在盒中", _ => null };
                    if (payload[2 + 2 * i] == 1) state = state with { LeftWearing = text };
                    if (payload[2 + 2 * i] == 2) state = state with { RightWearing = text };
                }
                break;
            case 3 or 4:
                state = payload.Length >= 4 && payload[1] == 4 && payload[2] == 1
                    ? ReadRealtimeNoise(state, payload[3]) with
                    { NoiseMode = HeadphoneNoiseMode.NoiseCancellation, NoiseLevel = HeadphoneNoiseLevel.Smart }
                    : HeadphoneProtocol.ReadAnc(state, payload.AsSpan(1), _profile);
                break;
        }
        return state;
    }

    private static HeadphoneState ReadRealtimeNoise(HeadphoneState state, byte bitmap) =>
        state with { RealtimeNoiseLevel = (bitmap & 0x10) != 0 ? HeadphoneNoiseLevel.Deep
            : (bitmap & 0x20) != 0 ? HeadphoneNoiseLevel.Moderate
            : (bitmap & 0x40) != 0 ? HeadphoneNoiseLevel.Light : null };

    private static string? ReadFirmware(ReadOnlySpan<byte> payload)
    {
        var raw = Encoding.UTF8.GetString(payload).Trim('\0', ' ');
        if (raw.Length == 0) return null;
        var fields = raw.Split(',');
        if (fields.Length < 3) return raw;
        var versions = new List<string>();
        for (var i = 0; i + 2 < fields.Length; i += 3)
            if (int.TryParse(fields[i], out _) && !string.IsNullOrWhiteSpace(fields[i + 2]))
                versions.Add(fields[i + 2].Trim());
        return versions.Count == 0 ? null : string.Join(" · ", versions.Distinct());
    }

    private async Task RefreshConnectedDevicesAsync(Session session)
    {
        if (Interlocked.Exchange(ref session.DevicesRefreshPending, 1) != 0) return;
        try { await OptionalQueryAsync(session, 0x0112, []); }
        catch (OperationCanceledException) when (session.Cancellation.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (session.Cancellation.IsCancellationRequested) { }
        catch (Exception exception) when (IsIoFailure(exception))
        { ConnectionLost(session, $"耳机连接中断：{exception.Message}"); }
        finally { Volatile.Write(ref session.DevicesRefreshPending, 0); }
    }

    private static bool HasCapability(byte[] payload, int bit) =>
        payload.Length > 1 + bit / 8 && (payload[1 + bit / 8] & (1 << (bit % 8))) != 0;
    private static bool? Feature(Dictionary<byte, bool> features, byte id) => features.TryGetValue(id, out var value) ? value : null;

    private async Task SubscribeBluetoothAsync()
    {
        try
        {
            await WatchBluetoothSignalAsync(new MatchRule
            {
                Type = MessageType.Signal, Sender = "org.bluez",
                Interface = "org.freedesktop.DBus.Properties", Member = "PropertiesChanged",
                Arg0 = "org.bluez.Device1"
            }, static (message, _) =>
            {
                var reader = message.GetBodyReader();
                _ = reader.ReadString();
                var changed = reader.ReadDictionaryOfStringToVariantValue();
                var invalidated = reader.ReadArrayOfString();
                if (!changed.Keys.Concat(invalidated).Any(IsDeviceDiscoveryProperty)) return null;
                return new BluetoothChange(message.PathAsString,
                    changed.TryGetValue("Connected", out var connected) && !connected.GetBool());
            });
            await WatchBluetoothSignalAsync(new MatchRule
            {
                Type = MessageType.Signal, Sender = "org.bluez",
                Interface = "org.freedesktop.DBus.ObjectManager", Member = "InterfacesAdded"
            }, static (message, _) =>
            {
                var reader = message.GetBodyReader();
                var path = reader.ReadObjectPath().ToString();
                var interfaces = reader.ReadDictionaryStart();
                var hasDevice = false;
                while (reader.HasNext(interfaces))
                {
                    reader.AlignStruct();
                    hasDevice |= reader.ReadString() == "org.bluez.Device1";
                    _ = reader.ReadDictionaryOfStringToVariantValue();
                }
                return hasDevice ? new BluetoothChange(path) : null;
            });
            await WatchBluetoothSignalAsync(new MatchRule
            {
                Type = MessageType.Signal, Sender = "org.bluez",
                Interface = "org.freedesktop.DBus.ObjectManager", Member = "InterfacesRemoved"
            }, static (message, _) =>
            {
                var reader = message.GetBodyReader();
                var path = reader.ReadObjectPath().ToString();
                return reader.ReadArrayOfString().Contains("org.bluez.Device1")
                    ? new BluetoothChange(path, Disconnected: true) : null;
            });
            await WatchBluetoothSignalAsync(new MatchRule
            {
                Type = MessageType.Signal, Sender = "org.freedesktop.DBus",
                Interface = "org.freedesktop.DBus", Member = "NameOwnerChanged", Arg0 = "org.bluez"
            }, static (message, _) =>
            {
                var reader = message.GetBodyReader();
                _ = reader.ReadString();
                _ = reader.ReadString();
                return new BluetoothChange(ResetConnections: true, ServiceAvailable: reader.ReadString().Length > 0);
            });
        }
        catch { DisposeBluetoothObservers(); throw; }
    }

    private static bool IsDeviceDiscoveryProperty(string name) =>
        name is "Connected" or "ServicesResolved" or "Paired" or "Name" or "Alias" or "UUIDs";

    private async Task WatchBluetoothSignalAsync(MatchRule rule, MessageValueReader<BluetoothChange?> reader)
    {
        _lifetime.Token.ThrowIfCancellationRequested();
        var observer = await DBusConnection.System.AddMatchAsync(rule, reader,
            static notification => ((HeadphoneService)notification.State!).OnBluetoothSignal(
                notification.IsCompletion ? notification.Exception : null,
                notification.HasValue ? notification.Value : null),
            flags: ObserverFlags.EmitOnConnectionClosed | ObserverFlags.EmitOnReaderFailed,
            state: this, emitOnCapturedContext: false);
        // A watch can finish registering after shutdown. In that case release the returned match immediately.
        lock (_monitorLock)
        {
            if (!_disposed) { _bluetoothObservers.Add(observer); return; }
        }
        observer.Dispose();
        _lifetime.Token.ThrowIfCancellationRequested();
    }

    private void OnBluetoothSignal(Exception? exception, BluetoothChange? change)
    {
        if (_disposed) return;
        if (exception is not null) change = new BluetoothChange(ResetConnections: true, Error: exception);
        if (change is null) return;
        if (change.Disconnected || change.ResetConnections)
        {
            // Cancel an in-flight connection immediately; the worker may currently be awaiting its handshake.
            lock (_monitorLock)
                if (change.ResetConnections || change.Path == _devicePath)
                    _deviceCancellation?.Cancel();
        }
        _bluetoothChanges.Writer.TryWrite(change);
    }

    private async Task MonitorBluetoothAsync()
    {
        try
        {
            while (await _bluetoothChanges.Reader.WaitToReadAsync(_lifetime.Token))
            {
                var disconnected = new HashSet<string>();
                var reset = false;
                bool? serviceAvailable = null;
                Exception? error = null;
                while (_bluetoothChanges.Reader.TryRead(out var change))
                {
                    if (change.Disconnected && change.Path is { } path) disconnected.Add(path);
                    reset |= change.ResetConnections;
                    serviceAvailable = change.ServiceAvailable ?? serviceAvailable;
                    error = change.Error ?? error;
                }
                await PerformAsync(async () =>
                {
                    if (reset) _attemptedConnections.Clear();
                    foreach (var path in disconnected) _attemptedConnections.Remove(path);
                    if (reset || SelectedDevice is { } selected && disconnected.Contains(selected.ObjectPath))
                        await DisconnectCoreAsync();
                    if (error is not null || serviceAvailable == false)
                    {
                        Devices = [];
                        ApplySelection(null);
                        StatusMessage = error is null ? "系统蓝牙服务已停止" : $"蓝牙监测已中断：{error.Message}";
                        return;
                    }
                    await RefreshDevicesCoreAsync();
                    _attemptedConnections.IntersectWith(Devices.Where(device => device.IsConnected)
                        .Select(device => device.ObjectPath));
                    if (_session is not null && (!State.IsConnected ||
                        !Devices.Any(device => device.ObjectPath == SelectedDevice?.ObjectPath && device.IsConnected)))
                    {
                        await DisconnectCoreAsync();
                        StatusMessage = "系统蓝牙连接已断开";
                    }
                    if (!AutoConnect || _session is not null) return;
                    var candidate = Devices.Where(device => device.IsConnected && device.ServicesResolved &&
                            !_attemptedConnections.Contains(device.ObjectPath) &&
                            ProfileForDevice(device)?.Capabilities.SupportsRfcomm != false &&
                            ProfileForDevice(device) != HuaweiHeadphoneCatalog.Unknown)
                        .OrderByDescending(device => device.Address == _preferences.Address)
                        .FirstOrDefault();
                    if (candidate is null) return;
                    ApplySelection(candidate);
                    await ConnectCoreAsync();
                }, "自动连接耳机失败");
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    private void DisposeBluetoothObservers()
    {
        IDisposable[] observers;
        lock (_monitorLock)
        {
            observers = _bluetoothObservers.ToArray();
            _bluetoothObservers.Clear();
        }
        foreach (var observer in observers) observer.Dispose();
    }

    public void SetConnectionMethod(HeadphoneConnectionMethod method)
    {
        _preferences.ConnectionMethod = method;
        SavePreferencesSafely();
    }

    public void SetAutoConnect(bool enabled)
    {
        _preferences.AutoDetect = enabled;
        _preferences.Address = SelectedDevice?.Address;
        SavePreferencesSafely();
        if (enabled) _bluetoothChanges.Writer.TryWrite(new BluetoothChange());
    }

    private void SavePreferencesSafely()
    {
        try { SavePreferences(); }
        catch (Exception exception) when (IsIoFailure(exception)) { StatusMessage = $"保存耳机设置失败：{exception.Message}"; }
        Publish();
    }

    private void SavePreferences()
    {
        var directory = Path.GetDirectoryName(_settingsPath)!;
        Directory.CreateDirectory(directory);
        if (OperatingSystem.IsLinux())
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var temporary = _settingsPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_preferences, new JsonSerializerOptions { WriteIndented = true }));
        if (OperatingSystem.IsLinux())
            File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temporary, _settingsPath, true);
    }

    private async Task PerformAsync(Func<Task> action, string errorPrefix = "操作失败")
    {
        if (_disposed) return;
        try
        {
            await _operations.WaitAsync(_lifetime.Token);
            try
            {
                IsBusy = true;
                Publish();
                await action();
            }
            finally { IsBusy = false; _operations.Release(); Publish(); }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (OperationCanceledException) { StatusMessage = $"{errorPrefix}：耳机连接已结束"; Publish(); }
        catch (Exception exception) when (IsIoFailure(exception) || exception is InvalidOperationException or JsonException)
        { StatusMessage = $"{errorPrefix}：{exception.Message}"; Publish(); }
    }

    private static bool IsIoFailure(Exception exception) => exception is IOException or Win32Exception or TimeoutException or
        UnauthorizedAccessException or DBusExceptionBase;

    private void Update(Func<HeadphoneState, HeadphoneState> update)
    {
        lock (_stateLock) Volatile.Write(ref _state, update(_state));
        Publish();
    }
    private void Publish() => StateChanged?.Invoke(this, EventArgs.Empty);

    private Task<IReadOnlyList<HeadphoneDevice>> ReadDevicesAsync()
    {
        var connection = DBusConnection.System;
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: "org.bluez", path: "/",
            @interface: "org.freedesktop.DBus.ObjectManager", member: "GetManagedObjects");
        return connection.CallMethodAsync(writer.CreateMessage(), (message, _) =>
        {
            var reader = message.GetBodyReader();
            var devices = new List<HeadphoneDevice>();
            var objects = reader.ReadDictionaryStart();
            while (reader.HasNext(objects))
            {
                reader.AlignStruct();
                var path = reader.ReadObjectPath().ToString();
                var interfaces = reader.ReadDictionaryStart();
                while (reader.HasNext(interfaces))
                {
                    reader.AlignStruct();
                    var name = reader.ReadString();
                    var properties = reader.ReadDictionaryOfStringToVariantValue();
                    if (name != "org.bluez.Device1" || !properties.TryGetValue("Paired", out var paired) || !paired.GetBool()) continue;
                    var address = properties["Address"].GetString();
                    var alias = properties.TryGetValue("Alias", out var aliasValue) ? aliasValue.GetString() : address;
                    var deviceName = properties.TryGetValue("Name", out var nameValue) ? nameValue.GetString() : alias;
                    var uuids = properties.TryGetValue("UUIDs", out var uuidValue) ? uuidValue.GetArray<string>() : [];
                    var oppoService = uuids.Any(uuid => HeadphoneTransport.ServiceUuids.Contains(uuid, StringComparer.OrdinalIgnoreCase));
                    // Standard SPP alone also describes serial peripherals. Only list audio devices for manual probing.
                    var huaweiSerial = !oppoService && uuids.Contains(HuaweiHeadphoneCatalog.ServiceUuid, StringComparer.OrdinalIgnoreCase) &&
                        uuids.Any(uuid => uuid.Equals("0000110b-0000-1000-8000-00805f9b34fb", StringComparison.OrdinalIgnoreCase) ||
                            uuid.Equals("0000111e-0000-1000-8000-00805f9b34fb", StringComparison.OrdinalIgnoreCase));
                    if (HeadphoneCatalog.Find(null, deviceName) is null &&
                        !_preferences.HuaweiModels.ContainsKey(address) && !huaweiSerial &&
                        !oppoService) continue;
                    devices.Add(new HeadphoneDevice(address, deviceName,
                        properties.TryGetValue("Connected", out var connected) && connected.GetBool(), path)
                    {
                        ServicesResolved = properties.TryGetValue("ServicesResolved", out var resolved) && resolved.GetBool(),
                        HasHuaweiSerialService = huaweiSerial
                    });
                }
            }
            return (IReadOnlyList<HeadphoneDevice>)devices.OrderByDescending(device => device.IsConnected)
                .ThenBy(device => device.Name).ToArray();
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        lock (_monitorLock) _deviceCancellation?.Cancel();
        DisposeBluetoothObservers();
        _bluetoothChanges.Writer.TryComplete();
        _session?.Transport.Dispose();
    }

    private sealed class Preferences
    {
        public string? Address { get; set; }
        // Earlier versions stored a startup-only AutoConnect flag. Absence of AutoDetect enables the new behavior.
        public bool AutoDetect { get; set; } = true;
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? AutoConnect { get; set; }
        public HeadphoneConnectionMethod ConnectionMethod { get; set; }
        public Dictionary<string, string> HuaweiModels { get; set; } = new();
    }

    private sealed record BluetoothChange(string? Path = null, bool Disconnected = false,
        bool ResetConnections = false, bool? ServiceAvailable = null, Exception? Error = null);

    private sealed class Session(HeadphoneTransport transport, CancellationTokenSource cancellation, bool isHuawei)
    {
        public bool IsHuawei { get; } = isHuawei;
        public HeadphoneTransport Transport { get; } = transport;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public SemaphoreSlim Writes { get; } = new(1, 1);
        public ConcurrentDictionary<(ushort, byte), TaskCompletionSource<byte[]>> Pending { get; } = new();
        public byte Sequence;
        public int GameFeature = 6;
        public bool BatchNotifications;
        public int DevicesRefreshPending;
        public Task Reader { get; set; } = Task.CompletedTask;
        public Task Poller { get; set; } = Task.CompletedTask;
    }
}
