namespace Nama.Core.Abstractions;

/// <summary>
/// Local cache for provider responses.
/// <para>
/// Always best-effort. Every method must swallow its own failures — a broken or locked
/// cache degrades to a live request, it never surfaces an error. Callers are not expected
/// to handle exceptions from this interface.
/// </para>
/// </summary>
public interface ISearchCache
{
    /// <summary>Returns the cached payload, or null when absent or expired.</summary>
    Task<string?> GetAsync(string key, CancellationToken ct = default);

    /// <summary>Stores a payload. Overwrites any existing entry for the key.</summary>
    Task SetAsync(string key, string payload, TimeSpan ttl, CancellationToken ct = default);

    /// <summary>Drops everything. Used by the "clear cache" action in settings.</summary>
    Task ClearAsync(CancellationToken ct = default);
}

/// <summary>No-op cache. Used when caching is disabled or unavailable.</summary>
public sealed class NullSearchCache : ISearchCache
{
    public static readonly NullSearchCache Instance = new();

    public Task<string?> GetAsync(string key, CancellationToken ct = default) => Task.FromResult<string?>(null);

    public Task SetAsync(string key, string payload, TimeSpan ttl, CancellationToken ct = default) => Task.CompletedTask;

    public Task ClearAsync(CancellationToken ct = default) => Task.CompletedTask;
}
