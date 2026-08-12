using System.Text.Json;
using Nama.Core.Abstractions;
using Nama.Core.Models;
using Nama.Providers.Steam;

namespace Nama.Providers.SteamGridDb;

/// <summary>
/// SteamGridDB — the main artwork source, and the only one that covers games with no
/// official Steam art at all.
/// <para>
/// Needs a personal API key. Without one it reports itself unavailable and is skipped;
/// Steam and VNDB continue to work, so the app is usable before the user sets one up.
/// </para>
/// <para>
/// Resolution goes through a Steam app id when there is one, since that is an exact
/// mapping, and falls back to a name search otherwise — which is how VN and non-Steam
/// titles still find artwork.
/// </para>
/// </summary>
public sealed class SteamGridDbProvider(ProviderHttp http) : IArtworkProvider
{
    public const string Id = "steamgriddb";

    private const string Api = "https://www.steamgriddb.com/api/v2";

    public string SourceId => Id;

    public string DisplayName => "SteamGridDB";

    public bool IsAvailable => !string.IsNullOrWhiteSpace(http.Options.SteamGridDbApiKey);

    public IReadOnlyCollection<ArtworkType> SupportedTypes { get; } =
        [ArtworkType.Grid, ArtworkType.Cover, ArtworkType.Hero, ArtworkType.Logo, ArtworkType.Icon];

    /// <summary>Anything with a name can be attempted, so this accepts every ref.</summary>
    public bool CanResolve(GameRef game) => IsAvailable;

    public async Task<IReadOnlyList<Artwork>> GetArtworkAsync(GameRef game, CancellationToken ct = default)
    {
        if (!IsAvailable) return [];

        var gameId = await ResolveGameIdAsync(game, ct).ConfigureAwait(false);
        if (gameId is null) return [];

        // The four endpoints are independent, so fetch them together rather than serially.
        var groups = await Task.WhenAll(
            FetchAsync("grids", gameId.Value, ClassifyGrid, ct),
            FetchAsync("heroes", gameId.Value, _ => ArtworkType.Hero, ct),
            FetchAsync("logos", gameId.Value, _ => ArtworkType.Logo, ct),
            FetchAsync("icons", gameId.Value, _ => ArtworkType.Icon, ct)).ConfigureAwait(false);

        return groups.SelectMany(g => g).ToList();
    }

    /// <summary>
    /// Maps this game to a SteamGridDB id. The Steam route is exact; the name search is a
    /// fallback and takes the first hit, which the artwork picker lets the user override
    /// by simply not choosing the results.
    /// </summary>
    private async Task<int?> ResolveGameIdAsync(GameRef game, CancellationToken ct)
    {
        if (game.GetId(SteamProvider.Id) is { Length: > 0 } steamAppId)
        {
            using var bySteam = await GetAsync($"{Api}/games/steam/{steamAppId}", ct).ConfigureAwait(false);
            if (bySteam?.RootElement.Prop("data")?.Int("id") is { } id) return id;
        }

        foreach (var term in new[] { game.Name, game.JapaneseName })
        {
            if (string.IsNullOrWhiteSpace(term)) continue;

            using var search = await GetAsync(
                $"{Api}/search/autocomplete/{Uri.EscapeDataString(term)}", ct).ConfigureAwait(false);
            if (search is null) continue;

            foreach (var item in search.RootElement.Array("data"))
            {
                if (item.Int("id") is { } id) return id;
            }
        }

        return null;
    }

    private async Task<IReadOnlyList<Artwork>> FetchAsync(
        string endpoint, int gameId, Func<JsonElement, ArtworkType> classify, CancellationToken ct)
    {
        using var document = await GetAsync($"{Api}/{endpoint}/game/{gameId}", ct).ConfigureAwait(false);
        if (document is null) return [];

        var artwork = new List<Artwork>();

        foreach (var item in document.RootElement.Array("data"))
        {
            var url = item.String("url");
            if (string.IsNullOrWhiteSpace(url)) continue;

            var mime = item.String("mime");

            artwork.Add(new Artwork
            {
                Id = $"sgdb-{item.Int("id")?.ToString() ?? url.GetHashCode().ToString()}",
                Type = classify(item),
                Url = url,
                ThumbnailUrl = item.String("thumb") ?? url,
                Source = Id,
                Width = item.Int("width") ?? 0,
                Height = item.Int("height") ?? 0,
                Score = NormalizeScore(item),
                Votes = item.Int("upvotes"),
                Author = item.Prop("author")?.String("name"),
                Style = item.String("style"),
                IsAnimated = mime is "image/gif" or "image/apng",
                IsNsfw = item.Bool("nsfw"),
            });
        }

        return artwork;
    }

    /// <summary>
    /// A "grid" on SteamGridDB is both the wide library capsule and the portrait cover,
    /// distinguished only by shape. Steam stores them in different files, so they have to
    /// be separated here or covers would end up written to the banner slot.
    /// </summary>
    private static ArtworkType ClassifyGrid(JsonElement item)
    {
        var width = item.Int("width") ?? 0;
        var height = item.Int("height") ?? 0;

        if (height == 0) return ArtworkType.Grid;

        return (double)width / height < 1.0 ? ArtworkType.Cover : ArtworkType.Grid;
    }

    /// <summary>
    /// Turns votes into the 0..1 score the ranker expects. Unvoted artwork sits mid-range
    /// rather than at the bottom — new uploads are not bad, just unrated.
    /// </summary>
    private static double NormalizeScore(JsonElement item)
    {
        var up = item.Int("upvotes") ?? 0;
        var down = item.Int("downvotes") ?? 0;
        var total = up + down;

        return total == 0 ? 0.5 : (double)up / total;
    }

    private Task<JsonDocument?> GetAsync(string url, CancellationToken ct) =>
        http.GetJsonAsync(url, http.Options.SteamGridDbApiKey, ct);
}
