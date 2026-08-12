using System.Text.Json;
using Nama.Core.Abstractions;
using Nama.Core.Models;

namespace Nama.Providers.Vndb;

/// <summary>
/// Visual novel lookup against VNDB's Kana API. No key required.
/// <para>
/// This is the provider that makes Nama useful for the library it was built for. A VN
/// almost never appears on the Steam store under the name its folder uses, and VNDB is the
/// only source here that returns the original Japanese title, the romaji title and the
/// official English title as separate fields — exactly the aliases fuzzy matching needs.
/// </para>
/// </summary>
public sealed class VndbProvider(ProviderHttp http) : IGameProvider, IArtworkProvider
{
    public const string Id = "vndb";

    private const string Endpoint = "https://api.vndb.org/kana/vn";
    private const string ReleaseEndpoint = "https://api.vndb.org/kana/release";

    /// <summary>Everything needed for both identification and artwork, in one round trip.</summary>
    private const string Fields =
        "id,title,alttitle,titles{title,lang,official},released,image{url,dims,sexual}," +
        "developers{name},screenshots{url,dims,sexual}";

    public string SourceId => Id;

    public string DisplayName => "VNDB";

    public bool IsAvailable => http.Options.EnableVndb;

    public int Priority => 90;

    public IReadOnlyCollection<ArtworkType> SupportedTypes { get; } = [ArtworkType.Cover, ArtworkType.Background];

    public bool CanResolve(GameRef game) => IsAvailable && game.Has(Id);

    public async Task<IReadOnlyList<GameCandidate>> SearchAsync(string query, CancellationToken ct = default)
    {
        if (!IsAvailable) return [];
        var document = await QueryAsync(["search", "=", query], ct).ConfigureAwait(false);
        if (document is null) return [];

        using (document)
        {
            var candidates = new List<GameCandidate>();

            foreach (var result in document.RootElement.Array("results"))
            {
                if (Map(result) is { } candidate) candidates.Add(candidate);
            }

            return candidates;
        }
    }

    public async Task<IReadOnlyList<Artwork>> GetArtworkAsync(GameRef game, CancellationToken ct = default)
    {
        if (!IsAvailable) return [];
        var vnId = game.GetId(Id);
        if (string.IsNullOrWhiteSpace(vnId)) return [];

        var document = await QueryAsync(["id", "=", vnId], ct).ConfigureAwait(false);
        if (document is null) return [];

        using (document)
        {
            var result = document.RootElement.Array("results").FirstOrDefault();
            if (result.ValueKind != JsonValueKind.Object) return [];

            var artwork = new List<Artwork>();

            // The package cover. Portrait, and usually close to the 600x900 Steam wants.
            if (result.Prop("image") is { } image && image.String("url") is { Length: > 0 } coverUrl)
            {
                var (width, height) = ReadDimensions(image);
                artwork.Add(new Artwork
                {
                    Id = $"vndb-{vnId}-cover",
                    Type = ArtworkType.Cover,
                    Url = coverUrl,
                    ThumbnailUrl = coverUrl,
                    Source = Id,
                    Width = width,
                    Height = height,
                    Score = 0.85,
                    IsNsfw = (image.Int("sexual") ?? 0) > 0,
                });
            }

            var index = 0;
            foreach (var screenshot in result.Array("screenshots"))
            {
                if (screenshot.String("url") is not { Length: > 0 } url) continue;

                var (width, height) = ReadDimensions(screenshot);
                artwork.Add(new Artwork
                {
                    Id = $"vndb-{vnId}-screenshot-{index++}",
                    Type = ArtworkType.Background,
                    Url = url,
                    ThumbnailUrl = url,
                    Source = Id,
                    Width = width,
                    Height = height,
                    // Screenshots are not designed as library art, so they rank below covers.
                    Score = 0.40,
                    IsNsfw = (screenshot.Int("sexual") ?? 0) > 0,
                });
            }

            await AddReleaseArtworkAsync(artwork, vnId, ct).ConfigureAwait(false);
            return artwork;
        }
    }

    private async Task AddReleaseArtworkAsync(List<Artwork> artwork, string vnId, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new
        {
            filters = new object[] { "vn", "=", new object[] { "id", "=", vnId } },
            fields = "images{id,url,dims,sexual,type}",
            results = Math.Clamp(http.Options.MaxResults, 1, 100),
        });

        var document = await http.PostJsonAsync(ReleaseEndpoint, body, ct: ct).ConfigureAwait(false);
        if (document is null) return;

        using (document)
        {
            foreach (var release in document.RootElement.Array("results"))
            foreach (var image in release.Array("images"))
            {
                if (image.String("url") is not { Length: > 0 } url) continue;
                var type = image.String("type");
                if (type is not ("pkgfront" or "dig")) continue;
                var (width, height) = ReadDimensions(image);
                artwork.Add(new Artwork
                {
                    Id = $"vndb-{image.String("id") ?? Guid.NewGuid().ToString("N")}",
                    Type = ArtworkType.Cover, Url = url, ThumbnailUrl = url, Source = Id,
                    Width = width, Height = height, Score = type == "pkgfront" ? 0.82 : 0.76,
                    IsNsfw = (image.Int("sexual") ?? 0) > 0,
                });
            }
        }
    }

    private async Task<JsonDocument?> QueryAsync(string[] filter, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new
        {
            filters = filter,
            fields = Fields,
            results = Math.Clamp(http.Options.MaxResults, 1, 100),
        });

        return await http.PostJsonAsync(Endpoint, body, ct: ct).ConfigureAwait(false);
    }

    private static (int Width, int Height) ReadDimensions(JsonElement node)
    {
        // "dims" is a two-element array, [width, height].
        var dims = node.Array("dims").ToArray();
        if (dims.Length < 2) return (0, 0);

        return (dims[0].TryGetInt32(out var w) ? w : 0, dims[1].TryGetInt32(out var h) ? h : 0);
    }

    private static GameCandidate? Map(JsonElement result)
    {
        var id = result.String("id");
        var title = result.String("title");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title)) return null;

        // "alttitle" is the original-language title when it differs from the main one;
        // when it is null the Japanese title has to come from the titles list.
        var japanese = result.String("alttitle") ?? FindTitle(result, "ja");

        var aliases = new List<string>();
        foreach (var entry in result.Array("titles"))
        {
            if (entry.String("title") is { Length: > 0 } value &&
                !string.Equals(value, title, StringComparison.OrdinalIgnoreCase) &&
                !aliases.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                aliases.Add(value);
            }
        }

        return new GameCandidate
        {
            Source = Id,
            SourceId = id,
            Name = title,
            JapaneseName = japanese,
            Aliases = aliases,
            ReleaseDate = JsonExtensions.ParseReleaseDate(result.String("released")),
            Developer = result.StringsFrom("developers", "name").FirstOrDefault(),
            CoverUrl = result.Prop("image")?.String("url"),
            Platforms = ["Visual Novel"],
        };
    }

    private static string? FindTitle(JsonElement result, string language)
    {
        foreach (var entry in result.Array("titles"))
        {
            if (entry.String("lang") == language) return entry.String("title");
        }

        return null;
    }
}
