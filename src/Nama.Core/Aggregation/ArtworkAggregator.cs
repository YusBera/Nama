using Nama.Core.Models;
using Nama.Core.Providers;

namespace Nama.Core.Aggregation;

/// <summary>One artwork type's worth of results, already ordered for display.</summary>
public sealed class ArtworkSection
{
    public required ArtworkType Type { get; init; }

    /// <summary>All artwork of this type, best first.</summary>
    public required IReadOnlyList<Artwork> Items { get; init; }

    /// <summary>The first five items — what the section shows before "Show more".</summary>
    public IReadOnlyList<Artwork> Recommended => Items.Take(ArtworkAggregator.RecommendedCount).ToList();

    public bool HasMore => Items.Count > ArtworkAggregator.RecommendedCount;

    public string Label => ArtworkTypeInfo.Label(Type);
}

/// <summary>Everything the artwork picker needs for one confirmed game.</summary>
public sealed class ArtworkCollection
{
    public required IReadOnlyList<ArtworkSection> Sections { get; init; }
    public IReadOnlyList<ProviderFailure> Failures { get; init; } = [];

    public bool IsEmpty => Sections.Count == 0;

    public ArtworkSection? this[ArtworkType type] => Sections.FirstOrDefault(s => s.Type == type);
}

/// <param name="Provider">Display name of the provider that failed.</param>
/// <param name="Message">Short, user-presentable reason.</param>
public readonly record struct ProviderFailure(string Provider, string Message);

/// <summary>
/// Merges artwork from every enabled provider into one normalized set, then ranks each
/// type so the first five results are genuinely the best five.
/// </summary>
public sealed class ArtworkAggregator(IEnumerable<IArtworkProvider> providers)
{
    /// <summary>How many results each section shows before the user expands it.</summary>
    public const int RecommendedCount = 5;

    private readonly IReadOnlyList<IArtworkProvider> _providers = providers.OrderBy(p => p.Priority).ToList();

    /// <summary>
    /// Fetches and ranks artwork for <paramref name="game"/> across all providers.
    /// Providers that fail are reported but never block the others.
    /// </summary>
    public async Task<ArtworkCollection> CollectAsync(
        Game game,
        IReadOnlyCollection<ArtworkType>? types = null,
        CancellationToken ct = default)
    {
        var wanted = types ?? ArtworkTypeInfo.SteamApplicable;

        var tasks = _providers
            .Where(p => p.IsEnabled && p.SupportedTypes.Intersect(wanted).Any())
            .Select(p => FetchAsync(p, game, wanted, ct))
            .ToList();

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);

        var all = new List<Artwork>();
        var failures = new List<ProviderFailure>();

        foreach (var (artwork, failure) in results)
        {
            all.AddRange(artwork);
            if (failure is { } f) failures.Add(f);
        }

        var sections = all
            .Where(a => !string.IsNullOrWhiteSpace(a.Url))
            .GroupBy(a => a.Type)
            .Select(g => new ArtworkSection
            {
                Type = g.Key,
                Items = Rank(Deduplicate(g), g.Key),
            })
            .Where(s => s.Items.Count > 0)
            .OrderBy(s => ArtworkTypeInfo.OrderIndex(s.Type))
            .ToList();

        return new ArtworkCollection { Sections = sections, Failures = failures };
    }

    private static async Task<(IReadOnlyList<Artwork> Artwork, ProviderFailure? Failure)> FetchAsync(
        IArtworkProvider provider,
        Game game,
        IReadOnlyCollection<ArtworkType> types,
        CancellationToken ct)
    {
        try
        {
            var supported = types.Intersect(provider.SupportedTypes).ToList();
            var artwork = await provider.GetArtworkAsync(game, supported, ct).ConfigureAwait(false);
            return (artwork, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var message = ex switch
            {
                HttpRequestException => "Could not reach the service.",
                TaskCanceledException => "The request timed out.",
                _ => ex.Message,
            };
            return ([], new ProviderFailure(provider.DisplayName, message));
        }
    }

    /// <summary>
    /// Orders one type's artwork so the top five are the best five. Ranking is a weighted
    /// blend rather than a strict sort, so a slightly lower-rated image at the correct
    /// aspect ratio can outrank a popular one that Steam would crop badly.
    /// </summary>
    private static IReadOnlyList<Artwork> Rank(IEnumerable<Artwork> items, ArtworkType type)
    {
        var list = items.ToList();
        if (list.Count == 0) return list;

        // Provider scores live on different scales (upvotes vs 0-100), so normalize
        // each provider's scores against its own maximum before comparing them.
        var maxByProvider = list
            .Where(a => a.Score.HasValue)
            .GroupBy(a => a.Source, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => Math.Max(g.Max(a => a.Score!.Value), 1.0), StringComparer.OrdinalIgnoreCase);

        var ideal = ArtworkTypeInfo.IdealAspect(type);

        return list
            .OrderByDescending(a => ScoreOf(a, type, ideal, maxByProvider))
            .ToList();
    }

    private static double ScoreOf(
        Artwork artwork,
        ArtworkType type,
        double idealAspect,
        IReadOnlyDictionary<string, double> maxByProvider)
    {
        // 1. Provider score / popularity, normalized per provider.
        var rating = 0.5;
        if (artwork.Score.HasValue && maxByProvider.TryGetValue(artwork.Source, out var max))
            rating = Math.Clamp(artwork.Score.Value / max, 0, 1);

        // 2. Aspect ratio fit. Steam crops hard, so a wrong ratio is a real defect.
        var aspectFit = 0.5;
        if (artwork.AspectRatio > 0 && idealAspect > 0)
        {
            var deviation = Math.Abs(Math.Log(artwork.AspectRatio / idealAspect));
            aspectFit = Math.Clamp(1.0 - (deviation * 1.6), 0, 1);
        }

        // 3. Resolution, on a log curve — 4x the pixels is better, but not 4x better.
        var pixels = (double)artwork.Width * artwork.Height;
        var resolution = pixels > 0
            ? Math.Clamp(Math.Log10(pixels / 20_000.0) / 2.2, 0, 1)
            : 0.35;

        var score = (rating * 0.45) + (aspectFit * 0.35) + (resolution * 0.20);

        // Animated artwork is only partially supported by Steam, so it sinks below stills.
        if (artwork.IsAnimated) score -= 0.30;

        // Logos must be transparent to look right; PNG is a decent proxy for that.
        if (type == ArtworkType.Logo && !artwork.Url.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            score -= 0.15;

        // Icons need to stay legible when small; very large icons are usually mislabeled art.
        if (type == ArtworkType.Icon && artwork.Width > 1024) score -= 0.10;

        return score;
    }

    /// <summary>
    /// Removes the same image arriving from several providers or several search terms.
    /// Keys on the URL, falling back to provider id.
    /// </summary>
    private static IEnumerable<Artwork> Deduplicate(IEnumerable<Artwork> items)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in items)
        {
            var key = string.IsNullOrWhiteSpace(item.Url) ? $"{item.Source}:{item.Id}" : item.Url;
            if (seen.Add(key)) yield return item;
        }
    }
}
