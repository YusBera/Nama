using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Nama.Storage;

/// <summary>
/// Caches downloaded images on disk by URL. The artwork picker shows dozens of
/// thumbnails at once and users scroll back and forth, so re-downloading would make
/// the UI feel slow for no reason.
/// </summary>
public sealed class ImageCache(HttpClient httpClient, string? directory = null)
{
    private readonly string _directory = directory ?? NamaPaths.ImageCacheDirectory;

    /// <summary>Collapses concurrent requests for the same URL into a single download.</summary>
    private readonly ConcurrentDictionary<string, Task<string?>> _inFlight = new(StringComparer.Ordinal);

    /// <summary>
    /// Returns a local file path for <paramref name="url"/>, downloading it if needed.
    /// Returns null when the image could not be fetched.
    /// </summary>
    public Task<string?> GetLocalPathAsync(string url, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url)) return Task.FromResult<string?>(null);

        // Artwork that already lives on disk — an icon extracted from the game's own
        // executable — needs no download and no second copy in the cache.
        if (TryGetExistingLocalFile(url, out var local))
            return Task.FromResult<string?>(local);

        return _inFlight.GetOrAdd(url, u => DownloadAsync(u, ct));
    }

    /// <summary>Resolves a <c>file://</c> URI (or a plain path) to an existing file.</summary>
    private static bool TryGetExistingLocalFile(string url, out string? path)
    {
        path = null;

        try
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                if (!uri.IsFile) return false;
                path = uri.LocalPath;
            }
            else if (Path.IsPathFullyQualified(url))
            {
                path = url;
            }
            else
            {
                return false;
            }

            if (File.Exists(path)) return true;

            path = null;
            return false;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            path = null;
            return false;
        }
    }

    /// <summary>Reads an image into memory, from cache when possible.</summary>
    public async Task<byte[]?> GetBytesAsync(string url, CancellationToken ct = default)
    {
        var path = await GetLocalPathAsync(url, ct).ConfigureAwait(false);
        if (path is null) return null;

        try
        {
            return await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private async Task<string?> DownloadAsync(string url, CancellationToken ct)
    {
        try
        {
            var path = PathFor(url);

            if (File.Exists(path) && new FileInfo(path).Length > 0)
                return path;

            using var response = await httpClient
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode) return null;

            NamaPaths.Ensure(_directory);

            // Download to a temp file first so an interrupted transfer never leaves a
            // truncated image that later reads would treat as valid.
            var temp = $"{path}.{Environment.CurrentManagedThreadId}.tmp";

            await using (var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var file = File.Create(temp))
            {
                await stream.CopyToAsync(file, ct).ConfigureAwait(false);
            }

            File.Move(temp, path, overwrite: true);
            return path;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            // Allow a later retry rather than caching the failure forever.
            _inFlight.TryRemove(url, out _);
        }
    }

    /// <summary>Maps a URL to a stable cache file name, preserving the original extension.</summary>
    private string PathFor(string url)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))).ToLowerInvariant();

        var extension = ".img";
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var candidate = Path.GetExtension(uri.AbsolutePath);
            if (candidate.Length is > 1 and <= 5 && candidate.All(c => char.IsLetterOrDigit(c) || c == '.'))
                extension = candidate.ToLowerInvariant();
        }

        return Path.Combine(_directory, hash[..24] + extension);
    }

    /// <summary>Deletes every cached image.</summary>
    public void Clear()
    {
        try
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort.
        }
    }

    /// <summary>Total bytes currently held on disk, for the settings screen.</summary>
    public long SizeOnDisk()
    {
        try
        {
            if (!Directory.Exists(_directory)) return 0;
            return new DirectoryInfo(_directory).EnumerateFiles().Sum(f => f.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
