using System.Net;
using Nama.Core.Abstractions;
using Nama.Providers.Dlsite;
using Nama.Providers.Igdb;
using Nama.Providers.Steam;
using Nama.Providers.SteamGridDb;
using Nama.Providers.Vndb;

namespace Nama.Providers;

/// <summary>Everything Nama can talk to, already wired up.</summary>
public sealed record ProviderSet(
    IReadOnlyList<IGameProvider> GameProviders,
    IReadOnlyList<IArtworkProvider> ArtworkProviders,
    HttpClient Http) : IDisposable
{
    public void Dispose() => Http.Dispose();
}

/// <summary>
/// Builds the provider set.
/// <para>
/// Adding a provider means adding one line here — nothing in Core, the aggregators or the
/// UI needs to know it exists. That is the whole point of the abstraction, and IGDB is
/// registered despite being a stub to keep the claim honest.
/// </para>
/// </summary>
public static class ProviderFactory
{
    public static ProviderSet Create(ProviderOptions options, ISearchCache? cache = null, HttpClient? httpClient = null)
    {
        var http = httpClient ?? CreateHttpClient(options);
        var transport = new ProviderHttp(http, options, cache);

        var steam = new SteamProvider(transport);
        var vndb = new VndbProvider(transport);
        var dlsite = new DlsiteProvider(transport);
        var igdb = new IgdbProvider();

        return new ProviderSet(
            GameProviders: [dlsite, steam, vndb, igdb],
            ArtworkProviders: [new SteamGridDbProvider(transport), new SteamArtworkProvider(transport), dlsite, vndb, igdb],
            Http: http);
    }

    private static HttpClient CreateHttpClient(ProviderOptions options)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            // Providers already run concurrently; this keeps Nama from looking like a
            // scraper to any single host.
            MaxConnectionsPerServer = 8,
        };

        var client = new HttpClient(handler)
        {
            // Per-request timeouts are enforced in ProviderHttp so one slow provider does
            // not stall the rest; this is only a backstop.
            Timeout = TimeSpan.FromSeconds(30),
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd(options.UserAgent);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");

        return client;
    }
}
