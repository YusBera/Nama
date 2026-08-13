using System.IO;
using System.Windows.Media.Imaging;
using Nama.Storage;

namespace Nama.App.Services;

/// <summary>
/// Turns a remote image URL into a frozen <see cref="BitmapSource"/> the UI can bind to.
///
/// Decoding happens off the UI thread and the result is frozen, which is what keeps the
/// artwork grid smooth while dozens of thumbnails arrive at once.
/// </summary>
public sealed class ImageLoader(ImageCache cache)
{
    /// <summary>
    /// Loads and decodes an image. Returns null when it could not be fetched or decoded,
    /// which the UI renders as an empty tile rather than an error.
    /// </summary>
    /// <param name="decodeWidth">
    /// Target width in pixels. Decoding thumbnails at display size rather than full
    /// resolution keeps memory flat when a section holds fifty 4K images.
    /// </param>
    public async Task<BitmapSource?> LoadAsync(string? url, int decodeWidth = 0, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        var bytes = await cache.GetBytesAsync(url, ct).ConfigureAwait(false);
        if (bytes is null || bytes.Length == 0) return null;

        try
        {
            return await Task.Run(() => Decode(bytes, decodeWidth), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException or FileFormatException or OverflowException)
        {
            // Providers occasionally serve formats WPF cannot decode (webm previews,
            // truncated files). An empty tile is a better outcome than a crash.
            return null;
        }
    }

    private static BitmapSource Decode(byte[] bytes, int decodeWidth)
    {
        using var stream = new MemoryStream(bytes);

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
        bitmap.StreamSource = stream;
        if (decodeWidth > 0) bitmap.DecodePixelWidth = decodeWidth;
        bitmap.EndInit();
        bitmap.Freeze();

        return bitmap;
    }
}
