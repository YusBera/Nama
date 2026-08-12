using Nama.Core.Abstractions;
using Nama.Core.Models;

namespace Nama.Core.Aggregation;

/// <summary>Search results merged across providers.</summary>
public sealed class SearchResults
{
    public required IReadOnlyList<GameCandidate> Candidates { get; init; }

    public required IReadOnlyList<string> FailedProviders { get; init; }

    public static SearchResults Empty { get; } = new() { Candidates = [], FailedProviders = [] };
}

/// <summary>
/// Queries every available game provider at once and merges the hits.
/// <para>
/// Ordering here is provider priority only. Relevance ranking against the local name
/// candidates is the identification pipeline's job — this layer deliberately knows nothing
/// about the file the user picked.
/// </para>
/// </summary>
public sealed class GameSearchAggregator(IEnumerable<IGameProvider> providers)
{
    private readonly IReadOnlyList<IGameProvider> _providers = providers.ToList();

    public async Task<SearchResults> SearchAsync(string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return SearchResults.Empty;

        var failed = new List<string>();
        var available = _providers.Where(p => p.IsAvailable).ToList();

        var results = await Task.WhenAll(available.Select(async provider =>
        {
            try
            {
                return (Provider: provider, Candidates: await provider.SearchAsync(query, ct).ConfigureAwait(false));
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                lock (failed) failed.Add(provider.DisplayName);
                return (Provider: provider, Candidates: (IReadOnlyList<GameCandidate>)[]);
            }
        })).ConfigureAwait(false);

        var merged = results
            .OrderByDescending(r => r.Provider.Priority)
            .SelectMany(r => r.Candidates)
            .ToList();

        return new SearchResults { Candidates = merged, FailedProviders = failed };
    }

    /// <summary>Searches several terms and merges, dropping duplicates of the same provider entry.</summary>
    public async Task<SearchResults> SearchManyAsync(IEnumerable<string> queries, CancellationToken ct = default)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<GameCandidate>();
        var failed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var query in queries)
        {
            var results = await SearchAsync(query, ct).ConfigureAwait(false);

            foreach (var name in results.FailedProviders) failed.Add(name);

            foreach (var candidate in results.Candidates)
            {
                if (seen.Add($"{candidate.Source}:{candidate.SourceId}")) candidates.Add(candidate);
            }
        }

        return new SearchResults { Candidates = candidates, FailedProviders = failed.ToList() };
    }
}
