using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media.Imaging;
using Nama.Core.Abstractions;

namespace Nama.App.Services;

/// <summary>
/// Loads artwork thumbnails for display.
/// <para>
/// WPF can bind an image straight to an http URI, but that decodes on the UI thread and
/// re-fetches every time a tile scrolls back into view. The artwork step shows dozens of
/// images at once, so this fetches off-thread, caches on disk and in memory, decodes at
/// display width, and freezes the result so it can cross threads.
/// </para>
/// </summary>
public sealed class ThumbnailLoader(IImageDownloader downloader, string cacheDirectory)
{
    private readonly ConcurrentDictionary<string, Task<BitmapImage?>> _inFlight = new();

    public Task<BitmapImage?> LoadAsync(string url, int decodeWidth, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url)) return Task.FromResult<BitmapImage?>(null);

        // One request per URL even when several tiles ask at once.
        return _inFlight.GetOrAdd($"{url}|{decodeWidth}", _ => LoadCoreAsync(url, decodeWidth, ct));
    }

    private async Task<BitmapImage?> LoadCoreAsync(string url, int decodeWidth, CancellationToken ct)
    {
        try
        {
            var bytes = ReadFromDisk(url) ?? await FetchAsync(url, ct).ConfigureAwait(false);

            return bytes is null ? null : Decode(bytes, decodeWidth);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // A thumbnail that will not load shows as an empty tile; it is never fatal.
            return null;
        }
    }

    private async Task<byte[]?> FetchAsync(string url, CancellationToken ct)
    {
        var image = await downloader.DownloadAsync(url, ct).ConfigureAwait(false);
        if (image is null) return null;

        WriteToDisk(url, image.Bytes);
        return image.Bytes;
    }

    private static BitmapImage? Decode(byte[] bytes, int decodeWidth)
    {
        try
        {
            using var stream = new MemoryStream(bytes);

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;   // read fully, then release the stream
            bitmap.DecodePixelWidth = decodeWidth;           // decode small: these are thumbnails
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();                                 // usable from any thread

            return bitmap;
        }
        catch (NotSupportedException)
        {
            return null; // not an image WPF can decode
        }
    }

    private string PathFor(string url) =>
        Path.Combine(cacheDirectory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))) + ".img");

    private byte[]? ReadFromDisk(string url)
    {
        try
        {
            var path = PathFor(url);
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private void WriteToDisk(string url, byte[] bytes)
    {
        try
        {
            Directory.CreateDirectory(cacheDirectory);
            File.WriteAllBytes(PathFor(url), bytes);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Caching is an optimisation.
        }
    }
}
