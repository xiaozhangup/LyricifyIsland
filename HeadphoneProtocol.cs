using System.Buffers.Binary;
using System.Text;

namespace LyricifyIsland;

// Protocol facts: Leaf-lsgtky/OppoPods and Zhaoyi-ya/OppoPodsManager.
// LinkLen is a 7-bit little-endian varint, including the seven-byte inner header.
internal readonly record struct HeadphonePacket(ushort Command, byte Sequence, byte[] Payload);

internal sealed class HeadphoneProtocol
{
    private readonly List<byte> _buffer = [];

    public static byte[] Encode(ushort command, byte sequence, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(payload));
        var linkLength = 7 + payload.Length;
        var lengthBytes = linkLength < 128 ? 1 : linkLength < 16384 ? 2 : 3;
        var result = new byte[1 + lengthBytes + linkLength];
        result[0] = 0xaa;
        var position = 1;
        var remaining = linkLength;
        do
        {
            var part = (byte)(remaining & 0x7f);
            remaining >>= 7;
            result[position++] = remaining == 0 ? part : (byte)(part | 0x80);
        } while (remaining != 0);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(position + 2), command);
        result[position + 4] = sequence;
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(position + 5), (ushort)payload.Length);
        payload.CopyTo(result.AsSpan(position + 7));
        return result;
    }

    public IReadOnlyList<HeadphonePacket> Append(ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes) _buffer.Add(value);
        var packets = new List<HeadphonePacket>();
        while (_buffer.Count != 0)
        {
            var start = _buffer.IndexOf(0xaa);
            if (start < 0) { _buffer.Clear(); break; }
            if (start > 0) _buffer.RemoveRange(0, start);
            var position = 1;
            var length = 0;
            var complete = false;
            while (position < _buffer.Count && position <= 3)
            {
                var part = _buffer[position];
                length |= (part & 0x7f) << (7 * (position - 1));
                position++;
                if ((part & 0x80) == 0) { complete = true; break; }
            }
            if (!complete)
            {
                if (position <= 3) break;
                _buffer.RemoveAt(0);
                continue;
            }
            if (length < 7 || length > ushort.MaxValue + 7)
            { _buffer.RemoveAt(0); continue; }
            if (_buffer.Count < position + length) break;
            var body = _buffer.GetRange(position, length).ToArray();
            _buffer.RemoveRange(0, position + length);
            if ((body[0] & 3) != 0)
                throw new IOException("耳机返回了暂不支持的链路分片");
            var payloadLength = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(5));
            if (payloadLength != length - 7) throw new IOException("耳机数据长度校验失败");
            packets.Add(new HeadphonePacket(BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(2)),
                body[4], body[7..]));
        }
        return packets;
    }

    public static byte[] AncPayload(int index)
    {
        var payload = new byte[3 + index / 8];
        payload[0] = payload[1] = 1;
        payload[2 + index / 8] = (byte)(1 << (index % 8));
        return payload;
    }

    public static HeadphoneState ReadBattery(HeadphoneState state, ReadOnlySpan<byte> payload)
    {
        if (payload.Length >= 8 && payload[0] == 0 && payload[1] == 4)
            return state with
            {
                LeftBattery = Battery(payload[2], payload[3] != 0),
                RightBattery = Battery(payload[4], payload[5] != 0),
                CaseBattery = Battery(payload[6], payload[7] != 0)
            };
        if ((payload.Length & 1) != 0) return state;
        for (var i = 0; i < payload.Length; i += 2)
        {
            var battery = Battery(payload[i + 1] & 0x7f, (payload[i + 1] & 0x80) != 0);
            state = payload[i] switch
            {
                1 => state with { LeftBattery = battery },
                2 => state with { RightBattery = battery },
                3 => state with { CaseBattery = battery },
                _ => state
            };
        }
        return state;
    }

    private static HeadphoneBattery? Battery(int value, bool charging) =>
        value <= 100 ? new HeadphoneBattery(value, charging) : null;

    public static Dictionary<byte, bool>? ReadFeatures(ReadOnlySpan<byte> payload)
    {
        if (payload.Length >= 2 && payload[0] == 0 && payload.Length == 2 + payload[1] * 2)
            payload = payload[2..];
        if ((payload.Length & 1) != 0) return null;
        var result = new Dictionary<byte, bool>();
        for (var i = 0; i < payload.Length; i += 2)
            if (payload[i + 1] <= 1) result[payload[i]] = payload[i + 1] == 1;
        return result;
    }

    public static HeadphoneState ReadAnc(HeadphoneState state, ReadOnlySpan<byte> payload, HeadphoneProfile? profile)
    {
        if (profile is null) return state;
        for (var i = 0; i + 2 < payload.Length; i++)
        {
            if (payload[i] != 1 || payload[i + 1] != 1) continue;
            var bitmap = payload[i + 2] | (i + 3 < payload.Length ? payload[i + 3] << 8 : 0);
            foreach (var (index, value) in profile.AncReports)
                if ((bitmap & (1 << index)) != 0)
                    return state with { NoiseMode = value.Mode, NoiseLevel = value.Level,
                        RealtimeNoiseLevel = value.Level == HeadphoneNoiseLevel.Smart ? state.RealtimeNoiseLevel : null };
        }
        return state;
    }

    public static (IReadOnlyList<HeadphoneEqualizer> Entries, int? Selected)? ReadEqualizers(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 2 || payload[0] != 0) return null;
        var entries = new List<HeadphoneEqualizer>();
        int? selected = null;
        var position = 2;
        for (var i = 0; i < payload[1]; i++)
        {
            if (position + 5 > payload.Length) return null;
            var active = payload[position] != 0;
            var id = payload[position + 3];
            var nameLength = payload[position + 4];
            position += 5;
            if (position + nameLength >= payload.Length) return null;
            var name = Encoding.UTF8.GetString(payload.Slice(position, nameLength)).Trim();
            position += nameLength;
            var bands = payload[position++];
            if (position + bands * 3 > payload.Length) return null;
            position += bands * 3;
            entries.Add(new HeadphoneEqualizer(id, name.Length == 0 ? $"均衡器 {id}" : name));
            if (active) selected = id;
        }
        return (entries, selected);
    }

    public static IReadOnlyList<HeadphoneConnectedDevice>? ReadConnectedDevices(ReadOnlySpan<byte> payload, bool notification)
    {
        if (payload.Length < 2 || payload[0] != (notification ? 6 : 0)) return null;
        var devices = new List<HeadphoneConnectedDevice>();
        var position = 2;
        for (var i = 0; i < payload[1]; i++)
        {
            if (position + 10 > payload.Length) return null;
            var address = payload.Slice(position, 6).ToArray();
            Array.Reverse(address);
            var connection = payload[position + 7];
            var flags = payload[position + 8];
            var nameLength = payload[position + 9];
            position += 10;
            if (position + nameLength > payload.Length) return null;
            var name = Encoding.UTF8.GetString(payload.Slice(position, nameLength)).Trim();
            position += nameLength;
            var formatted = string.Join(":", address.Select(part => part.ToString("X2")));
            devices.Add(new HeadphoneConnectedDevice(formatted, name.Length == 0 ? formatted : name,
                connection, (flags & 4) != 0, (flags & 1) != 0));
        }
        return devices;
    }
}
