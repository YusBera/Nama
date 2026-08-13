namespace Nama.Core.Normalization;

/// <summary>
/// The full output of normalizing one raw name. Nama keeps every stage rather than just
/// the final string, because identification searches candidates in order and the UI shows
/// the user what was detected.
/// </summary>
public sealed class NormalizationResult
{
    /// <summary>Exactly what came in, e.g. <c>ELDEN-RING-v1.12.2-FITGIRL</c>.</summary>
    public required string Raw { get; init; }

    /// <summary>Cleaned, human-presentable title, e.g. <c>Elden Ring</c>.</summary>
    public required string Normalized { get; init; }

    /// <summary>
    /// Search terms in descending order of confidence. The first entry is normally
    /// <see cref="Normalized"/>; later entries are progressively looser fallbacks.
    /// </summary>
    public required IReadOnlyList<string> Candidates { get; init; }

    /// <summary>Lowercased, punctuation-free, space-collapsed form used for fuzzy comparison.</summary>
    public required string MatchKey { get; init; }

    /// <summary>Tags that were stripped out — release groups, versions, noise words.</summary>
    public IReadOnlyList<string> RemovedTokens { get; init; } = [];

    /// <summary>True when the raw input contained Japanese/Chinese characters.</summary>
    public bool ContainsCjk { get; init; }

    /// <summary>True when nothing meaningful survived cleaning and Nama fell back to the raw name.</summary>
    public bool IsFallback { get; init; }

    public override string ToString() => Normalized;

    public static NormalizationResult Empty { get; } = new()
    {
        Raw = string.Empty,
        Normalized = string.Empty,
        Candidates = [],
        MatchKey = string.Empty,
    };
}
