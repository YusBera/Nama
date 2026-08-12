using System.IO;
using System.Net.Http;
using Nama.Core.Aggregation;
using Nama.Core.Identification;
using Nama.Providers;
using Nama.Steam;
using Nama.Storage;

namespace Nama.App.Services;

/// <summary>
/// Composition root. Built once at startup and handed to the view models.
/// <para>
/// Deliberately plain rather than a DI container: the object graph is small, fixed, and
/// this way the whole wiring of the app is readable in one screen.
/// </para>
/// </summary>
public sealed class AppServices : IDisposable
{
    private AppServices(
        NamaSettings settings,
        SqliteSearchCache cache,
        ProviderSet providers,
        SteamManager steam,
        ThumbnailLoader thumbnails)
    {
        Settings = settings;
        Cache = cache;
        Providers = providers;
        Steam = steam;
        Thumbnails = thumbnails;

        Identifier = new GameIdentifier(new CandidateExtractor(), new GameSearchAggregator(providers.GameProviders));
        Artwork = new ArtworkAggregator(providers.ArtworkProviders);
        Downloader = new HttpImageDownloader(providers.Http);
    }

    public NamaSettings Settings { get; private set; }

    public SqliteSearchCache Cache { get; }

    public ProviderSet Providers { get; private set; }

    public SteamManager Steam { get; }

    public ThumbnailLoader Thumbnails { get; }

    public GameIdentifier Identifier { get; private set; }

    public ArtworkAggregator Artwork { get; private set; }

    public HttpImageDownloader Downloader { get; private set; }

    /// <summary>True when SteamGridDB has no key, so the artwork step will be thin.</summary>
    public bool ArtworkIsLimited => string.IsNullOrWhiteSpace(Settings.SteamGridDbApiKey);

    public static AppServices Create()
    {
        var settings = NamaSettings.Load();
        var cache = new SqliteSearchCache();
        var providers = ProviderFactory.Create(
            OptionsFrom(settings), cache);

        // Its own client, not the provider set's: ReloadProviders disposes the old set, and
        // a thumbnail loader holding that client would silently stop working the moment the
        // user saved settings.
        var thumbnailHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };

        var thumbnails = new ThumbnailLoader(
            new HttpImageDownloader(thumbnailHttp),
            Path.Combine(Path.GetDirectoryName(SqliteSearchCache.DefaultPath)!, "thumbs"));

        return new AppServices(settings, cache, providers, new SteamManager(), thumbnails)
        {
            ThumbnailHttp = thumbnailHttp,
        };
    }

    /// <summary>Rebuilds the providers after the API key changes, without restarting the app.</summary>
    public void ReloadProviders()
    {
        Settings = NamaSettings.Load();

        var replacement = ProviderFactory.Create(
            OptionsFrom(Settings), Cache);

        var previous = Providers;
        Providers = replacement;

        Identifier = new GameIdentifier(new CandidateExtractor(), new GameSearchAggregator(replacement.GameProviders));
        Artwork = new ArtworkAggregator(replacement.ArtworkProviders);
        Downloader = new HttpImageDownloader(replacement.Http);

        previous.Dispose();
    }

    private static ProviderOptions OptionsFrom(NamaSettings settings) => new()
    {
        SteamGridDbApiKey = settings.SteamGridDbApiKey,
        EnableDlsite = settings.ExperimentalDlsiteEnabled,
        EnableVndb = settings.ExperimentalVndbEnabled,
    };

    private HttpClient? ThumbnailHttp { get; init; }

    public void Dispose()
    {
        Providers.Dispose();
        ThumbnailHttp?.Dispose();
        Cache.Dispose();
    }
}
