namespace LyricifyIsland;

// Model IDs, capabilities and wire values checked against Nshpiter/HuaweiPods.
// See THIRD_PARTY_NOTICES.md for the pinned protocol reference.
internal sealed record HuaweiHeadphoneModel(string? ModelId, bool LegacyAnc, byte TransparencySubMode,
    bool ReversedSpatialModes);

internal static class HuaweiHeadphoneCatalog
{
    public const string ServiceUuid = "00001101-0000-1000-8000-00805f9b34fb";

    private static readonly (HeadphoneNoiseLevel, int)[] StandardLevels =
    [(HeadphoneNoiseLevel.Smart, 3), (HeadphoneNoiseLevel.Light, 1),
        (HeadphoneNoiseLevel.Moderate, 0), (HeadphoneNoiseLevel.Deep, 2)];
    private static readonly (HeadphoneNoiseLevel, int)[] Pro3Levels =
    [(HeadphoneNoiseLevel.Smart, 1), (HeadphoneNoiseLevel.Light, 0),
        (HeadphoneNoiseLevel.Moderate, 2), (HeadphoneNoiseLevel.Deep, 3)];

    public static IReadOnlyList<HeadphoneProfile> Profiles { get; } =
    [
        Model("freebuds3", "FreeBuds 3", anc: true, legacyAnc: true, ancReadback: false),
        Model("freebuds4e", "FreeBuds 4E", "000135", anc: true,
            levels: [(HeadphoneNoiseLevel.Light, 1), (HeadphoneNoiseLevel.Moderate, 0)],
            wear: true, equalizers: [1, 2, 3]),
        Model("freebuds5", "FreeBuds 5", "000141", anc: true, levels: StandardLevels[..3],
            wear: true, lowLatency: true, equalizers: [1, 2, 3, 9]),
        Model("freebuds5i", "FreeBuds 5i", "000145", anc: true, transparency: true,
            levels: StandardLevels, wear: true, lowLatency: true, equalizers: [1, 2, 3, 9]),
        Model("freebudsse4anc", "FreeBuds SE 4 ANC", "000169", anc: true, transparency: true,
            levels: StandardLevels[1..], lowLatency: true, equalizers: [1, 2, 3, 9]),
        Model("freebuds6i", "FreeBuds 6i", "000153", anc: true, transparency: true,
            levels: StandardLevels, transparencySubMode: 2, wear: true, lowLatency: true,
            equalizers: [1, 2, 3, 9]),
        Model("freebudspro3", "FreeBuds Pro 3", "000149", anc: true, transparency: true,
            levels: Pro3Levels, wear: true, lowLatency: true),
        Model("freebudspro4", "FreeBuds Pro 4", anc: true, ancReadback: false),
        Model("freebudspro5", "FreeBuds Pro 5", "00016D", anc: true, transparency: true,
            levels: StandardLevels, transparencySubMode: 2, wear: true, lowLatency: true,
            spatial: true, reversedSpatial: true, dual: true, equalizers: [5, 9, 2, 0xc9, 13, 15, 14, 16, 17]),
        Model("freebuds7i", "FreeBuds 7i", "000163", anc: true, transparency: true,
            levels: Pro3Levels, wear: true, lowLatency: true, spatial: true, equalizers: [1, 2, 3, 9]),
        Model("freeclip", "FreeClip"),
        Model("freeclip2", "FreeClip 2", "000167", wear: true, lowLatency: true,
            spatial: true, reversedSpatial: true, equalizers: [1, 10, 3, 9]),
        Model("freearc", "FreeArc", "00015D", equalizers: [1, 10, 2, 3, 9]),
        Model("eyewear", "Eyewear", eyewear: true),
        Model("eyewear2", "Eyewear 2", "00014F", eyewear: true, lowLatency: true),
        Model("eyewear3", "Eyewear 3", "000139", eyewear: true)
    ];

    public static HeadphoneProfile Unknown { get; } = Model("unknown", "耳机（型号未识别）");

    public static HeadphoneProfile? FindById(string id) =>
        Profiles.FirstOrDefault(p => p.ProductId.Equals(id, StringComparison.OrdinalIgnoreCase));
    public static HeadphoneProfile? FindByModelId(string id) =>
        Profiles.FirstOrDefault(p => string.Equals(p.Huawei!.ModelId, id, StringComparison.OrdinalIgnoreCase));

    public static HeadphoneProfile? FindByName(string? name, bool exactOnly = false)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var normalized = Normalize(name);
        if (normalized.Length == 0) return null;
        var stripped = normalized;
        if (stripped.StartsWith("HUAWEI", StringComparison.Ordinal)) stripped = stripped[6..];
        if (stripped.StartsWith("HONOR", StringComparison.Ordinal)) stripped = stripped[5..];
        if (stripped.StartsWith("华为", StringComparison.Ordinal)) stripped = stripped[2..];
        if (stripped.StartsWith("荣耀", StringComparison.Ordinal)) stripped = stripped[2..];
        var exact = Profiles.FirstOrDefault(p =>
            p.ProductId.AsSpan(7).Equals(stripped, StringComparison.OrdinalIgnoreCase));
        if (exact is not null || exactOnly) return exact;

        foreach (var profile in Profiles.OrderByDescending(p => p.ProductId.Length))
        {
            var key = profile.ProductId.AsSpan(7);
            // Eyewear requires an explicit brand or Chinese eyewear keyword when matching as a substring.
            if (key.StartsWith("eyewear", StringComparison.OrdinalIgnoreCase) &&
                !name.Contains("智能眼镜", StringComparison.Ordinal) &&
                !normalized.Contains("HUAWEI", StringComparison.Ordinal) &&
                !normalized.Contains("华为", StringComparison.Ordinal))
                continue;
            if ( HeadphoneCatalog.ContainsModelKey(normalized, key))
                return profile;
        }
        return null;
    }

    public static bool HasBrandKeyword(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var normalized = Normalize(name);
        return normalized.Contains("HUAWEI", StringComparison.Ordinal) ||
            normalized.Contains("华为", StringComparison.Ordinal) ||
            normalized.Contains("FREEBUDS", StringComparison.Ordinal) ||
            normalized.Contains("FREECLIP", StringComparison.Ordinal) ||
            normalized.Contains("FREEARC", StringComparison.Ordinal) ||
            normalized.Contains("FREELACE", StringComparison.Ordinal) ||
            name.Contains("智能眼镜", StringComparison.Ordinal);
    }

    private static string Normalize(string name) =>
        string.Concat(name.Replace("智能眼镜", "EYEWEAR", StringComparison.Ordinal)
            .Where(c => char.IsLetterOrDigit(c) || c == '+')).ToUpperInvariant();

    private static HeadphoneProfile Model(string id, string name, string? modelId = null,
        bool anc = false, bool transparency = false, (HeadphoneNoiseLevel Level, int Index)[]? levels = null,
        bool ancReadback = true, bool legacyAnc = false, byte transparencySubMode = 0xff,
        bool wear = false, bool lowLatency = false, bool spatial = false, bool reversedSpatial = false,
        bool dual = false, int[]? equalizers = null, bool eyewear = false)
    {
        var modes = new Dictionary<HeadphoneNoiseMode, int>();
        if (anc)
        {
            modes.Add(HeadphoneNoiseMode.Off, 0);
            modes.Add(HeadphoneNoiseMode.NoiseCancellation, 1);
            if (transparency) modes.Add(HeadphoneNoiseMode.Transparency, 2);
        }
        var levelIndices = (levels ?? []).ToDictionary(level => level.Level, level => level.Index);
        return new HeadphoneProfile
        {
            ProductId = $"huawei.{id}", Name = name, ServiceUuid = ServiceUuid,
            Huawei = new HuaweiHeadphoneModel(modelId, legacyAnc, transparencySubMode, reversedSpatial),
            ModeIndices = modes, LevelIndices = levelIndices, AncReports = new Dictionary<int, (HeadphoneNoiseMode, HeadphoneNoiseLevel?)>(),
            Capabilities = new HeadphoneCapabilities
            {
                HasBattery = !legacyAnc, HasChargingCase = !eyewear, IsEyewear = eyewear,
                NoiseModes = modes.Keys.ToArray(), NoiseLevels = levelIndices.Keys.ToArray(),
                CanReadNoiseMode = ancReadback, HasWearDetection = wear,
                HasGameMode = lowLatency, CanReadGameMode = false, HasDualDevice = dual,
                SpatialModes = spatial ? [0, 1, 2] : [],
                Equalizers = (equalizers ?? []).Select(value => new HeadphoneEqualizer(value, value switch
                {
                    1 => "默认", 2 => "低音增强", 3 => "高音增强", 5 => "悦彰均衡", 9 => "人声清晰",
                    10 => "运动激励", 13 => "电影", 14 => "游戏", 15 => "播客人声", 16 => "运动",
                    17 => "AI 音效", 0xc9 => "悦彰古典", _ => $"音效 {value}"
                })).ToArray()
            }
        };
    }
}
