using System.Text.Json.Serialization;
using Nama.Core.Models;
using Nama.Core.Providers;
using Nama.Storage;

namespace Nama.Providers.Steam;

/// <summary>
/// Searches the public Steam store and derives artwork from Steam's CDN.
///
/// Steam has no artwork API: the images live at well-known paths under the app id, so
/// Nama probes the ones that exist rather than listing them. This provider needs no
/// credentials, which is why it runs first.
/// </summary>
public sealed class SteamStoreProvider(HttpClient httpClient, SearchCache cache)
    : IGameProvider, IArtworkProvider
{
    public string Id => "steam";
    public string DisplayName => "Steam";
    public bool IsEnabled { get; set; } = true;
    public int Priority => 10;

    public IReadOnlyCollection<ArtworkType> SupportedTypes { get; } =
    [
        ArtworkType.Grid, ArtworkType.Cover, ArtworkType.Hero,
        ArtworkType.Logo, ArtworkType.Background,
    ];

    private const string CdnBase = "https://cdn.cloudflare.steamstatic.com/steam/apps";

    /// <summary>How many results get a follow-up details request for developer and date.</summary>
    private const int EnrichCount = 6;

    public async Task<IReadOnlyList<Game>> SearchAsync(string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];

        var response = await cache.GetOrAddAsync(
            Id,
            query,
            token => ProviderHttp.GetJsonAsync<StoreSearchResponse>(
                httpClient,
                $"https://store.steampowered.com/api/storesearch/?term={Uri.EscapeDataString(query)}&l=english&cc=US",
                token),
            ct).ConfigureAwait(false);

        var items = response?.Items;
        if (items is null || items.Count == 0) return [];

        var games = items
            .Where(i => i.Id > 0 && !string.IsNullOrWhiteSpace(i.Name))
            .Take(15)
            .Select(ToGame)
            .ToList();

        // The candidate list shows "Developer · Year", which the search endpoint omits.
        await EnrichAsync(games, ct).ConfigureAwait(false);

        return games;
    }

    private Game ToGame(StoreSearchItem item) => new()
    {
        CanonicalName = item.Name!.Trim(),
        DisplayName = item.Name!.Trim(),
        Platforms = BuildPlatforms(item.Platforms),
        PreviewImageUrl = string.IsNullOrWhiteSpace(item.TinyImage)
            ? $"{CdnBase}/{item.Id}/capsule_231x87.jpg"
            : item.TinyImage,
        SourceIds = [new GameSourceId(Id, item.Id.ToString())],
    };

    private static IReadOnlyList<string> BuildPlatforms(StorePlatforms? platforms)
    {
        if (platforms is null) return [];

        var result = new List<string>(3);
        if (platforms.Windows) result.Add("Windows");
        if (platforms.Mac) result.Add("macOS");
        if (platforms.Linux) result.Add("Linux");
        return result;
    }

    /// <summary>
    /// Fills in developer, publisher and release date for the top results. Failures are
    /// swallowed: the extra detail is cosmetic and the store details endpoint is
    /// aggressively rate limited.
    /// </summary>
    private async Task EnrichAsync(List<Game> games, CancellationToken ct)
    {
        var targets = games.Take(EnrichCount).ToList();

        var details = await Task.WhenAll(targets.Select(async game =>
        {
            var appId = game.SourceFor(Id)?.Id;
            if (appId is null) return (game, (AppDetails?)null);

            try
            {
                var response = await cache.GetOrAddAsync(
                    $"{Id}-details",
                    appId,
                    token => ProviderHttp.GetJsonAsync<Dictionary<string, AppDetailsEnvelope>>(
                        httpClient,
                        $"https://store.steampowered.com/api/appdetails?appids={appId}&l=english",
                        token),
                    ct).ConfigureAwait(false);

                return (game, response?.GetValueOrDefault(appId)?.Data);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                return (game, null);
            }
        })).ConfigureAwait(false);

        for (var i = 0; i < targets.Count; i++)
        {
            var (game, data) = details[i];
            if (data is null) continue;

            games[games.IndexOf(game)] = new Game
            {
                CanonicalName = game.CanonicalName,
                DisplayName = game.DisplayName,
                Aliases = game.Aliases,
                Platforms = game.Platforms,
                PreviewImageUrl = game.PreviewImageUrl,
                SourceIds = game.SourceIds,
                Developer = data.Developers?.FirstOrDefault(),
                Publisher = data.Publishers?.FirstOrDefault(),
                ReleaseDate = ParseReleaseDate(data.ReleaseDate?.Date),
                Summary = data.ShortDescription,
            };
        }
    }

    /// <summary>
    /// Steam formats release dates for the requested locale ("21 Feb, 2022"), so parsing
    /// is lenient and falls back to pulling out a bare year.
    /// </summary>
    internal static DateOnly? ParseReleaseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        if (DateOnly.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var parsed))
            return parsed;

        var match = System.Text.RegularExpressions.Regex.Match(value, @"(19|20)\d{2}");
        return match.Success ? new DateOnly(int.Parse(match.Value), 1, 1) : null;
    }

    public async Task<IReadOnlyList<Artwork>> GetArtworkAsync(
        Game game,
        IReadOnlyCollection<ArtworkType> types,
        CancellationToken ct = default)
    {
        var appId = game.SourceFor(Id)?.Id;
        if (appId is null) return [];

        // Each entry is a Steam CDN convention. Nama probes them in parallel and keeps
        // whichever actually exist for this app.
        var candidates = new List<(ArtworkType Type, string Url, int Width, int Height)>
        {
            (ArtworkType.Grid, $"{CdnBase}/{appId}/header.jpg", 460, 215),
            (ArtworkType.Grid, $"{CdnBase}/{appId}/capsule_616x353.jpg", 616, 353),
            (ArtworkType.Cover, $"{CdnBase}/{appId}/library_600x900_2x.jpg", 1200, 1800),
            (ArtworkType.Cover, $"{CdnBase}/{appId}/library_600x900.jpg", 600, 900),
            (ArtworkType.Hero, $"{CdnBase}/{appId}/library_hero.jpg", 1920, 620),
            (ArtworkType.Logo, $"{CdnBase}/{appId}/logo.png", 640, 360),
            (ArtworkType.Background, $"{CdnBase}/{appId}/page_bg_generated_v6b.jpg", 1438, 810),
        };

        var wanted = candidates.Where(c => types.Contains(c.Type)).ToList();

        var existence = await Task.WhenAll(
            wanted.Select(c => ProviderHttp.ExistsAsync(httpClient, c.Url, ct))).ConfigureAwait(false);

        var artwork = new List<Artwork>();

        for (var i = 0; i < wanted.Count; i++)
        {
            if (!existence[i]) continue;

            var (type, url, width, height) = wanted[i];

            artwork.Add(new Artwork
            {
                Id = $"steam-{appId}-{type}-{i}",
                Type = type,
                Url = url,
                ThumbnailUrl = url,
                Source = DisplayName,
                Width = width,
                Height = height,
                // Official store art is a safe default, so it ranks above average but
                // below highly-rated community work.
                Score = 0.7,
                Author = "Official",
                Style = "official",
            });
        }

        return artwork;
    }

    private sealed class StoreSearchResponse
    {
        [JsonPropertyName("items")]
        public List<StoreSearchItem>? Items { get; set; }
    }

    private sealed class StoreSearchItem
    {
        public long Id { get; set; }
        public string? Name { get; set; }

        [JsonPropertyName("tiny_image")]
        public string? TinyImage { get; set; }

        public StorePlatforms? Platforms { get; set; }
    }

    private sealed class StorePlatforms
    {
        public bool Windows { get; set; }
        public bool Mac { get; set; }
        public bool Linux { get; set; }
    }

    private sealed class AppDetailsEnvelope
    {
        public bool Success { get; set; }
        public AppDetails? Data { get; set; }
    }

    private sealed class AppDetails
    {
        public List<string>? Developers { get; set; }
        public List<string>? Publishers { get; set; }

        [JsonPropertyName("short_description")]
        public string? ShortDescription { get; set; }

        [JsonPropertyName("release_date")]
        public ReleaseDateInfo? ReleaseDate { get; set; }
    }

    private sealed class ReleaseDateInfo
    {
        [JsonPropertyName("coming_soon")]
        public bool ComingSoon { get; set; }
        public string? Date { get; set; }
    }
}
