namespace LyricifyIsland;

internal enum HeadphoneConnectionMethod { Automatic, Uuid, Channel15 }
internal enum HeadphoneNoiseMode { Off, NoiseCancellation, Transparency, Adaptive }
internal enum HeadphoneNoiseLevel { Smart, Deep, Moderate, Light }

internal sealed record HeadphoneDevice(string Address, string Name, bool IsConnected, string ObjectPath)
{
    public bool ServicesResolved { get; init; }
    public bool HasHuaweiSerialService { get; init; }
    public override string ToString() => Name;
}

internal sealed record HeadphoneBattery(int Level, bool IsCharging);
internal sealed record HeadphoneEqualizer(int Id, string Name)
{
    public override string ToString() => Name;
}

internal sealed record HeadphoneCapabilities
{
    public bool HasBattery { get; init; } = true;
    public bool HasChargingCase { get; init; } = true;
    public bool IsEyewear { get; init; }
    public bool CanReadNoiseMode { get; init; } = true;
    public bool CanReadGameMode { get; init; } = true;
    public IReadOnlyList<HeadphoneNoiseMode> NoiseModes { get; init; } = [];
    public IReadOnlyList<HeadphoneNoiseLevel> NoiseLevels { get; init; } = [];
    public IReadOnlyList<int> SpatialModes { get; init; } = [];
    public bool HasGameMode { get; init; }
    public bool HasSpatialSound { get; init; }
    public bool HasGameSound { get; init; }
    public int GameSoundType { get; init; }
    public bool GameSoundMutexSpatial { get; init; }
    public bool GameSoundMutexEqualizer { get; init; }
    public int MultiDeviceVersion { get; init; }
    public bool HasMultiDeviceSwitch => HasDualDevice && MultiDeviceVersion > 0;
    public bool CanSetDevicePriority { get; init; }
    public bool SupportsRfcomm { get; init; } = true;
    public bool HasDualDevice { get; init; }
    public bool HasWearDetection { get; init; }
    public IReadOnlyList<HeadphoneEqualizer> Equalizers { get; init; } = [];
}

internal sealed record HeadphoneConnectedDevice(
    string Address, string Name, int ConnectionState, bool IsAudioActive, bool IsCurrentDevice);

internal sealed record HeadphoneState
{
    public bool IsConnected { get; init; }
    public string? ProductId { get; init; }
    public string? ModelName { get; init; }
    public HeadphoneCapabilities Capabilities { get; init; } = new();
    public HeadphoneBattery? LeftBattery { get; init; }
    public HeadphoneBattery? RightBattery { get; init; }
    public HeadphoneBattery? CaseBattery { get; init; }
    public HeadphoneNoiseMode? NoiseMode { get; init; }
    public HeadphoneNoiseLevel? NoiseLevel { get; init; }
    public HeadphoneNoiseLevel? RealtimeNoiseLevel { get; init; }
    public bool? GameMode { get; init; }
    public bool? GameSound { get; init; }
    public string? FirmwareVersion { get; init; }
    public string? CodecName { get; init; }
    public string? LeftWearing { get; init; }
    public string? RightWearing { get; init; }
    public IReadOnlyList<HeadphoneConnectedDevice> ConnectedDevices { get; init; } = [];
    public int? SpatialMode { get; init; }
    public bool? DualDevice { get; init; }
    public bool? WearDetection { get; init; }
    public int? EqualizerId { get; init; }
}
