using Nama.Core.Abstractions;

namespace Nama.Providers;

/// <summary>Downloads artwork over HTTP, identifying the format from the content itself.</summary>
public sealed class HttpImageDownloader(HttpClient http, int maxBytes = 32 * 1024 * 1024) : IImageDownloader
{
    public async Task<DownloadedImage?> DownloadAsync(string url, CancellationToken ct = default)
    {
        try
        {
            if (TryLocalPath(url) is { } localPath)
            {
                var localBytes = await File.ReadAllBytesAsync(localPath, ct).ConfigureAwait(false);
                if (localBytes.Length == 0 || localBytes.Length > maxBytes) return null;
                var localExtension = ImageFormat.Detect(localBytes);
                return localExtension is null ? null : new DownloadedImage(localBytes, localExtension);
            }

            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode) return null;

            // Refuse anything implausibly large before reading it into memory.
            if (response.Content.Headers.ContentLength > maxBytes) return null;

            var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            if (bytes.Length == 0 || bytes.Length > maxBytes) return null;

            // Content sniffing, not the URL — providers serve PNG data from .jpg URLs, and
            // Steam decides how to read a file from its extension on disk.
            var extension = ImageFormat.Detect(bytes);

            return extension is null ? null : new DownloadedImage(bytes, extension);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or UnauthorizedAccessException ||
                                  e is TaskCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private static string? TryLocalPath(string value)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.IsFile) return uri.LocalPath;
        return Path.IsPathFullyQualified(value) ? value : null;
    }
}
