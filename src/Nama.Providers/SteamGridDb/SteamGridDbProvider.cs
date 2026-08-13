using System.Net.Http.Headers;
using System.Text.Json.Serialization;
using Nama.Core.Models;
using Nama.Core.Providers;
using Nama.Storage;

namespace Nama.Providers.SteamGridDb;

/// <summary>
/// SteamGridDB — the richest source of Steam-shaped artwork, and the only one that
/// covers non-Steam games well. Requires a free API key; without one the provider
/// disables itself and the rest of Nama carries on.
/// </summary>
public sealed class SteamGridDbProvider(HttpClient httpClient, SearchCache cache, Func<string?> apiKeyAccessor)
    : IGameProvider, IArtworkProvider
{
    private const string ApiBase = "https://www.steamgriddb.com/api/v2";

    /// <summary>How many artwork items to request per type.</summary>
    private const int PageLimit = 50;

    public string Id => "steamgriddb";
    public string DisplayName => "SteamGridDB";
    public int Priority => 20;

    /// <summary>User-facing toggle, independent of whether a key is present.</summary>
    public bool IsUserEnabled { get; set; } = true;

    public bool IsEnabled => IsUserEnabled && !string.IsNullOrWhiteSpace(apiKeyAccessor());

    public IReadOnlyCollection<ArtworkType> SupportedTypes { get; } =
    [
        ArtworkType.Grid, ArtworkType.Cover, ArtworkType.Hero,
        ArtworkType.Logo, ArtworkType.Icon,
    ];

    public async Task<IReadOnlyList<Game>> SearchAsync(string query, CancellationToken ct = default)
    {
        if (!IsEnabled || string.IsNullOrWhiteSpace(query)) return [];

        var response = await cache.GetOrAddAsync(
            Id,
            query,
            token => GetAsync<ApiResponse<List<SearchResult>>>(
                $"{ApiBase}/search/autocomplete/{Uri.EscapeDataString(query)}", token),
            ct).ConfigureAwait(false);

        if (response?.Data is not { Count: > 0 } results) return [];

        return results
            .Where(r => !string.IsNullOrWhiteSpace(r.Name))
            .Take(15)
            .Select(r => new Game
            {
                CanonicalName = r.Name!.Trim(),
                DisplayName = r.Name!.Trim(),
                ReleaseDate = FromUnixSeconds(r.ReleaseDate),
                SourceIds = BuildSourceIds(r),
            })
            .ToList();
    }

    /// <summary>
    /// SteamGridDB knows each game's Steam app id, so recording both lets the Steam
    /// provider contribute official artwork for a game found here.
    /// </summary>
    private IReadOnlyList<GameSourceId> BuildSourceIds(SearchResult result)
    {
        var ids = new List<GameSourceId> { new(Id, result.Id.ToString()) };

        var steamId = result.ExternalPlatformData?.Steam?.FirstOrDefault()?.Id;
        if (!string.IsNullOrWhiteSpace(steamId))
            ids.Add(new GameSourceId("steam", steamId));

        return ids;
    }

    public async Task<IReadOnlyList<Artwork>> GetArtworkAsync(
        Game game,
        IReadOnlyCollection<ArtworkType> types,
        CancellationToken ct = default)
    {
        if (!IsEnabled) return [];

        var gameId = await ResolveGameIdAsync(game, ct).ConfigureAwait(false);
        if (gameId is null) return [];

        // Grids serve both the wide capsule and the vertical cover; the difference is
        // the requested dimensions, so those are two separate calls.
        var requests = new List<(ArtworkType Type, string Url)>();

        if (types.Contains(ArtworkType.Grid))
            requests.Add((ArtworkType.Grid, $"{ApiBase}/grids/game/{gameId}?dimensions=460x215,920x430&limit={PageLimit}"));

        if (types.Contains(ArtworkType.Cover))
            requests.Add((ArtworkType.Cover, $"{ApiBase}/grids/game/{gameId}?dimensions=600x900&limit={PageLimit}"));

        if (types.Contains(ArtworkType.Hero))
            requests.Add((ArtworkType.Hero, $"{ApiBase}/heroes/game/{gameId}?limit={PageLimit}"));

        if (types.Contains(ArtworkType.Logo))
            requests.Add((ArtworkType.Logo, $"{ApiBase}/logos/game/{gameId}?limit={PageLimit}"));

        if (types.Contains(ArtworkType.Icon))
            requests.Add((ArtworkType.Icon, $"{ApiBase}/icons/game/{gameId}?limit={PageLimit}"));

        var responses = await Task.WhenAll(requests.Select(async request =>
        {
            var response = await cache.GetOrAddAsync(
                $"{Id}-art",
                request.Url,
                token => GetAsync<ApiResponse<List<ImageResult>>>(request.Url, token),
                ct).ConfigureAwait(false);

            return (request.Type, response?.Data);
        })).ConfigureAwait(false);

        var artwork = new List<Artwork>();

        foreach (var (type, items) in responses)
        {
            if (items is null) continue;

            foreach (var item in items)
            {
                if (string.IsNullOrWhiteSpace(item.Url)) continue;

                artwork.Add(new Artwork
                {
                    Id = $"sgdb-{item.Id}",
                    Type = type,
                    Url = item.Url!,
                    ThumbnailUrl = item.Thumb,
                    Source = DisplayName,
                    Width = item.Width,
                    Height = item.Height,
                    // Upvotes minus downvotes is a better popularity signal than score
                    // alone, which is often zero on newer uploads.
                    Score = item.Score ?? (item.Upvotes - item.Downvotes),
                    Author = item.Author?.Name,
                    Style = item.Style,
                    IsAnimated = item.Mime?.Contains("webm", StringComparison.OrdinalIgnoreCase) == true
                                 || item.Mime?.Contains("apng", StringComparison.OrdinalIgnoreCase) == true,
                });
            }
        }

        return artwork;
    }

    /// <summary>
    /// Finds this game's SteamGridDB id. Uses the recorded id when the game came from
    /// here, otherwise resolves via the Steam app id, and finally falls back to a
    /// name search.
    /// </summary>
    private async Task<string?> ResolveGameIdAsync(Game game, CancellationToken ct)
    {
        if (game.SourceFor(Id) is { } direct) return direct.Id;

        if (game.SourceFor("steam") is { } steam)
        {
            var bySteamId = await cache.GetOrAddAsync(
                $"{Id}-bysteam",
                steam.Id,
                token => GetAsync<ApiResponse<SearchResult>>($"{ApiBase}/games/steam/{steam.Id}", token),
                ct).ConfigureAwait(false);

            if (bySteamId?.Data is { Id: > 0 } resolved) return resolved.Id.ToString();
        }

        var search = await SearchAsync(game.CanonicalName, ct).ConfigureAwait(false);
        return search.FirstOrDefault()?.SourceFor(Id)?.Id;
    }

    private Task<T?> GetAsync<T>(string url, CancellationToken ct) =>
        ProviderHttp.GetJsonAsync<T>(httpClient, url, ct, request =>
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKeyAccessor()));

    private static DateOnly? FromUnixSeconds(long? seconds) =>
        seconds is > 0 ? DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(seconds.Value).UtcDateTime) : null;

    private sealed class ApiResponse<T>
    {
        public bool Success { get; set; }
        public T? Data { get; set; }
        public List<string>? Errors { get; set; }
    }

    private sealed class SearchResult
    {
        public int Id { get; set; }
        public string? Name { get; set; }

        [JsonPropertyName("release_date")]
        public long? ReleaseDate { get; set; }

        [JsonPropertyName("external_platform_data")]
        public ExternalPlatformData? ExternalPlatformData { get; set; }
    }

    private sealed class ExternalPlatformData
    {
        public List<ExternalPlatformEntry>? Steam { get; set; }
    }

    private sealed class ExternalPlatformEntry
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
    }

    private sealed class ImageResult
    {
        public int Id { get; set; }
        public double? Score { get; set; }
        public string? Style { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public bool Nsfw { get; set; }
        public string? Mime { get; set; }
        public string? Url { get; set; }
        public string? Thumb { get; set; }
        public int Upvotes { get; set; }
        public int Downvotes { get; set; }
        public ImageAuthor? Author { get; set; }
    }

    private sealed class ImageAuthor
    {
        public string? Name { get; set; }
    }
}
