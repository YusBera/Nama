using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nama.Core.Abstractions;

namespace Nama.Providers;

/// <summary>
/// Shared HTTP plumbing for providers: timeouts, caching, and — most importantly — the
/// rule that ordinary failures produce null rather than an exception.
/// <para>
/// Nama merges results from several providers. One of them being down, rate limited, or
/// returning something unexpected must degrade the result set, never break the flow. Every
/// method here returns null on failure and records why in <see cref="LastError"/>.
/// </para>
/// <para>
/// Caching happens at this layer, keyed by method + URL + body, so search calls, detail
/// lookups and asset probes all benefit without each provider implementing it.
/// </para>
/// </summary>
public sealed class ProviderHttp(HttpClient http, ProviderOptions options, ISearchCache? cache = null)
{
    private readonly ISearchCache _cache = cache ?? NullSearchCache.Instance;

    /// <summary>Description of the most recent failure, for surfacing a provider as degraded.</summary>
    public string? LastError { get; private set; }

    public ProviderOptions Options => options;

    /// <summary>GETs a JSON document. Returns null on any failure.</summary>
    public Task<JsonDocument?> GetJsonAsync(string url, string? bearerToken = null, CancellationToken ct = default) =>
        SendJsonAsync(HttpMethod.Get, url, body: null, bearerToken, ct);

    /// <summary>POSTs a JSON body and reads a JSON document. Returns null on any failure.</summary>
    public Task<JsonDocument?> PostJsonAsync(string url, string body, string? bearerToken = null, CancellationToken ct = default) =>
        SendJsonAsync(HttpMethod.Post, url, body, bearerToken, ct);

    private async Task<JsonDocument?> SendJsonAsync(
        HttpMethod method, string url, string? body, string? bearerToken, CancellationToken ct)
    {
        var cacheKey = BuildCacheKey(method, url, body);

        var cached = await TryGetCachedAsync(cacheKey, ct).ConfigureAwait(false);
        if (cached is not null && TryParse(cached, out var fromCache)) return fromCache;

        var payload = await SendAsync(method, url, body, bearerToken, ct).ConfigureAwait(false);
        if (payload is null) return null;

        if (!TryParse(payload, out var parsed))
        {
            LastError = $"{url}: response was not valid JSON.";
            return null;
        }

        // Only successful responses are cached; a rate-limit or outage must not be
        // remembered for a week.
        await TrySetCachedAsync(cacheKey, payload, options.CacheTtl, ct).ConfigureAwait(false);
        return parsed;

        static bool TryParse(string text, out JsonDocument? document)
        {
            try
            {
                document = JsonDocument.Parse(text);
                return true;
            }
            catch (JsonException)
            {
                document = null;
                return false;
            }
        }
    }

    private async Task<string?> SendAsync(
        HttpMethod method, string url, string? body, string? bearerToken, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(options.Timeout);

            using var request = new HttpRequestMessage(method, url);
            if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            if (bearerToken is not null) request.Headers.Authorization = new("Bearer", bearerToken);

            using var response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                LastError = response.StatusCode switch
                {
                    HttpStatusCode.TooManyRequests => $"{url}: rate limited by the provider.",
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => $"{url}: rejected — check the API key.",
                    _ => $"{url}: HTTP {(int)response.StatusCode}.",
                };
                return null;
            }

            LastError = null;
            return await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            LastError = $"{url}: timed out after {options.Timeout.TotalSeconds:0.#}s.";
            return null;
        }
        catch (OperationCanceledException)
        {
            throw; // The caller cancelled; that is not a provider failure.
        }
        catch (HttpRequestException e)
        {
            LastError = $"{url}: {e.Message}";
            return null;
        }
    }

    /// <summary>
    /// True when a URL resolves to an actual asset. Uses HEAD, which Steam's CDN supports
    /// and which avoids downloading images only to discover they are missing — many apps
    /// lack a logo or hero.
    /// </summary>
    public async Task<bool> AssetExistsAsync(string url, CancellationToken ct = default)
    {
        var cacheKey = BuildCacheKey(HttpMethod.Head, url, body: null);

        var cached = await TryGetCachedAsync(cacheKey, ct).ConfigureAwait(false);
        if (cached is not null) return cached == "1";

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(options.Timeout);

            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            using var response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);

            var exists = response.IsSuccessStatusCode;

            // A definite 404 is worth remembering; a transient failure is not.
            if (exists || response.StatusCode == HttpStatusCode.NotFound)
            {
                await TrySetCachedAsync(cacheKey, exists ? "1" : "0", options.CacheTtl, ct).ConfigureAwait(false);
            }

            return exists;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    // --- cache access ----------------------------------------------------------------
    // The cache contract says implementations swallow their own errors, but a custom or
    // third-party one might not. Treat any failure here as a miss.

    private async Task<string?> TryGetCachedAsync(string key, CancellationToken ct)
    {
        try
        {
            return await _cache.GetAsync(key, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return null;
        }
    }

    private async Task TrySetCachedAsync(string key, string payload, TimeSpan ttl, CancellationToken ct)
    {
        try
        {
            await _cache.SetAsync(key, payload, ttl, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Caching is an optimisation. Losing it changes nothing the user can see.
        }
    }

    private static string BuildCacheKey(HttpMethod method, string url, string? body)
    {
        if (body is null) return $"{method.Method} {url}";

        // POST bodies (VNDB) are part of the identity of the request, but can be long.
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)))[..16];
        return $"{method.Method} {url} {hash}";
    }
}
