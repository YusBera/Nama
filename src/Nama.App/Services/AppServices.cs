using System.Net.Http;
using Nama.Core.Aggregation;
using Nama.Core.Identification;
using Nama.Core.Normalization;
using Nama.Core.Providers;
using Nama.Providers;
using Nama.Providers.Local;
using Nama.SteamIntegration;
using Nama.Storage;

namespace Nama.App.Services;

/// <summary>
/// Composition root. Nama is small enough that a hand-wired container is clearer than a
/// DI framework, and it keeps startup instant.
/// </summary>
public sealed class AppServices : IDisposable
{
    private NamaSettings _settings;

    public AppServices()
    {
        SettingsStore = new SettingsStore();
        _settings = SettingsStore.Load();

        HttpClient = ProviderHttp.CreateClient();
        SearchCache = new SearchCache(TimeSpan.FromHours(Math.Max(1, _settings.SearchCacheHours)));
        ImageCache = new ImageCache(HttpClient);
        ImageLoader = new ImageLoader(ImageCache);

        TokenStore = new ProtectedTokenStore();

        Providers = new ProviderRegistry(HttpClient, SearchCache, () => _settings, TokenStore);
        Normalizer = new NameNormalizer();

        SteamManager = new SteamManager((url, ct) => ImageCache.GetBytesAsync(url, ct));
    }

    public SettingsStore SettingsStore { get; }
    public HttpClient HttpClient { get; }
    public SearchCache SearchCache { get; }
    public ImageCache ImageCache { get; }
    public ImageLoader ImageLoader { get; }
    public ProtectedTokenStore TokenStore { get; }
    public ProviderRegistry Providers { get; }
    public NameNormalizer Normalizer { get; }
    public SteamManager SteamManager { get; }

    /// <summary>Current settings. Mutate via <see cref="SaveSettings"/> so changes reach disk.</summary>
    public NamaSettings Settings => _settings;

    /// <summary>
    /// A fresh identifier over the currently enabled providers. Built per use so a
    /// settings change takes effect on the next search without restarting Nama.
    /// </summary>
    public GameIdentifier CreateIdentifier() => new(Providers.GameProviders, Normalizer);

    /// <summary>
    /// Builds the artwork aggregator. When a local target is supplied the game's own
    /// executable icon is added as an extra provider — it needs the executable path, so
    /// unlike the online providers it cannot be a long-lived singleton.
    /// </summary>
    public ArtworkAggregator CreateArtworkAggregator(LocalGameTarget? target = null)
    {
        var providers = new List<IArtworkProvider>(Providers.ArtworkProviders);

        if (target is not null)
            providers.Add(new ExecutableIconProvider(target));

        return new ArtworkAggregator(providers);
    }

    public void SaveSettings(NamaSettings settings)
    {
        _settings = settings;
        SettingsStore.Save(settings);
    }

    public void Dispose()
    {
        HttpClient.Dispose();
    }
}
