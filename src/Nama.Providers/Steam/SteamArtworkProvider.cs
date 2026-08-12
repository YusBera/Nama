using Nama.Core.Abstractions;
using Nama.Core.Models;

namespace Nama.Providers.Steam;

/// <summary>
/// The legacy Steam CDN layout, <c>apps/{id}/{file}</c>.
/// <para>
/// Reliable for older apps and nothing else. Valve now serves newer titles from hashed
/// paths (<c>apps/{id}/{hash}/header.jpg</c>) that cannot be derived from the app id, so
/// every one of these URLs 404s for a recent release. They are still worth probing — when
/// they do exist they are the properly library-shaped assets — but they can never be the
/// only source. See <see cref="SteamArtworkProvider"/>.
/// </para>
/// </summary>
public static class SteamCdn
{
    private const string Root = "https://cdn.cloudflare.steamstatic.com/steam/apps";

    public static string Cover(int appId) => $"{Root}/{appId}/library_600x900.jpg";

    public static string CoverHiDpi(int appId) => $"{Root}/{appId}/library_600x900_2x.jpg";

    public static string Header(int appId) => $"{Root}/{appId}/header.jpg";

    public static string Capsule(int appId) => $"{Root}/{appId}/capsule_616x353.jpg";

    public static string Hero(int appId) => $"{Root}/{appId}/library_hero.jpg";

    public static string Logo(int appId) => $"{Root}/{appId}/logo.png";
}

/// <summary>
/// Official Steam store artwork.
/// <para>
/// Worth having alongside SteamGridDB: for a game that is actually on Steam this is the
/// art the user would have got from Steam itself, which is very often what they want.
/// It scores high by default for that reason.
/// </para>
/// <para>
/// Two sources, in that order of preference. The legacy <see cref="SteamCdn"/> paths are
/// HEAD-probed concurrently — when they exist they are the correctly library-shaped assets.
/// For anything they do not cover, the <c>appdetails</c> endpoint is asked for the URLs
/// Steam itself publishes. That fallback is what keeps recent releases from coming back
/// with no artwork at all, since their assets live under hashed paths nothing can guess.
/// </para>
/// </summary>
public sealed class SteamArtworkProvider(ProviderHttp http) : IArtworkProvider
{
    public const string Id = "steam";

    public string SourceId => Id;

    public string DisplayName => "Steam";

    public bool IsAvailable => true;

    public IReadOnlyCollection<ArtworkType> SupportedTypes { get; } =
        [ArtworkType.Cover, ArtworkType.Grid, ArtworkType.Hero, ArtworkType.Logo];

    public bool CanResolve(GameRef game) => game.Has(SteamProvider.Id);

    public async Task<IReadOnlyList<Artwork>> GetArtworkAsync(GameRef game, CancellationToken ct = default)
    {
        if (!int.TryParse(game.GetId(SteamProvider.Id), out var appId)) return [];

        // Known dimensions for each Steam asset; the CDN does not report them.
        var candidates = new (string Url, ArtworkType Type, int Width, int Height, double Score)[]
        {
            (SteamCdn.CoverHiDpi(appId), ArtworkType.Cover, 1200, 1800, 0.95),
            (SteamCdn.Cover(appId), ArtworkType.Cover, 600, 900, 0.90),
            (SteamCdn.Capsule(appId), ArtworkType.Grid, 616, 353, 0.92),
            (SteamCdn.Header(appId), ArtworkType.Grid, 460, 215, 0.90),
            (SteamCdn.Hero(appId), ArtworkType.Hero, 1920, 620, 0.95),
            (SteamCdn.Logo(appId), ArtworkType.Logo, 640, 360, 0.95),
        };

        var present = await Task.WhenAll(
            candidates.Select(async c => (Candidate: c, Exists: await http.AssetExistsAsync(c.Url, ct).ConfigureAwait(false))))
            .ConfigureAwait(false);

        var artwork = new List<Artwork>();

        foreach (var (candidate, exists) in present)
        {
            if (!exists) continue;

            artwork.Add(new Artwork
            {
                Id = $"steam-{appId}-{candidate.Type}-{candidate.Width}",
                Type = candidate.Type,
                Url = candidate.Url,
                ThumbnailUrl = candidate.Url,
                Source = Id,
                Width = candidate.Width,
                Height = candidate.Height,
                Score = candidate.Score,
                Author = "Steam",
            });
        }

        await AddPublishedAssetsAsync(appId, artwork, ct).ConfigureAwait(false);

        return artwork;
    }

    /// <summary>
    /// Fills gaps using the URLs <c>appdetails</c> publishes.
    /// <para>
    /// Only for slots the probes left empty, so an app with real library art does not end
    /// up showing the same image twice. For a recent release, where every constructed path
    /// 404s, this is the difference between some artwork and none.
    /// </para>
    /// </summary>
    private async Task AddPublishedAssetsAsync(int appId, List<Artwork> artwork, CancellationToken ct)
    {
        if (artwork.Any(a => a.Type == ArtworkType.Grid) && artwork.Any(a => a.Type == ArtworkType.Cover)) return;

        var url = $"https://store.steampowered.com/api/appdetails?appids={appId}&l=english";
        using var document = await http.GetJsonAsync(url, ct: ct).ConfigureAwait(false);
        if (document is null) return;

        var entry = document.RootElement.Prop(appId.ToString());
        if (entry is null || !entry.Value.Bool("success")) return;

        var data = entry.Value.Prop("data");
        if (data is null) return;

        // Store header: 460x215, the same shape as the library banner.
        if (!artwork.Any(a => a.Type == ArtworkType.Grid) &&
            data.Value.String("header_image") is { Length: > 0 } header)
        {
            artwork.Add(new Artwork
            {
                Id = $"steam-{appId}-published-header",
                Type = ArtworkType.Grid,
                Url = header,
                ThumbnailUrl = header,
                Source = Id,
                Width = 460,
                Height = 215,
                // Below a real library asset, above nothing at all.
                Score = 0.75,
                Author = "Steam",
            });
        }
    }
}
