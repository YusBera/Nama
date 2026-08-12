using Nama.Core.Aggregation;
using Nama.Core.Models;
using Nama.Core.Normalization;

namespace Nama.Core.Identification;

/// <summary>What Nama thinks the selected file is.</summary>
public sealed class IdentificationResult
{
    public required ExtractionResult Extraction { get; init; }

    /// <summary>Provider matches, best first, each with <see cref="GameCandidate.Confidence"/> set.</summary>
    public required IReadOnlyList<GameCandidate> Matches { get; init; }

    public required IReadOnlyList<string> FailedProviders { get; init; }

    /// <summary>The queries actually sent, for diagnosing a bad result.</summary>
    public required IReadOnlyList<string> QueriesUsed { get; init; }

    public GameCandidate? Best => Matches.Count > 0 ? Matches[0] : null;

    /// <summary>
    /// True when the leading match is both strong and clearly ahead of the runner-up.
    /// Pre-selects it in the UI — it never skips confirmation, which the spec requires.
    /// </summary>
    public bool IsConfident =>
        Best is { Confidence: >= 0.82 } &&
        (Matches.Count < 2 || Best.Confidence - Matches[1].Confidence >= 0.08);

    /// <summary>The name to put in the Steam name box before the user edits it.</summary>
    public string SuggestedDisplayName => Best?.Name ?? Extraction.BestGuess;
}

/// <summary>
/// Runs the whole identification pipeline: path → local names → provider searches → scored
/// matches.
/// <para>
/// The user always confirms. Confidence exists to put the right row first, not to skip the
/// question.
/// </para>
/// </summary>
public sealed class GameIdentifier(
    CandidateExtractor extractor,
    GameSearchAggregator search,
    NameNormalizer? normalizer = null)
{
    private readonly NameNormalizer _normalizer = normalizer ?? new NameNormalizer();

    /// <summary>How many local candidates get their own provider search.</summary>
    private const int MaxQueries = 3;

    public async Task<IdentificationResult> IdentifyAsync(string path, CancellationToken ct = default)
    {
        var extraction = extractor.Extract(path);

        // Distinct search terms, strongest first. Searching every variant would multiply
        // provider calls for very little gain.
        var queries = extraction.Candidates
            .Select(c => c.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxQueries)
            .ToList();

        var results = await search.SearchManyAsync(queries, ct).ConfigureAwait(false);

        return new IdentificationResult
        {
            Extraction = extraction,
            Matches = Rank(results.Candidates, extraction.Candidates),
            FailedProviders = results.FailedProviders,
            QueriesUsed = queries,
        };
    }

    /// <summary>
    /// Manual correction from the search box. Scores against what the user typed rather
    /// than the filename, since they are overriding exactly that.
    /// </summary>
    public async Task<IReadOnlyList<GameCandidate>> SearchAsync(string query, CancellationToken ct = default)
    {
        var results = await search.SearchAsync(query, ct).ConfigureAwait(false);
        var analysis = _normalizer.Normalize(query);

        var local = analysis.Candidates
            .Select(c => new LocalNameCandidate(c.Value, CandidateOrigin.ExecutableName, c.Weight, query))
            .ToList();

        return Rank(results.Candidates, local);
    }

    /// <summary>
    /// Scores every provider result against every local guess and sorts.
    /// <para>
    /// The score is the best single pairing found, not an average: one strong agreement
    /// between any local name and any of the game's aliases is the signal, and averaging
    /// would drown it in the weaker candidates that exist precisely because no single
    /// source is reliable.
    /// </para>
    /// </summary>
    public static IReadOnlyList<GameCandidate> Rank(
        IEnumerable<GameCandidate> candidates, IReadOnlyList<LocalNameCandidate> localNames)
    {
        var scored = new List<GameCandidate>();

        foreach (var candidate in candidates)
        {
            var titles = candidate.AllTitles().ToList();
            var best = 0.0;

            foreach (var local in localNames)
            {
                // Weighting by source keeps a lucky hit on a weak signal (an engine
                // executable name) from outranking a solid hit on the folder.
                var score = FuzzyMatcher.BestSimilarity(local.Value, titles) * local.Weight;
                if (score > best) best = score;
            }

            scored.Add(candidate with { Confidence = Math.Clamp(best, 0.0, 1.0) });
        }

        return scored
            .OrderByDescending(c => c.Confidence)
            // A dated release is more likely to be the real game than a bare store entry.
            .ThenByDescending(c => c.ReleaseDate.HasValue)
            .ThenBy(c => c.Name.Length)
            .ToList();
    }
}
