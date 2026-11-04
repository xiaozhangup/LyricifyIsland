using System.Buffers.Binary;
using System.Text;

namespace LyricifyIsland;

// Huawei SPP: 5A 00, little-endian length (frame size minus five), service/command,
// one-byte type/length TLVs, then big-endian CRC-16/XMODEM over the preceding bytes.
internal sealed class HuaweiHeadphoneProtocol
{
    private readonly List<byte> _buffer = [];

    public static byte[] Encode(ushort command, ReadOnlySpan<byte> payload)
    {
        var frame = new byte[payload.Length + 8];
        frame[0] = 0x5a;
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(2), checked((ushort)(frame.Length - 5)));
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4), command);
        payload.CopyTo(frame.AsSpan(6));
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(frame.Length - 2), Crc(frame.AsSpan(0, frame.Length - 2)));
        return frame;
    }

    public IReadOnlyList<HeadphonePacket> Append(ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes) _buffer.Add(value);
        var packets = new List<HeadphonePacket>();
        while (_buffer.Count > 0)
        {
            var start = _buffer.IndexOf(0x5a);
            if (start < 0) { _buffer.Clear(); break; }
            if (start > 0) _buffer.RemoveRange(0, start);
            if (_buffer.Count < 4) break;
            var size = 5 + _buffer[2] + (_buffer[3] << 8);
            if (_buffer[1] != 0 || size < 8) { _buffer.RemoveAt(0); continue; }
            if (_buffer.Count < size) break;
            var frame = _buffer.GetRange(0, size).ToArray();
            if (Crc(frame.AsSpan(0, size - 2)) != BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(size - 2)))
            { _buffer.RemoveAt(0); continue; }
            _buffer.RemoveRange(0, size);
            var payload = frame[6..^2];
            if (ReadFields(payload) is not null)
                packets.Add(new HeadphonePacket(BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(4)), 0, payload));
        }
        return packets;
    }

    public static Dictionary<byte, byte[]>? ReadFields(ReadOnlySpan<byte> payload)
    {
        var fields = new Dictionary<byte, byte[]>();
        for (var offset = 0; offset < payload.Length;)
        {
            if (offset + 2 > payload.Length) return null;
            var type = payload[offset++];
            var length = payload[offset++];
            if (offset + length > payload.Length || !fields.TryAdd(type, payload.Slice(offset, length).ToArray()))
                return null;
            offset += length;
        }
        return fields;
    }

    private static ushort Crc(ReadOnlySpan<byte> data)
    {
        ushort crc = 0;
        foreach (var value in data)
        {
            crc ^= (ushort)(value << 8);
            for (var bit = 0; bit < 8; bit++)
                crc = (ushort)((crc << 1) ^ ((crc & 0x8000) != 0 ? 0x1021 : 0));
        }
        return crc;
    }

    // A write ACK can share a command with a query (notably spatial audio). Complete
    // pending reads only on a state-bearing response, never on a generic acknowledgement.
    public static bool IsQueryResponse(HeadphonePacket packet)
    {
        var fields = ReadFields(packet.Payload)!;
        return packet.Command switch
        {
            0x0107 => ReadModelId(fields) is not null || fields.TryGetValue(7, out var firmware) && firmware.Length > 0,
            0x0108 or 0x0127 => fields.TryGetValue(2, out var battery) && battery.Length >= 2,
            0x2b2a => fields.TryGetValue(1, out var anc) && anc.Length == 2,
            0x2b11 or 0x2b2f => Boolean(fields, 1) is not null,
            0x2b4a => fields.TryGetValue(2, out var equalizer) && equalizer.Length == 1,
            0x2bb4 => fields.TryGetValue(1, out var feature) && feature is [0x18] &&
                fields.TryGetValue(2, out var spatial) && spatial is [<= 2] &&
                fields.TryGetValue(3, out var scene) && scene.Length == 1,
            _ => false
        };
    }

    public static string? ReadModelId(Dictionary<byte, byte[]> fields)
    {
        if (!fields.TryGetValue(0x0a, out var model) || model.Length is < 8 or > 64 ||
            !fields.TryGetValue(0x0f, out var prefix) || prefix.Length != model.Length - 7 ||
            !fields.TryGetValue(0x02, out var code) || code.Length != 2 ||
            !fields.TryGetValue(0x19, out var color) || color.Length != 1)
            return null;
        if (!model.AsSpan(0, prefix.Length).SequenceEqual(prefix) || model[prefix.Length] != '-' ||
            prefix.Any(value => value is not (>= (byte)'A' and <= (byte)'Z' or >= (byte)'0' and <= (byte)'9')))
            return null;
        var id = Encoding.ASCII.GetString(model.AsSpan(model.Length - 6));
        if (!id.All(char.IsAsciiHexDigit) || id == "00000A" || !id.EndsWith(Convert.ToHexString(code), StringComparison.Ordinal))
            return null;
        return id;
    }

    public static HeadphoneState ReadState(HeadphoneState state, HeadphoneProfile profile, ushort command,
        Dictionary<byte, byte[]> fields)
    {
        switch (command)
        {
            case 0x0108 or 0x0127 when fields.TryGetValue(2, out var levels) && levels.Length >= 2:
                fields.TryGetValue(3, out var charging);
                fields.TryGetValue(5, out var inCase);
                HeadphoneBattery? Battery(int index) => index < levels.Length && levels[index] <= 100
                    ? new HeadphoneBattery(levels[index], charging is not null && index < charging.Length && charging[index] != 0)
                    : null;
                state = state with
                {
                    LeftBattery = Battery(0), RightBattery = Battery(1),
                    CaseBattery = profile.Capabilities.HasChargingCase ? Battery(2) : null
                };
                // Only Pro 5 captures establish 01/08 field 05 as the in-case flags.
                if (profile.Huawei!.ModelId == "00016D" && inCase is { Length: >= 2 })
                    state = state with
                    {
                        LeftWearing = inCase[0] == 1 ? "在盒中" : null,
                        RightWearing = inCase[1] == 1 ? "在盒中" : null
                    };
                break;
            case 0x2b2a when profile.Capabilities.CanReadNoiseMode && fields.TryGetValue(1, out var anc) && anc.Length == 2:
                var mode = anc[1] switch
                {
                    0 => HeadphoneNoiseMode.Off, 1 => HeadphoneNoiseMode.NoiseCancellation,
                    2 => HeadphoneNoiseMode.Transparency, _ => (HeadphoneNoiseMode?)null
                };
                if (mode is null || !profile.ModeIndices.ContainsKey(mode.Value)) break;
                HeadphoneNoiseLevel? level = null;
                if (mode == HeadphoneNoiseMode.NoiseCancellation && profile.LevelIndices.Count > 0)
                {
                    foreach (var entry in profile.LevelIndices)
                        if (entry.Value == anc[0]) level = entry.Key;
                    if (level is null) break;
                }
                state = state with { NoiseMode = mode, NoiseLevel = level };
                break;
            case 0x2b11 when profile.Capabilities.HasWearDetection:
                state = state with { WearDetection = Boolean(fields, 1) ?? state.WearDetection };
                break;
            case 0x2b2f when profile.Capabilities.HasDualDevice:
                state = state with { DualDevice = Boolean(fields, 1) ?? state.DualDevice };
                break;
            case 0x2b4a when fields.TryGetValue(2, out var equalizer) && equalizer.Length == 1:
                state = state with { EqualizerId = equalizer[0] };
                break;
            case 0x2bb4 when profile.Capabilities.SpatialModes.Count > 0 &&
                fields.TryGetValue(1, out var feature) && feature is [0x18] &&
                fields.TryGetValue(2, out var spatial) && spatial is [<= 2] &&
                fields.TryGetValue(3, out var scene) && scene.Length == 1:
                state = state with { SpatialMode = profile.Huawei!.ReversedSpatialModes && spatial[0] != 0 ? 3 - spatial[0] : spatial[0] };
                break;
        }
        return state;
    }

    private static bool? Boolean(Dictionary<byte, byte[]> fields, byte type) =>
        fields.TryGetValue(type, out var value) && value is [<= 1] ? value[0] != 0 : null;

    public static byte[] NoisePayload(HeadphoneProfile profile, HeadphoneNoiseMode mode, HeadphoneNoiseLevel? level)
    {
        var modeIndex = (byte)profile.ModeIndices[mode];
        if (profile.Huawei!.LegacyAnc) return [1, 1, modeIndex];
        var subMode = mode switch
        {
            HeadphoneNoiseMode.Off => 0,
            HeadphoneNoiseMode.Transparency => profile.Huawei.TransparencySubMode,
            _ => level is { } selected ? profile.LevelIndices[selected]
                : profile.LevelIndices.Values.FirstOrDefault(0xff)
        };
        return [1, 2, modeIndex, (byte)subMode];
    }

    public static byte[] EqualizerPayload(int id) => id == 0xc9
        ? [1, 1, 0xc9, 2, 1, 10, 5, 1, 1, 3, 10, 0xfb, 20, 30, 10, 0, 0, 0xe7, 0xf6, 10, 0, 4, 3, 0x32, 0x30, 0x31]
        : [1, 1, (byte)id];
}
