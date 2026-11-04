using System.Text;

namespace LyricifyIsland;

internal sealed partial class HeadphoneService
{
    public string HuaweiModelOverride => SelectedDevice is { } device &&
        _preferences.HuaweiModels.TryGetValue(device.Address, out var id) ? id : "";
    public bool UsesHuaweiProtocol => _profile?.Huawei is not null;

    private HeadphoneProfile? ProfileForDevice(HeadphoneDevice? device)
    {
        if (device is null) return null;
        if (_preferences.HuaweiModels.TryGetValue(device.Address, out var id) &&
            HuaweiHeadphoneCatalog.FindById(id) is { } selected)
            return selected;
        return HeadphoneCatalog.Find(null, device.Name)
            ?? (device.HasHuaweiSerialService ? HuaweiHeadphoneCatalog.Unknown : null);
    }

    public Task SetHuaweiModelAsync(string id) => PerformAsync(async () =>
    {
        var device = SelectedDevice ?? throw new InvalidOperationException("请先选择耳机");
        if (id.Length != 0 && HuaweiHeadphoneCatalog.FindById(id) is null)
            throw new InvalidOperationException("请选择列表中的设备型号");
        await DisconnectCoreAsync();
        if (id.Length == 0) _preferences.HuaweiModels.Remove(device.Address);
        else _preferences.HuaweiModels[device.Address] = id;
        ApplySelection(device);
        SavePreferences();
        _attemptedConnections.Remove(device.ObjectPath);
        _bluetoothChanges.Writer.TryWrite(new BluetoothChange());
    }, "保存耳机型号失败");

    private async Task InitializeHuaweiAsync(Session session)
    {
        if (State.Capabilities.HasBattery)
        {
            // Verified identity takes precedence over a Bluetooth name or a manual selection.
            await OptionalQueryAsync(session, 0x0107,
                [1, 0, 2, 0, 3, 0, 4, 0, 5, 0, 6, 0, 7, 0, 8, 0, 9, 0, 10, 0, 11, 0, 12, 0, 15, 0, 24, 0, 25, 0]);
        }
        await RefreshHuaweiAsync(session);
        session.Cancellation.Token.ThrowIfCancellationRequested();
        Update(state => state with { IsConnected = true });
        StatusMessage = $"已连接 · {State.ModelName}";
        _preferences.Address = SelectedDevice!.Address;
        SavePreferences();
    }

    private async Task RefreshHuaweiAsync(Session session)
    {
        var capabilities = State.Capabilities;
        if (capabilities.HasBattery) await RequestAsync(session, 0x0108, [1, 0, 2, 0, 3, 0]);
        if (capabilities.NoiseModes.Count > 0 && capabilities.CanReadNoiseMode)
            await OptionalQueryAsync(session, 0x2b2a, [1, 0]);
        if (capabilities.HasWearDetection) await OptionalQueryAsync(session, 0x2b11, [1, 0]);
        if (capabilities.Equalizers.Count > 0) await OptionalQueryAsync(session, 0x2b4a, [2, 0]);
        if (capabilities.SpatialModes.Count > 0)
            await OptionalQueryAsync(session, 0x2bb4, [1, 1, 0x18, 2, 0, 3, 0]);
        if (capabilities.HasDualDevice) await OptionalQueryAsync(session, 0x2b2f, [1, 0]);
    }

    private void ApplyHuaweiPacket(HeadphonePacket packet)
    {
        var fields = HuaweiHeadphoneProtocol.ReadFields(packet.Payload)!; // Framer validated complete, unique TLVs.
        lock (_stateLock)
        {
            var state = _state;
            if (packet.Command == 0x0107)
            {
                if (HuaweiHeadphoneProtocol.ReadModelId(fields) is { } modelId)
                {
                    _profile = HuaweiHeadphoneCatalog.FindByModelId(modelId) ?? HuaweiHeadphoneCatalog.Unknown;
                    state = state with
                    {
                        ProductId = modelId, ModelName = _profile.Name, Capabilities = _profile.Capabilities
                    };
                }
                if (fields.TryGetValue(7, out var firmware) && firmware.Length > 0)
                    state = state with { FirmwareVersion = Encoding.UTF8.GetString(firmware).Trim('\0', ' ') };
            }
            Volatile.Write(ref _state, HuaweiHeadphoneProtocol.ReadState(state, _profile!, packet.Command, fields));
        }
        Publish();
    }

    private async Task SetHuaweiNoiseAsync(Session session, HeadphoneNoiseMode mode, HeadphoneNoiseLevel? level)
    {
        var profile = _profile!;
        var payload = HuaweiHeadphoneProtocol.NoisePayload(profile, mode, level ?? State.NoiseLevel);
        if (!profile.Capabilities.CanReadNoiseMode)
        {
            await SendHuaweiAsync(session, 0x2b04, payload);
            StatusMessage = "降噪指令已发送；此型号不支持降噪状态回读";
            return;
        }
        await SetHuaweiAndReadAsync(0x2b04, payload, 0x2b2a, [1, 0],
            state => state.NoiseMode == mode && (level is null || state.NoiseLevel == level));
    }

    private async Task SetHuaweiAndReadAsync(ushort command, byte[] payload, ushort query, byte[] queryPayload,
        Func<HeadphoneState, bool> confirmed)
    {
        var session = ConnectedSession();
        await SendHuaweiAsync(session, command, payload);
        // HuaweiPods' wear-detection captures require time for settings to be applied before readback.
        await Task.Delay(200, session.Cancellation.Token);
        var response = await RequestAsync(session, query, queryPayload);
        var readback = HuaweiHeadphoneProtocol.ReadState(new HeadphoneState(), _profile!, query,
            HuaweiHeadphoneProtocol.ReadFields(response)!);
        if (!confirmed(readback)) throw new IOException("耳机尚未确认所选设置，请更新状态");
        StatusMessage = $"已连接 · {State.ModelName}";
    }

    private static async Task SendHuaweiAsync(Session session, ushort command, byte[] payload)
    {
        await session.Writes.WaitAsync(session.Cancellation.Token);
        try { await session.Transport.WriteAsync(HuaweiHeadphoneProtocol.Encode(command, payload), session.Cancellation.Token); }
        finally { session.Writes.Release(); }
    }
}
