using System.Reflection;
using System.Text.Json;

namespace LyricifyIsland;

// Protocol indices and model metadata: OppoPodsManager, see THIRD_PARTY_NOTICES.md.
internal sealed record HeadphoneProfile
{
    public required string ProductId { get; init; }
    public required string Name { get; init; }
    public string? ServiceUuid { get; init; }
    public required IReadOnlyDictionary<HeadphoneNoiseMode, int> ModeIndices { get; init; }
    public required IReadOnlyDictionary<HeadphoneNoiseLevel, int> LevelIndices { get; init; }
    public required IReadOnlyDictionary<int, (HeadphoneNoiseMode Mode, HeadphoneNoiseLevel? Level)> AncReports { get; init; }
    public required HeadphoneCapabilities Capabilities { get; init; }
    public int GameFeature { get; init; } = 0x06;
    public HuaweiHeadphoneModel? Huawei { get; init; }
}

internal static class HeadphoneCatalog
{
    private static readonly HeadphoneProfile[] Profiles = Load();

    public static HeadphoneProfile? Find(string? productId, string? name)
    {
        if (productId is not null && Profiles.FirstOrDefault(p =>
                p.ProductId.Equals(productId, StringComparison.OrdinalIgnoreCase)) is { } identified)
            return identified;
        if (name is null)
            return null;
        if (HuaweiHeadphoneCatalog.FindByName(name) is { } huawei)
            return huawei;
        var normalized = Normalize(name);
        return Profiles.FirstOrDefault(p => Normalize(p.Name) == normalized);
    }

    private static string Normalize(string name) =>
        string.Concat(name.Where(char.IsLetterOrDigit)).ToUpperInvariant();

    private static HeadphoneProfile[] Load()
    {
        using var models = ReadResource("DeviceModels");
        using var equalizers = ReadResource("EqModeNames");
        var names = equalizers.RootElement.GetProperty("mapping");
        // A few aliases share a product ID; prefer the entry that describes its controls.
        return models.RootElement.GetProperty("whiteList").EnumerateArray()
            .OrderByDescending(entry => entry.TryGetProperty("function", out _))
            .Select(entry => ReadProfile(entry, names)).ToArray();
    }

    private static JsonDocument ReadResource(string name)
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream($"LyricifyIsland.Headphones.{name}.json")!;
        return JsonDocument.Parse(stream);
    }

    private static HeadphoneProfile ReadProfile(JsonElement entry, JsonElement equalizerNames)
    {
        var modes = new Dictionary<HeadphoneNoiseMode, int>();
        var levels = new Dictionary<HeadphoneNoiseLevel, int>();
        var reports = new Dictionary<int, (HeadphoneNoiseMode, HeadphoneNoiseLevel?)>();
        var equalizers = new Dictionary<int, HeadphoneEqualizer>();
        var hasFunction = entry.TryGetProperty("function", out var function);
        bool Flag(string name) => hasFunction && function.TryGetProperty(name, out var value)
            && (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.Number && value.GetInt32() > 0);
        JsonElement[] Items(string name) => hasFunction && function.TryGetProperty(name, out var value)
            ? value.EnumerateArray().ToArray() : [];

        foreach (var option in Items("noiseReductionMode"))
        {
            var modeType = option.GetProperty("modeType").GetInt32();
            var (mode, level) = NoiseOption(modeType);
            var index = option.GetProperty("protocolIndex").GetInt32();
            reports[index] = (mode, level);
            var children = option.TryGetProperty("childrenMode", out var nested)
                ? nested.EnumerateArray().ToArray() : [];
            foreach (var child in children)
            {
                var childType = child.GetProperty("modeType").GetInt32();
                var (childMode, childLevel) = NoiseOption(childType);
                var childIndex = child.GetProperty("protocolIndex").GetInt32();
                reports[childIndex] = (childLevel is null ? mode : childMode, childLevel);
                if (childLevel is { } intensity)
                    levels.TryAdd(intensity, childIndex);
            }
            if (level is { } ownLevel)
                levels.TryAdd(ownLevel, index);
            // Main modes use the parent index; choosing an intensity sends its child index.
            // This preserves the earbud's intensity when returning to noise cancellation.
            modes.TryAdd(mode, index);
        }
        foreach (var key in new[] { "equalizerMode", "equalizerModeCompat", "equalizerModeByVersion" })
        foreach (var equalizer in Items(key))
        {
            var index = equalizer.GetProperty("protocolIndex").GetInt32();
            var type = equalizer.GetProperty("modeType").GetInt32().ToString();
            var name = equalizerNames.TryGetProperty(type, out var label) ? label.GetString()! : $"预设 {index + 1}";
            equalizers.TryAdd(index, new HeadphoneEqualizer(index, name));
        }
        var gameSoundType = Items("gameSoundList").Select(item => item.GetProperty("type").GetInt32())
            .FirstOrDefault(type => type != 0);
        var gameModes = Items("gameModeList");
        var hasGameMode = gameSoundType != 0 || (gameModes.Length > 0
            ? gameModes.Any(item => item.GetProperty("gameMode").GetInt32() == 1) : Flag("gameMode"));
        var multiDeviceVersion = hasFunction && function.TryGetProperty("multiDevicesConnect", out var multi)
            ? multi.GetInt32() : 0;
        var multiFunctions = Items("multiConnectFunctions");
        if (multiFunctions.Length > 0)
            multiDeviceVersion = Math.Max(multiDeviceVersion,
                multiFunctions.Any(item => item.GetProperty("functionType").GetString() == "unpairDevice") ? 2 : 1);
        var mutexes = Items("gameSoundMutexes").Select(item => item.GetInt32()).ToArray();
        var spatialTypes = Items("spatialTypes").Select(item => item.GetInt32()).Distinct().Order().ToArray();
        return new HeadphoneProfile
        {
            ProductId = entry.GetProperty("id").GetString()!,
            Name = entry.GetProperty("name").GetString()!,
            ServiceUuid = entry.TryGetProperty("uuid", out var uuid) ? uuid.GetString() : null,
            ModeIndices = modes,
            LevelIndices = levels,
            AncReports = reports,
            GameFeature = gameSoundType != 0 ? 0x28 : 0x06,
            Capabilities = new HeadphoneCapabilities
            {
                SupportsRfcomm = !entry.TryGetProperty("supportSpp", out var spp) || spp.GetBoolean(),
                NoiseModes = modes.Keys.OrderBy(mode => mode).ToArray(),
                NoiseLevels = levels.Keys.OrderBy(level => level).ToArray(),
                SpatialModes = spatialTypes,
                HasSpatialSound = spatialTypes.Length > 0 && !spatialTypes.Contains(2),
                HasGameMode = hasGameMode,
                HasGameSound = gameSoundType != 0,
                GameSoundType = gameSoundType,
                GameSoundMutexSpatial = mutexes.Contains(2),
                GameSoundMutexEqualizer = mutexes.Contains(1) || mutexes.Contains(3),
                HasDualDevice = multiDeviceVersion > 0,
                MultiDeviceVersion = multiDeviceVersion,
                HasWearDetection = Flag("wearDetection"),
                Equalizers = equalizers.Values.OrderBy(preset => preset.Id).ToArray()
            }
        };
    }

    private static (HeadphoneNoiseMode Mode, HeadphoneNoiseLevel? Level) NoiseOption(int type) => type switch
    {
        1 => (HeadphoneNoiseMode.Off, null),
        2 => (HeadphoneNoiseMode.Transparency, null),
        3 => (HeadphoneNoiseMode.NoiseCancellation, HeadphoneNoiseLevel.Light),
        4 => (HeadphoneNoiseMode.NoiseCancellation, HeadphoneNoiseLevel.Deep),
        5 => (HeadphoneNoiseMode.NoiseCancellation, null),
        6 or 10 => (HeadphoneNoiseMode.Adaptive, null),
        7 => (HeadphoneNoiseMode.NoiseCancellation, HeadphoneNoiseLevel.Smart),
        8 => (HeadphoneNoiseMode.NoiseCancellation, HeadphoneNoiseLevel.Moderate),
        _ => throw new InvalidDataException($"Unknown headphone noise mode: {type}")
    };
}
