using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace LyricifyIsland;

// Linux Bluetooth socket ABI; vendor service UUIDs are selected before SDP discovery.
// Owns only the control socket. Closing it leaves the system audio connection intact.
internal sealed class HeadphoneTransport : IDisposable
{
    internal static readonly string[] ServiceUuids =
    ["00001107-d102-11e1-9b23-00025b00a5a5", "0000079a-d102-11e1-9b23-00025b00a5a5"];

    private readonly SafeFileHandle _socket;
    private HeadphoneTransport(SafeFileHandle socket) => _socket = socket;

    public static async Task<HeadphoneTransport> ConnectAsync(
        string address, HeadphoneConnectionMethod method, string? serviceUuid, CancellationToken cancellationToken)
    {
        var huawei = serviceUuid == HuaweiHeadphoneCatalog.ServiceUuid;
        // Channel 15 belongs to the OPPO protocol, including when saved as a global preference.
        if (huawei) method = HeadphoneConnectionMethod.Uuid;
        IReadOnlyList<string> serviceUuids = huawei ? [HuaweiHeadphoneCatalog.ServiceUuid]
            : serviceUuid is null ? ServiceUuids : new[] { serviceUuid }.Concat(ServiceUuids).Distinct().ToArray();
        var channels = new List<int>();
        if (method != HeadphoneConnectionMethod.Channel15)
        {
            try
            {
                using var discoveryTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                discoveryTimeout.CancelAfter(TimeSpan.FromSeconds(8));
                channels.AddRange(await DiscoverChannelsAsync(address, serviceUuids, discoveryTimeout.Token));
                Console.Error.WriteLine($"[headphones] SDP {address}: RFCOMM channels [{string.Join(", ", channels)}]");
            }
            catch (Exception exception) when (method == HeadphoneConnectionMethod.Automatic &&
                !cancellationToken.IsCancellationRequested &&
                exception is IOException or Win32Exception or OperationCanceledException)
            {
                Console.Error.WriteLine($"[headphones] SDP {address} failed: {DescribeError(exception)}");
                // Some supported firmwares omit SDP. Their documented alternate channel is 15.
            }
        }
        if (method != HeadphoneConnectionMethod.Uuid && !channels.Contains(15))
            channels.Add(15);
        if (channels.Count == 0)
            throw new IOException(huawei ? "耳机未提供控制服务，请确认已在系统蓝牙中连接"
                : "耳机未提供控制服务，可尝试兼容连接");

        Exception? primaryError = null;
        foreach (var channel in channels)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(8));
                return new HeadphoneTransport(await OpenSocketAsync(address, channel, false, timeout.Token));
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested &&
                exception is IOException or Win32Exception or OperationCanceledException)
            {
                primaryError ??= exception;
                Console.Error.WriteLine($"[headphones] RFCOMM {address} channel {channel} failed: {DescribeError(exception)}");
            }
        }
        var message = primaryError is Win32Exception { NativeErrorCode: 16 }
            ? "耳机控制通道正忙，请稍后重试或重新连接系统蓝牙"
            : $"无法打开耳机控制通道：{primaryError!.Message}";
        throw new IOException(message, primaryError);
    }

    private static async Task<SafeFileHandle> OpenSocketAsync(
        string address, int channel, bool sdp, CancellationToken cancellationToken)
    {
        var descriptor = socket(31, (sdp ? 5 : 1) | 0x800 | 0x80000, sdp ? 0 : 3);
        if (descriptor < 0) throw NativeError();
        var handle = new SafeFileHandle((nint)descriptor, ownsHandle: true);
        try
        {
            var mac = Convert.FromHexString(address.Replace(":", ""));
            Array.Reverse(mac);
            // sockaddr_rc: family, bdaddr, channel, padding; sockaddr_l2 adds PSM and CID.
            var endpoint = new byte[sdp ? 14 : 10];
            BinaryPrimitives.WriteUInt16LittleEndian(endpoint, 31);
            mac.CopyTo(endpoint, sdp ? 4 : 2);
            if (sdp) BinaryPrimitives.WriteUInt16LittleEndian(endpoint.AsSpan(2), 1);
            else endpoint[8] = checked((byte)channel);
            if (connect(handle, endpoint, (uint)endpoint.Length) < 0)
            {
                var error = Marshal.GetLastPInvokeError();
                if (error != 115) throw new Win32Exception(error);
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var descriptorPoll = new PollDescriptor { FileDescriptor = descriptor, Events = 4 };
                    var ready = poll(ref descriptorPoll, 1, 0);
                    if (ready < 0) throw NativeError();
                    if (ready > 0)
                    {
                        uint size = sizeof(int);
                        if (getsockopt(handle, 1, 4, out var socketError, ref size) < 0) throw NativeError();
                        if (socketError != 0) throw new Win32Exception(socketError);
                        break;
                    }
                    await Task.Delay(25, cancellationToken).ConfigureAwait(false);
                }
            }
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }

    public Task<int> ReadAsync(byte[] buffer, CancellationToken cancellationToken) =>
        ReadAsync(_socket, buffer, cancellationToken);

    private static async Task<int> ReadAsync(SafeFileHandle handle, byte[] buffer, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var length = recv(handle, buffer, (nuint)buffer.Length, 0);
            if (length >= 0) return checked((int)length);
            var error = Marshal.GetLastPInvokeError();
            if (error is not (11 or 4)) throw new Win32Exception(error);
            await Task.Delay(25, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task WriteAsync(byte[] buffer, CancellationToken cancellationToken) =>
        WriteAsync(_socket, buffer, cancellationToken);

    private static async Task WriteAsync(SafeFileHandle handle, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = offset == 0 ? buffer : buffer[offset..];
            var length = send(handle, remaining, (nuint)remaining.Length, 0x4000); // MSG_NOSIGNAL
            if (length > 0) { offset += checked((int)length); continue; }
            var error = Marshal.GetLastPInvokeError();
            if (length == 0) throw new IOException("耳机控制连接已关闭");
            if (error is not (11 or 4)) throw new Win32Exception(error);
            await Task.Delay(25, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<IReadOnlyList<int>> DiscoverChannelsAsync(string address, IReadOnlyList<string> serviceUuids, CancellationToken cancellationToken)
    {
        using var handle = await OpenSocketAsync(address, 0, true, cancellationToken);
        var channels = new List<int>();
        ushort transaction = 0;
        foreach (var uuid in serviceUuids)
        {
            byte[] continuation = [];
            var attributes = new List<byte>();
            do
            {
                // Bluetooth SDP ServiceSearchAttributeRequest, ProtocolDescriptorList attribute 0x0004.
                byte[] parameters = [0x35, 0x11, 0x1c, ..Convert.FromHexString(uuid.Replace("-", "")),
                    0xff, 0xff, 0x35, 0x03, 0x09, 0x00, 0x04, (byte)continuation.Length, ..continuation];
                var packet = new byte[5 + parameters.Length];
                packet[0] = 6;
                BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(1), ++transaction);
                BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(3), (ushort)parameters.Length);
                parameters.CopyTo(packet, 5);
                await WriteAsync(handle, packet, cancellationToken);
                var response = new byte[65535];
                var length = await ReadAsync(handle, response, cancellationToken);
                if (length < 8 || response[0] != 7 ||
                    BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(1)) != transaction)
                    throw new IOException("耳机返回了无效的蓝牙服务目录");
                var count = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(5));
                if (7 + count >= length || 8 + count + response[7 + count] != length)
                    throw new IOException("蓝牙服务目录数据不完整");
                attributes.AddRange(response.AsSpan(7, count).ToArray());
                continuation = response.AsSpan(8 + count, response[7 + count]).ToArray();
                if (attributes.Count > 65535) throw new IOException("蓝牙服务目录过大");
            } while (continuation.Length != 0);
            var index = 0;
            while (index < attributes.Count)
                CollectRfcommChannels(ReadElement(attributes.ToArray(), ref index, 0), channels);
        }
        return channels.Distinct().ToArray();
    }

    private sealed record SdpElement(int Type, byte[] Bytes, IReadOnlyList<SdpElement> Children);

    private static SdpElement ReadElement(byte[] data, ref int position, int depth)
    {
        if (position >= data.Length || depth > 16) throw new IOException("无效的蓝牙服务目录元素");
        var descriptor = data[position++];
        var type = descriptor >> 3;
        var sizeIndex = descriptor & 7;
        var count = type == 0 ? 0 : 1 << sizeIndex;
        if (sizeIndex >= 5)
        {
            var lengthBytes = 1 << (sizeIndex - 5);
            if (position + lengthBytes > data.Length) throw new IOException("蓝牙服务目录长度缺失");
            uint value = 0;
            for (var i = 0; i < lengthBytes; i++) value = (value << 8) | data[position++];
            if (value > data.Length) throw new IOException("蓝牙服务目录长度无效");
            count = (int)value;
        }
        var end = position + count;
        if (end > data.Length) throw new IOException("蓝牙服务目录被截断");
        var bytes = data[position..end];
        var children = new List<SdpElement>();
        if (type is 6 or 7)
        {
            while (position < end) children.Add(ReadElement(data, ref position, depth + 1));
            if (position != end) throw new IOException("蓝牙服务目录结构无效");
        }
        else position = end;
        return new SdpElement(type, bytes, children);
    }

    private static void CollectRfcommChannels(SdpElement element, List<int> channels)
    {
        if (element.Children.Count >= 2 && element.Children[0] is { Type: 3, Bytes: [0, 3] } &&
            element.Children[1] is { Type: 1, Bytes: [var channel] } && channel is >= 1 and <= 30)
            channels.Add(channel);
        foreach (var child in element.Children) CollectRfcommChannels(child, channels);
    }

    public void Dispose() => _socket.Dispose();
    private static string DescribeError(Exception exception) => exception is Win32Exception native
        ? $"errno {native.NativeErrorCode}: {native.Message}"
        : $"{exception.GetType().Name}: {exception.Message}";

    private static Win32Exception NativeError() => new(Marshal.GetLastPInvokeError());
    [StructLayout(LayoutKind.Sequential)]
    private struct PollDescriptor { public int FileDescriptor; public short Events; public short ReturnedEvents; }
    [DllImport("libc", SetLastError = true)] private static extern int socket(int family, int type, int protocol);
    [DllImport("libc", SetLastError = true)] private static extern int connect(SafeFileHandle socket, byte[] address, uint length);
    [DllImport("libc", SetLastError = true)] private static extern int poll(ref PollDescriptor descriptor, nuint count, int timeout);
    [DllImport("libc", SetLastError = true)] private static extern int getsockopt(SafeFileHandle socket, int level, int name, out int value, ref uint length);
    [DllImport("libc", SetLastError = true)] private static extern nint recv(SafeFileHandle socket, byte[] bytes, nuint length, int flags);
    [DllImport("libc", SetLastError = true)] private static extern nint send(SafeFileHandle socket, byte[] bytes, nuint length, int flags);
}
