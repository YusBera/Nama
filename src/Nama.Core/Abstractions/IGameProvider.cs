using Nama.Core.Models;

namespace Nama.Core.Abstractions;

/// <summary>
/// A source that can answer "what game is this title?". Implementations map their own
/// API shape into <see cref="GameCandidate"/> and never leak transport types.
/// </summary>
public interface IGameProvider
{
    /// <summary>Stable lowercase id, e.g. "steam", "vndb", "igdb". Used as the key in <see cref="GameRef.SourceIds"/>.</summary>
    string SourceId { get; }

    /// <summary>Human-readable label for source chips in the UI.</summary>
    string DisplayName { get; }

    /// <summary>
    /// False when a prerequisite is missing (typically an API key). Unavailable providers
    /// are skipped silently rather than throwing.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Relative trust when merging results from several providers, higher wins ties.
    /// </summary>
    int Priority { get; }

    /// <summary>
    /// Search by title. Must not throw for ordinary failures (network, rate limit, bad
    /// response) — return an empty list so one dead provider never blocks the flow.
    /// </summary>
    Task<IReadOnlyList<GameCandidate>> SearchAsync(string query, CancellationToken ct = default);
}
