namespace Nama.Core.Abstractions;

/// <summary>An image fetched into memory, with the format determined from its content.</summary>
/// <param name="Bytes">Raw file contents.</param>
/// <param name="Extension">File extension including the dot, e.g. ".png".</param>
public sealed record DownloadedImage(byte[] Bytes, string Extension);

/// <summary>
/// Fetches artwork. Kept as an abstraction so the Steam layer, which decides where images
/// go on disk, does not have to know anything about HTTP.
/// </summary>
public interface IImageDownloader
{
    /// <summary>Downloads an image, or returns null if it cannot be fetched or is not an image.</summary>
    Task<DownloadedImage?> DownloadAsync(string url, CancellationToken ct = default);
}

/// <summary>
/// Identifies image formats from their leading bytes.
/// <para>
/// The URL's extension is not trustworthy: providers serve <c>.jpg</c> URLs returning PNG
/// data, and Steam decides how to render artwork from the file extension on disk. Getting
/// this wrong produces a file Steam silently refuses to display.
/// </para>
/// </summary>
public static class ImageFormat
{
    /// <summary>Returns the extension for the given content, or null if it is not a known image.</summary>
    public static string? Detect(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 12) return null;

        if (bytes is [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, ..]) return ".png";
        if (bytes is [0xFF, 0xD8, 0xFF, ..]) return ".jpg";
        if (bytes is [(byte)'G', (byte)'I', (byte)'F', (byte)'8', ..]) return ".gif";
        if (bytes is [(byte)'B', (byte)'M', ..]) return ".bmp";
        if (bytes is [0x00, 0x00, 0x01, 0x00, ..]) return ".ico";

        // WEBP is "RIFF" .... "WEBP".
        if (bytes[..4].SequenceEqual("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8)) return ".webp";

        return null;
    }

    /// <summary>Extensions Steam will render for library artwork.</summary>
    public static readonly string[] SteamCompatible = [".png", ".jpg", ".jpeg", ".webp", ".bmp"];

    /// <summary>True when Steam can display this format as library artwork.</summary>
    public static bool IsSteamCompatible(string extension) =>
        SteamCompatible.Contains(extension, StringComparer.OrdinalIgnoreCase);
}
