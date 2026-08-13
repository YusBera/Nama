using Nama.Core.Models;
using Nama.Core.Normalization;
using Nama.Core.Providers;

namespace Nama.Core.Identification;

/// <summary>The outcome of identifying a local target.</summary>
public sealed class IdentificationResult
{
    public required LocalMetadata Local { get; init; }
    public required NormalizationResult Normalization { get; init; }

    /// <summary>Candidate games, best match first.</summary>
    public required IReadOnlyList<Game> Candidates { get; init; }

    /// <summary>Providers that failed, so the UI can warn without blocking the flow.</summary>
    public IReadOnlyList<ProviderFailure> Failures { get; init; } = [];

    /// <summary>The top candidate, when it scored well enough to preselect.</summary>
    public Game? BestMatch => Candidates.Count > 0 && Candidates[0].Confidence >= 0.62 ? Candidates[0] : null;

    /// <summary>
    /// True when the UI can preselect the top result. Nama still asks the user to confirm.
    ///
    /// Two ways to qualify. A near-exact title match stands on its own, because a game
    /// and its sequel legitimately score close together — "Steins Gate 0" matches
    /// STEINS;GATE 0 perfectly while STEINS;GATE trails only slightly, and demanding a
    /// wide margin there would refuse to preselect the most certain case there is.
    /// Otherwise the top result must be strong *and* clearly ahead of the runner-up.
    /// </summary>
    public bool IsConfident
    {
        get
        {
            if (Candidates.Count == 0) return false;

            var top = Candidates[0];

            // The blended confidence is deliberately dragged down by the raw local hints,
            // which still carry version and release-group noise, so it tops out well
            // short of 1.0 even for a perfect match. Ask the title question directly.
            if (FuzzyMatcher.BestSimilarity(Normalization.Normalized, top.AllTitles()) >= 0.98)
                return true;

            if (top.Confidence < 0.80) return false;

            return Candidates.Count == 1 || top.Confidence - Candidates[1].Confidence >= 0.12;
        }
    }
}

/// <param name="Provider">Display name of the provider that failed.</param>
/// <param name="Message">Short, user-presentable reason.</param>
public readonly record struct ProviderFailure(string Provider, string Message);

/// <summary>
/// Runs the identification pipeline: local metadata to normalized candidates to
/// provider searches to a ranked, de-duplicated candidate list.
/// </summary>
public sealed class GameIdentifier(
    IEnumerable<IGameProvider> providers,
    NameNormalizer? normalizer = null)
{
    private readonly IReadOnlyList<IGameProvider> _providers = providers.OrderBy(p => p.Priority).ToList();
    private readonly NameNormalizer _normalizer = normalizer ?? new NameNormalizer();

    /// <summary>How many normalized candidates get their own round of provider searches.</summary>
    private const int MaxSearchTerms = 3;

    /// <summary>Identifies the game at <paramref name="path"/>.</summary>
    public async Task<IdentificationResult> IdentifyAsync(string path, CancellationToken ct = default)
    {
        var extractor = new LocalMetadataExtractor();
        var local = extractor.Extract(path);
        return await IdentifyAsync(local, ct).ConfigureAwait(false);
    }

    /// <summary>Identifies a target whose local metadata has already been read.</summary>
    public async Task<IdentificationResult> IdentifyAsync(LocalMetadata local, CancellationToken ct = default)
    {
        var normalization = _normalizer.Normalize(local.PrimaryRawName);

        // Search the normalized candidates, plus the normalization of the next-best hint —
        // when the executable name is junk the folder name often carries the real title.
        var terms = new List<string>(normalization.Candidates.Take(MaxSearchTerms));

        foreach (var hint in local.Hints.Skip(1).Take(2))
        {
            var alternative = _normalizer.Normalize(hint.Value);
            if (alternative.Normalized.Length >= 2 &&
                !terms.Contains(alternative.Normalized, StringComparer.OrdinalIgnoreCase))
                terms.Add(alternative.Normalized);
        }

        var (games, failures) = await SearchAllAsync(terms, ct).ConfigureAwait(false);
        var ranked = RankCandidates(games, normalization, local);

        return new IdentificationResult
        {
            Local = local,
            Normalization = normalization,
            Candidates = ranked,
            Failures = failures,
        };
    }

    /// <summary>
    /// Searches every enabled provider for a user-typed query. Used by the manual
    /// correction box on the identification screen.
    /// </summary>
    public async Task<IReadOnlyList<Game>> SearchAsync(string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];

        var (games, _) = await SearchAllAsync([query.Trim()], ct).ConfigureAwait(false);

        var merged = MergeDuplicates(games);
        foreach (var game in merged)
            game.Confidence = FuzzyMatcher.BestSimilarity(query, game.AllTitles());

        return merged.OrderByDescending(g => g.Confidence).ToList();
    }

    /// <summary>
    /// Fans out every term across every enabled provider concurrently. A provider that
    /// throws is recorded as a failure and the rest of the results are still used.
    /// </summary>
    private async Task<(List<Game> Games, List<ProviderFailure> Failures)> SearchAllAsync(
        IReadOnlyList<string> terms,
        CancellationToken ct)
    {
        var enabled = _providers.Where(p => p.IsEnabled).ToList();
        var tasks = new List<Task<(IReadOnlyList<Game> Games, ProviderFailure? Failure)>>();

        foreach (var provider in enabled)
        {
            foreach (var term in terms.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                tasks.Add(SearchOneAsync(provider, term, ct));
            }
        }

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);

        var games = new List<Game>();
        var failures = new List<ProviderFailure>();

        foreach (var (providerGames, failure) in results)
        {
            games.AddRange(providerGames);

            // One failure message per provider, not one per search term.
            if (failure is { } f && !failures.Any(existing => existing.Provider == f.Provider))
                failures.Add(f);
        }

        return (games, failures);
    }

    private static async Task<(IReadOnlyList<Game> Games, ProviderFailure? Failure)> SearchOneAsync(
        IGameProvider provider,
        string term,
        CancellationToken ct)
    {
        try
        {
            var games = await provider.SearchAsync(term, ct).ConfigureAwait(false);
            return (games, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ([], new ProviderFailure(provider.DisplayName, Summarize(ex)));
        }
    }

    private static string Summarize(Exception ex) => ex switch
    {
        HttpRequestException => "Could not reach the service.",
        TaskCanceledException => "The request timed out.",
        _ => ex.Message,
    };

    /// <summary>
    /// Scores and orders candidates. Confidence blends title similarity against the
    /// normalized name with a smaller signal from the raw local hints, so a game that
    /// matches the folder name as well as the exe name ranks higher.
    /// </summary>
    private static IReadOnlyList<Game> RankCandidates(
        List<Game> games,
        NormalizationResult normalization,
        LocalMetadata local)
    {
        var merged = MergeDuplicates(games);

        foreach (var game in merged)
        {
            var titles = game.AllTitles().ToList();

            // Primary signal: the cleaned title.
            var primary = FuzzyMatcher.BestSimilarity(normalization.Normalized, titles);

            // Secondary signal: any other normalized candidate term.
            var secondary = normalization.Candidates
                .Skip(1)
                .Select(c => FuzzyMatcher.BestSimilarity(c, titles))
                .DefaultIfEmpty(0)
                .Max();

            // Tertiary signal: agreement with the local hints, weighted by hint trust.
            var hintScore = local.Hints
                .Select(h => FuzzyMatcher.BestSimilarity(h.Value, titles) * h.Weight)
                .DefaultIfEmpty(0)
                .Max();

            var confidence = (primary * 0.65) + (secondary * 0.15) + (hintScore * 0.20);

            // Corroboration bonus: several independent providers naming the same game
            // is strong evidence, so long as the title already matches reasonably.
            if (game.SourceIds.Count > 1 && confidence > 0.45)
                confidence += Math.Min(0.08, 0.04 * (game.SourceIds.Count - 1));

            game.Confidence = Math.Clamp(confidence, 0, 1);
        }

        return merged
            .Where(g => g.Confidence > 0.20)
            .OrderByDescending(g => g.Confidence)
            .ThenByDescending(g => g.SourceIds.Count)
            .Take(40)
            .ToList();
    }

    /// <summary>
    /// Collapses the same game reported by several providers into one candidate,
    /// keeping every source id so artwork can be fetched from all of them.
    /// </summary>
    private static List<Game> MergeDuplicates(List<Game> games)
    {
        var merged = new List<Game>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var game in games)
        {
            var key = NameNormalizer.BuildMatchKey(game.CanonicalName);
            if (key.Length == 0) continue;

            if (index.TryGetValue(key, out var existing))
            {
                merged[existing] = merged[existing].MergeWith(game);
                continue;
            }

            // Near-duplicate check for titles that differ only by edition wording.
            var nearIndex = -1;
            for (var i = 0; i < merged.Count; i++)
            {
                if (FuzzyMatcher.Similarity(merged[i].CanonicalName, game.CanonicalName) >= 0.96)
                {
                    nearIndex = i;
                    break;
                }
            }

            if (nearIndex >= 0)
            {
                merged[nearIndex] = merged[nearIndex].MergeWith(game);
                continue;
            }

            index[key] = merged.Count;
            merged.Add(game);
        }

        return merged;
    }
}
