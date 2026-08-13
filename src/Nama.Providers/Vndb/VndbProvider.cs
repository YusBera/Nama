using System.Text.Json.Serialization;
using Nama.Core.Models;
using Nama.Core.Providers;
using Nama.Storage;

namespace Nama.Providers.Vndb;

/// <summary>
/// VNDB, via the public Kana API. This is what makes Nama work for visual novels,
/// which Steam and SteamGridDB frequently do not list at all — and it is the only
/// provider that reliably supplies Japanese titles and romaji aliases.
///
/// No credentials are required for the queries Nama makes.
/// </summary>
public sealed class VndbProvider(HttpClient httpClient, SearchCache cache)
    : IGameProvider, IArtworkProvider
{
    private const string ApiBase = "https://api.vndb.org/kana";

    private const string Fields =
        "title, alttitle, titles.title, titles.lang, titles.official, titles.main, " +
        "aliases, released, description, " +
        "image.url, image.dims, image.sexual, " +
        "screenshots.url, screenshots.dims, screenshots.sexual, " +
        "developers.name, developers.original";

    public string Id => "vndb";
    public string DisplayName => "VNDB";
    public bool IsEnabled { get; set; } = true;
    public int Priority => 30;

    /// <summary>VNDB supplies package art and promotional screenshots, not Steam-shaped capsules.</summary>
    public IReadOnlyCollection<ArtworkType> SupportedTypes { get; } =
    [
        ArtworkType.Cover, ArtworkType.Background,
    ];

    public async Task<IReadOnlyList<Game>> SearchAsync(string query, CancellationToken ct = default)
    {
        if (!IsEnabled || string.IsNullOrWhiteSpace(query)) return [];

        var response = await cache.GetOrAddAsync(
            Id,
            query,
            token => ProviderHttp.PostJsonAsync<VnQuery, VnResponse>(
                httpClient,
                $"{ApiBase}/vn",
                new VnQuery
                {
                    Filters = ["search", "=", query],
                    Fields = Fields,
                    Results = 12,
                    Sort = "searchrank",
                },
                token),
            ct).ConfigureAwait(false);

        if (response?.Results is not { Count: > 0 } results) return [];

        return results.Select(ToGame).ToList();
    }

    private Game ToGame(VnResult result)
    {
        // VNDB's "title" is the romaji/latin form, "alttitle" the original script.
        var canonical = string.IsNullOrWhiteSpace(result.Title) ? result.AltTitle : result.Title;

        var aliases = new List<string>();

        if (result.Titles is not null)
            foreach (var title in result.Titles)
                if (!string.IsNullOrWhiteSpace(title.Title))
                    aliases.Add(title.Title!);

        if (result.Aliases is not null)
            aliases.AddRange(result.Aliases.Where(a => !string.IsNullOrWhiteSpace(a))!);

        return new Game
        {
            CanonicalName = (canonical ?? "Unknown").Trim(),
            DisplayName = (canonical ?? "Unknown").Trim(),
            JapaneseName = FindJapaneseTitle(result),
            Aliases = aliases.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            ReleaseDate = ParseDate(result.Released),
            Developer = result.Developers?.FirstOrDefault()?.Name,
            Summary = Trim(result.Description),
            PreviewImageUrl = result.Image?.Url,
            SourceIds = [new GameSourceId(Id, result.Id ?? string.Empty)],
        };
    }

    /// <summary>Prefers the official Japanese title, falling back to <c>alttitle</c>.</summary>
    private static string? FindJapaneseTitle(VnResult result)
    {
        var japanese = result.Titles?.FirstOrDefault(t =>
            string.Equals(t.Lang, "ja", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(t.Title));

        return japanese?.Title ?? result.AltTitle;
    }

    public async Task<IReadOnlyList<Artwork>> GetArtworkAsync(
        Game game,
        IReadOnlyCollection<ArtworkType> types,
        CancellationToken ct = default)
    {
        if (!IsEnabled) return [];

        var vnId = game.SourceFor(Id)?.Id;
        VnResult? result;

        if (!string.IsNullOrWhiteSpace(vnId))
        {
            var response = await cache.GetOrAddAsync(
                $"{Id}-byid",
                vnId,
                token => ProviderHttp.PostJsonAsync<VnQuery, VnResponse>(
                    httpClient,
                    $"{ApiBase}/vn",
                    new VnQuery { Filters = ["id", "=", vnId], Fields = Fields, Results = 1 },
                    token),
                ct).ConfigureAwait(false);

            result = response?.Results?.FirstOrDefault();
        }
        else
        {
            // The game was identified elsewhere; try to find it here by title so VN
            // package art is still offered.
            var matches = await SearchAsync(game.CanonicalName, ct).ConfigureAwait(false);
            var best = matches.FirstOrDefault();
            if (best is null) return [];

            return await GetArtworkAsync(best, types, ct).ConfigureAwait(false);
        }

        if (result is null) return [];

        var artwork = new List<Artwork>();

        if (types.Contains(ArtworkType.Cover) && result.Image is { Url: not null } image)
        {
            artwork.Add(new Artwork
            {
                Id = $"vndb-cover-{result.Id}",
                Type = ArtworkType.Cover,
                Url = image.Url,
                ThumbnailUrl = image.Url,
                Source = DisplayName,
                Width = image.Dims is { Length: 2 } ? image.Dims[0] : 0,
                Height = image.Dims is { Length: 2 } ? image.Dims[1] : 0,
                // The official package art is the canonical cover for a VN, so it should
                // sit at the top of the cover section.
                Score = 1.0,
                Author = "Official",
                Style = "package",
            });
        }

        if (types.Contains(ArtworkType.Background) && result.Screenshots is not null)
        {
            var index = 0;
            foreach (var screenshot in result.Screenshots.Take(30))
            {
                if (string.IsNullOrWhiteSpace(screenshot.Url)) continue;

                artwork.Add(new Artwork
                {
                    Id = $"vndb-shot-{result.Id}-{index++}",
                    Type = ArtworkType.Background,
                    Url = screenshot.Url!,
                    ThumbnailUrl = screenshot.Thumbnail ?? screenshot.Url,
                    Source = DisplayName,
                    Width = screenshot.Dims is { Length: 2 } ? screenshot.Dims[0] : 0,
                    Height = screenshot.Dims is { Length: 2 } ? screenshot.Dims[1] : 0,
                    // Screenshots are supporting material, ranked below package art.
                    Score = 0.4,
                    Style = "screenshot",
                });
            }
        }

        return artwork;
    }

    private static DateOnly? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        // VNDB uses "TBA" and partial dates such as "2011" or "2011-12".
        if (DateOnly.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var parsed))
            return parsed;

        var parts = value.Split('-');
        if (int.TryParse(parts[0], out var year) && year is > 1970 and < 2100)
        {
            var month = parts.Length > 1 && int.TryParse(parts[1], out var m) && m is >= 1 and <= 12 ? m : 1;
            return new DateOnly(year, month, 1);
        }

        return null;
    }

    private static string? Trim(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return null;

        // VNDB descriptions use BBCode-ish markup that would look wrong in the UI.
        var cleaned = System.Text.RegularExpressions.Regex.Replace(description, @"\[/?[a-z]+[^\]]*\]", string.Empty);
        cleaned = cleaned.Replace("\n", " ").Trim();

        return cleaned.Length > 300 ? cleaned[..297] + "..." : cleaned;
    }

    private sealed class VnQuery
    {
        [JsonPropertyName("filters")]
        public object[]? Filters { get; set; }

        [JsonPropertyName("fields")]
        public string? Fields { get; set; }

        [JsonPropertyName("results")]
        public int Results { get; set; } = 10;

        [JsonPropertyName("sort")]
        public string? Sort { get; set; }
    }

    private sealed class VnResponse
    {
        public List<VnResult>? Results { get; set; }
        public bool More { get; set; }
    }

    private sealed class VnResult
    {
        public string? Id { get; set; }
        public string? Title { get; set; }

        [JsonPropertyName("alttitle")]
        public string? AltTitle { get; set; }

        public List<VnTitle>? Titles { get; set; }
        public List<string>? Aliases { get; set; }
        public string? Released { get; set; }
        public string? Description { get; set; }
        public VnImage? Image { get; set; }
        public List<VnImage>? Screenshots { get; set; }
        public List<VnDeveloper>? Developers { get; set; }
    }

    private sealed class VnTitle
    {
        public string? Lang { get; set; }
        public string? Title { get; set; }
        public bool Official { get; set; }
        public bool Main { get; set; }
    }

    private sealed class VnImage
    {
        public string? Url { get; set; }
        public string? Thumbnail { get; set; }
        public int[]? Dims { get; set; }
        public double Sexual { get; set; }
    }

    private sealed class VnDeveloper
    {
        public string? Name { get; set; }
        public string? Original { get; set; }
    }
}
