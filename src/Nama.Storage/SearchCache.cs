using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Nama.Storage;

/// <summary>
/// Disk-backed cache for provider search responses, with an in-memory layer in front.
/// Typing in the search box re-queries constantly; this keeps repeat terms free and
/// keeps Nama usable when a provider is briefly unreachable.
/// </summary>
public sealed class SearchCache(TimeSpan? lifetime = null, string? directory = null)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly TimeSpan _lifetime = lifetime ?? TimeSpan.FromHours(72);
    private readonly string _directory = directory ?? NamaPaths.SearchCacheDirectory;
    private readonly Dictionary<string, (DateTimeOffset Stamp, string Json)> _memory = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <summary>
    /// Returns the cached value for a key, or runs <paramref name="factory"/> and caches
    /// its result. A cache write failure is ignored — caching is an optimization.
    /// </summary>
    public async Task<T> GetOrAddAsync<T>(
        string provider,
        string key,
        Func<CancellationToken, Task<T>> factory,
        CancellationToken ct = default)
    {
        var cacheKey = BuildKey(provider, key);

        if (TryGet<T>(cacheKey, out var cached) && cached is not null)
            return cached;

        var value = await factory(ct).ConfigureAwait(false);

        try
        {
            Set(cacheKey, value);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Failing to cache is never a reason to fail the request.
        }

        return value;
    }

    private bool TryGet<T>(string cacheKey, out T? value)
    {
        value = default;

        lock (_gate)
        {
            if (_memory.TryGetValue(cacheKey, out var entry))
            {
                if (DateTimeOffset.UtcNow - entry.Stamp <= _lifetime)
                {
                    value = JsonSerializer.Deserialize<T>(entry.Json, Options);
                    return value is not null;
                }
                _memory.Remove(cacheKey);
            }
        }

        try
        {
            var file = PathFor(cacheKey);
            if (!File.Exists(file)) return false;

            if (DateTimeOffset.UtcNow - File.GetLastWriteTimeUtc(file) > _lifetime)
            {
                File.Delete(file);
                return false;
            }

            var json = File.ReadAllText(file);
            value = JsonSerializer.Deserialize<T>(json, Options);

            if (value is not null)
            {
                lock (_gate) _memory[cacheKey] = (DateTimeOffset.UtcNow, json);
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A corrupt cache entry behaves as a miss.
        }

        return false;
    }

    private void Set<T>(string cacheKey, T value)
    {
        var json = JsonSerializer.Serialize(value, Options);

        lock (_gate) _memory[cacheKey] = (DateTimeOffset.UtcNow, json);

        NamaPaths.Ensure(_directory);
        var file = PathFor(cacheKey);
        var temp = file + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, file, overwrite: true);
    }

    /// <summary>Deletes every cached search response.</summary>
    public void Clear()
    {
        lock (_gate) _memory.Clear();

        try
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort.
        }
    }

    private string PathFor(string cacheKey) => Path.Combine(_directory, cacheKey + ".json");

    /// <summary>
    /// Hashes the query into a fixed-length file name, since search terms routinely
    /// contain characters that are illegal in paths.
    /// </summary>
    private static string BuildKey(string provider, string key)
    {
        var normalized = $"{provider}|{key.Trim().ToLowerInvariant()}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return $"{provider}-{Convert.ToHexString(hash)[..16].ToLowerInvariant()}";
    }
}
