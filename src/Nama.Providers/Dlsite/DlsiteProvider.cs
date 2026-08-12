using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Nama.Core.Abstractions;
using Nama.Core.Models;

namespace Nama.Providers.Dlsite;

/// <summary>
/// Resolves an exact DLsite product code found in a local folder or filename.
/// DLsite does not publish a supported developer API, so this provider intentionally
/// avoids title search and crawling: one cached, fail-soft product lookup is all it does.
/// </summary>
public sealed partial class DlsiteProvider(ProviderHttp http) : IGameProvider, IArtworkProvider
{
    public const string Id = "dlsite";
    private const string Endpoint = "https://www.dlsite.com/maniax/api/=/product.json?workno=";

    public string SourceId => Id;
    public string DisplayName => "DLsite";
    public bool IsAvailable => http.Options.EnableDlsite;
    public int Priority => 95;
    public IReadOnlyCollection<ArtworkType> SupportedTypes { get; } = [ArtworkType.Cover, ArtworkType.Background];

    public bool CanResolve(GameRef game) => IsAvailable && game.Has(Id);

    public async Task<IReadOnlyList<GameCandidate>> SearchAsync(string query, CancellationToken ct = default)
    {
        if (!IsAvailable) return [];
        var code = ExtractCode(query);
        if (code is null) return [];

        var product = await GetProductAsync(code, ct).ConfigureAwait(false);
        if (product is null) return [];

        using (product)
        {
            var root = ProductRoot(product.RootElement);
            if (root.ValueKind != JsonValueKind.Object ||
                root.String("work_name") is not { Length: > 0 } name) return [];

            return [new GameCandidate
            {
                Source = Id,
                SourceId = root.String("workno") ?? code,
                Name = name,
                JapaneseName = root.String("work_name_kana"),
                Aliases = Aliases(root, name),
                Developer = root.String("maker_name_en") ?? root.String("maker_name"),
                ReleaseDate = ParseDate(root.String("regist_date")),
                CoverUrl = NormalizeUrl(root.Prop("image_main")?.String("url")),
                Platforms = root.Array("platform").Select(x => x.GetString())
                    .Where(x => !string.IsNullOrWhiteSpace(x)).Select(PlatformName).Distinct().ToArray()!,
            }];
        }
    }

    public async Task<IReadOnlyList<Artwork>> GetArtworkAsync(GameRef game, CancellationToken ct = default)
    {
        if (!IsAvailable) return [];
        var code = ExtractCode(game.GetId(Id) ?? string.Empty);
        if (code is null) return [];

        var product = await GetProductAsync(code, ct).ConfigureAwait(false);
        if (product is null) return [];

        using (product)
        {
            var root = ProductRoot(product.RootElement);
            if (root.ValueKind != JsonValueKind.Object) return [];

            var artwork = new List<Artwork>();
            if (root.Prop("image_main") is { } main) AddImage(artwork, main, code, "main", ArtworkType.Cover, 0.88);

            var index = 0;
            foreach (var sample in root.Array("image_samples"))
                AddImage(artwork, sample, code, $"sample-{index++}", ArtworkType.Background, 0.55);

            return artwork;
        }
    }

    private Task<JsonDocument?> GetProductAsync(string code, CancellationToken ct) =>
        http.GetJsonAsync($"{Endpoint}{Uri.EscapeDataString(code)}&locale=en_US", ct: ct);

    private static string? ExtractCode(string value)
    {
        var match = ProductCodeRegex().Match(value);
        return match.Success ? match.Value.ToUpperInvariant() : null;
    }

    private static JsonElement ProductRoot(JsonElement root) =>
        root.ValueKind == JsonValueKind.Array ? root.EnumerateArray().FirstOrDefault() : root;

    private static IReadOnlyList<string> Aliases(JsonElement root, string name) =>
        new[] { root.String("product_name"), root.String("alt_name"), root.String("work_name_masked") }
            .Where(x => !string.IsNullOrWhiteSpace(x) && !string.Equals(x, name, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).Cast<string>().ToArray();

    private static DateOnly? ParseDate(string? value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? DateOnly.FromDateTime(date) : null;

    private static string PlatformName(string? platform) => platform?.ToLowerInvariant() switch
    {
        "pc" => "Windows",
        "smartphone" => "Mobile",
        "play" => "DLsite Play",
        _ => platform ?? string.Empty,
    };

    private static void AddImage(List<Artwork> output, JsonElement image, string code, string suffix,
        ArtworkType type, double score)
    {
        var url = NormalizeUrl(image.String("url"));
        if (url is null) return;

        output.Add(new Artwork
        {
            Id = $"dlsite-{code}-{suffix}", Type = type, Url = url,
            ThumbnailUrl = url, Source = Id,
            Width = ParseInt(image.String("width")), Height = ParseInt(image.String("height")), Score = score,
        });
    }

    private static string? NormalizeUrl(string? url) => url is { Length: > 0 }
        ? url.StartsWith("//", StringComparison.Ordinal) ? $"https:{url}" : url
        : null;

    private static int ParseInt(string? value) => int.TryParse(value, out var result) ? result : 0;

    [GeneratedRegex(@"(?<![A-Z0-9])(?:RJ|VJ|BJ)\d{6,10}(?![A-Z0-9])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ProductCodeRegex();
}
