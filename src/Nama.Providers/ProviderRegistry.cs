using Nama.Core.Providers;
using Nama.Providers.Igdb;
using Nama.Providers.Steam;
using Nama.Providers.SteamGridDb;
using Nama.Providers.Vndb;
using Nama.Storage;

namespace Nama.Providers;

/// <summary>
/// Builds the provider set from current settings. This is the one place that names
/// concrete providers; identification and artwork aggregation only ever see the
/// interfaces, so adding a provider means adding it here and nowhere else.
/// </summary>
public sealed class ProviderRegistry
{
    private readonly SteamStoreProvider _steam;
    private readonly SteamGridDbProvider _steamGridDb;
    private readonly VndbProvider _vndb;
    private readonly IgdbProvider _igdb;
    private readonly Func<NamaSettings> _settings;

    public ProviderRegistry(HttpClient httpClient, SearchCache cache, Func<NamaSettings> settings)
    {
        _settings = settings;

        _steam = new SteamStoreProvider(httpClient, cache);
        _steamGridDb = new SteamGridDbProvider(httpClient, cache, () => _settings().SteamGridDbApiKey);
        _vndb = new VndbProvider(httpClient, cache);
        _igdb = new IgdbProvider(httpClient, cache,
            () => (_settings().IgdbClientId, _settings().IgdbClientSecret));

        ApplySettings();
    }

    /// <summary>Every provider, whether or not it is currently enabled.</summary>
    public IReadOnlyList<object> All => [_steam, _steamGridDb, _vndb, _igdb];

    public IReadOnlyList<IGameProvider> GameProviders
    {
        get
        {
            ApplySettings();
            return [_steam, _steamGridDb, _vndb, _igdb];
        }
    }

    public IReadOnlyList<IArtworkProvider> ArtworkProviders
    {
        get
        {
            ApplySettings();
            return [_steamGridDb, _steam, _vndb, _igdb];
        }
    }

    /// <summary>
    /// True when nothing can supply Steam-shaped artwork, which is the case when the
    /// user has no SteamGridDB key and the game is not on Steam. The UI uses this to
    /// nudge the user toward adding a key.
    /// </summary>
    public bool HasSteamGridDbKey => !string.IsNullOrWhiteSpace(_settings().SteamGridDbApiKey);

    /// <summary>Describes each provider for the settings screen.</summary>
    public IEnumerable<ProviderStatus> Describe()
    {
        ApplySettings();

        yield return new ProviderStatus(_steam.Id, _steam.DisplayName, _steam.IsEnabled, null);
        yield return new ProviderStatus(_steamGridDb.Id, _steamGridDb.DisplayName, _steamGridDb.IsEnabled,
            HasSteamGridDbKey ? null : "Needs a free API key");
        yield return new ProviderStatus(_vndb.Id, _vndb.DisplayName, _vndb.IsEnabled, null);
        yield return new ProviderStatus(_igdb.Id, _igdb.DisplayName, _igdb.IsEnabled,
            _igdb.IsEnabled ? null : "Needs Twitch client credentials");
    }

    /// <summary>Pushes the user's enable/disable choices onto the provider instances.</summary>
    private void ApplySettings()
    {
        var settings = _settings();

        _steam.IsEnabled = settings.IsProviderEnabled(_steam.Id);
        _steamGridDb.IsUserEnabled = settings.IsProviderEnabled(_steamGridDb.Id);
        _vndb.IsEnabled = settings.IsProviderEnabled(_vndb.Id);
        _igdb.IsUserEnabled = settings.IsProviderEnabled(_igdb.Id);
    }
}

/// <param name="Id">Provider id.</param>
/// <param name="DisplayName">Name shown to the user.</param>
/// <param name="IsEnabled">Whether it will run.</param>
/// <param name="Requirement">Why it cannot run, when applicable.</param>
public readonly record struct ProviderStatus(string Id, string DisplayName, bool IsEnabled, string? Requirement);
