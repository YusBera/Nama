using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Nama.Providers;

/// <summary>
/// Shared HTTP helpers for providers: one configured client, consistent timeouts, and
/// a JSON GET that treats "not found" as an empty result rather than an error.
/// </summary>
public static class ProviderHttp
{
    /// <summary>User agent sent to every provider so Nama is identifiable in their logs.</summary>
    public const string UserAgent = "Nama/0.1 (+https://github.com/nama)";

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>Builds the shared client. Callers should create one and keep it alive.</summary>
    public static HttpClient CreateClient(TimeSpan? timeout = null)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(10),
        };

        var client = new HttpClient(handler)
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(20),
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        return client;
    }

    /// <summary>
    /// GETs and deserializes JSON. Returns default on 404 or on a response the provider
    /// returned but Nama could not parse, so one malformed reply cannot break a search.
    /// </summary>
    public static async Task<T?> GetJsonAsync<T>(
        HttpClient client,
        string url,
        CancellationToken ct,
        Action<HttpRequestMessage>? configure = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        configure?.Invoke(request);

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NoContent)
            return default;

        response.EnsureSuccessStatusCode();

        try
        {
            return await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    /// <summary>POSTs a JSON body and deserializes the JSON response.</summary>
    public static async Task<TResponse?> PostJsonAsync<TRequest, TResponse>(
        HttpClient client,
        string url,
        TRequest body,
        CancellationToken ct)
    {
        using var response = await client.PostAsJsonAsync(url, body, JsonOptions, ct).ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NoContent)
            return default;

        response.EnsureSuccessStatusCode();

        try
        {
            return await response.Content.ReadFromJsonAsync<TResponse>(JsonOptions, ct).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    /// <summary>
    /// Checks whether a URL exists without downloading it. Used for Steam CDN artwork,
    /// which is addressed by convention rather than listed by an API.
    /// </summary>
    public static async Task<bool> ExistsAsync(HttpClient client, string url, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }
}
