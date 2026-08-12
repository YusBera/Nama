using System.Text.Json;
using Nama.Core.Abstractions;
using Nama.Core.Models;

namespace Nama.Providers.Steam;

/// <summary>
/// Game lookup against the Steam store.
/// <para>
/// Two endpoints are involved. <c>storesearch</c> answers the query but returns only name,
/// id and a thumbnail — measured, not assumed. Developer and release date, which are what
/// let a user tell "Elden Ring" from "Elden Ring Nightreign" at a glance, need a follow-up
/// <c>appdetails</c> call per result. That endpoint is rate limited, so only the top few
/// results are enriched (see <see cref="ProviderOptions.SteamEnrichmentLimit"/>) and every
/// response is cached.
/// </para>
/// </summary>
public sealed class SteamProvider(ProviderHttp http) : IGameProvider
{
    public const string Id = "steam";

    public string SourceId => Id;

    public string DisplayName => "Steam";

    /// <summary>Needs no API key.</summary>
    public bool IsAvailable => true;

    public int Priority => 100;

    public async Task<IReadOnlyList<GameCandidate>> SearchAsync(string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];

        var url = $"https://store.steampowered.com/api/storesearch/?term={Uri.EscapeDataString(query)}&l=english&cc=us";
        using var document = await http.GetJsonAsync(url, ct: ct).ConfigureAwait(false);
        if (document is null) return [];

        var candidates = new List<GameCandidate>();

        foreach (var item in document.RootElement.Array("items"))
        {
            // The endpoint also returns bundles and subscriptions, which cannot be added
            // as a shortcut target.
            if (item.String("type") != "app") continue;

            var id = item.Int("id");
            var name = item.String("name");
            if (id is null || string.IsNullOrWhiteSpace(name)) continue;

            candidates.Add(new GameCandidate
            {
                Source = Id,
                SourceId = id.Value.ToString(),
                Name = name,
                // Use the thumbnail the API actually returns rather than constructing one.
                // Newer apps live under hashed CDN paths that cannot be derived from the
                // app id, so a constructed URL 404s and the result row shows blank.
                CoverUrl = item.String("tiny_image") ?? SteamCdn.Cover(id.Value),
                Platforms = ReadPlatforms(item),
            });

            if (candidates.Count >= http.Options.MaxResults) break;
        }

        return await EnrichAsync(candidates, ct).ConfigureAwait(false);
    }

    private static List<string> ReadPlatforms(JsonElement item)
    {
        var platforms = new List<string>();
        var node = item.Prop("platforms");
        if (node is null) return platforms;

        if (node.Value.Bool("windows")) platforms.Add("Windows");
        if (node.Value.Bool("mac")) platforms.Add("macOS");
        if (node.Value.Bool("linux")) platforms.Add("Linux");

        return platforms;
    }

    /// <summary>
    /// Fills in developer, publisher and release date for the leading results. Failures
    /// are ignored: an un-enriched candidate is still perfectly usable, just less labelled.
    /// </summary>
    private async Task<IReadOnlyList<GameCandidate>> EnrichAsync(
        List<GameCandidate> candidates, CancellationToken ct)
    {
        var limit = Math.Min(http.Options.SteamEnrichmentLimit, candidates.Count);
        if (limit <= 0) return candidates;

        var details = await Task.WhenAll(
            candidates.Take(limit).Select(c => GetDetailsAsync(c.SourceId, ct))).ConfigureAwait(false);

        for (var i = 0; i < limit; i++)
        {
            if (details[i] is not { } detail) continue;

            candidates[i] = candidates[i] with
            {
                Developer = detail.Developer,
                Publisher = detail.Publisher,
                ReleaseDate = detail.ReleaseDate,
            };
        }

        return candidates;
    }

    private sealed record AppDetails(string? Developer, string? Publisher, DateOnly? ReleaseDate);

    private async Task<AppDetails?> GetDetailsAsync(string appId, CancellationToken ct)
    {
        var url = $"https://store.steampowered.com/api/appdetails?appids={appId}&l=english";
        using var document = await http.GetJsonAsync(url, ct: ct).ConfigureAwait(false);
        if (document is null) return null;

        // Shape is { "<appid>": { "success": true, "data": { ... } } }.
        var entry = document.RootElement.Prop(appId);
        if (entry is null || !entry.Value.Bool("success")) return null;

        var data = entry.Value.Prop("data");
        if (data is null) return null;

        return new AppDetails(
            data.Value.Strings("developers").FirstOrDefault(),
            data.Value.Strings("publishers").FirstOrDefault(),
            JsonExtensions.ParseReleaseDate(data.Value.Prop("release_date")?.String("date")));
    }
}
