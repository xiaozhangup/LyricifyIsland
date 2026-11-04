using System.Buffers.Binary;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace LyricifyIsland;

internal sealed class PngClipboardBitmap(byte[] png) : Bitmap(new EncodedPng(png))
{
    private sealed class EncodedPng(byte[] png) : IBitmapImpl
    {
        public Vector Dpi => new(96, 96);
        public PixelSize PixelSize => new(
            BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4)),
            BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4)));
        public int Version => 1;

        // Avalonia's Wayland clipboard writes to an asynchronous pipe. Its Skia
        // encoder uses synchronous writes, so send the already encoded PNG instead.
        public void Save(Stream stream, BitmapEncoderOptions options) =>
            stream.WriteAsync(png.AsMemory()).AsTask().GetAwaiter().GetResult();

        public void Dispose() { }
    }
}
