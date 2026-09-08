using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Collections.Immutable;
using SkiaSharp;

namespace LyricifyIsland;

internal static class TrackCache
{
    internal static ImmutableArray<byte> PreferLargerImage(ImmutableArray<byte> current, ImmutableArray<byte> incoming)
    {
        static long Area(ImmutableArray<byte> bytes)
        {
            if (bytes.IsDefaultOrEmpty)
                return 0;
            using var data = SKData.CreateCopy(bytes.AsSpan());
            using var codec = SKCodec.Create(data);
            return codec is null ? 0 : (long)codec.Info.Width * codec.Info.Height;
        }
        var incomingArea = Area(incoming);
        return incomingArea > 0 && incomingArea >= Area(current) ? incoming : current;
    }

    public static TrackInfo? Load(string id)
    {
        try
        {
            var path = PathFor(id);
            var cached = File.Exists(path)
                ? JsonSerializer.Deserialize<TrackInfo>(File.ReadAllBytes(path))
                : null;
            return cached?.Id == id ? cached : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            Console.Error.WriteLine($"[cache] read failed: {exception.Message}");
            return null;
        }
    }

    public static void SaveIfChanged(TrackInfo track)
    {
        var path = PathFor(track.Id);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(track);
        var directory = Path.GetDirectoryName(path)!;
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
                return;

            Directory.CreateDirectory(directory);
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[cache] write failed: {exception.Message}");
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    public static long Size()
    {
        try
        {
            var directory = CacheDirectory();
            return Directory.Exists(directory)
                ? Directory.EnumerateFiles(directory).Sum(path => new FileInfo(path).Length)
                : 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[cache] size failed: {exception.Message}");
            return 0;
        }
    }

    public static bool Clear()
    {
        try
        {
            var directory = CacheDirectory();
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[cache] clear failed: {exception.Message}");
            return false;
        }
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024d:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024d * 1024):0.#} MB",
        _ => $"{bytes / (1024d * 1024 * 1024):0.#} GB"
    };

    private static string PathFor(string id) => Path.Combine(
        CacheDirectory(),
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id))) + ".json");

    private static string CacheDirectory()
    {
        var cacheHome = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        if (string.IsNullOrWhiteSpace(cacheHome) || !Path.IsPathRooted(cacheHome))
            cacheHome = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache");
        return Path.Combine(cacheHome, "lyricify-island", "tracks");
    }
}
