namespace Nama.Core.Models;

/// <summary>How a name candidate was derived. Drives its search weight.</summary>
public enum NameCandidateKind
{
    /// <summary>Output of the full normalization pipeline. The primary search term.</summary>
    Normalized,

    /// <summary>Expansion of a known abbreviation, e.g. SubaHibi -> Subarashiki Hibi.</summary>
    AbbreviationExpansion,

    /// <summary>CamelCase run split into words, e.g. SubaHibiEN -> Suba Hibi.</summary>
    CamelSplit,

    /// <summary>Input with only the file extension removed. A safety net when normalization over-trims.</summary>
    RawStem,

    /// <summary>Japanese (or other CJK) text preserved verbatim.</summary>
    Cjk,

    /// <summary>Variant produced by keeping bracketed text that was dropped from the primary.</summary>
    BracketVariant,
}

/// <summary>A single searchable title derived from the raw input.</summary>
/// <param name="Value">The search term.</param>
/// <param name="Kind">How it was derived.</param>
/// <param name="Weight">Relative trust, 0..1. Multiplied into the fuzzy match score.</param>
public sealed record NameCandidate(string Value, NameCandidateKind Kind, double Weight);

/// <summary>
/// The full result of normalizing one raw name. Every intermediate is retained: the
/// pipeline is additive, so nothing that might matter for identification is ever lost.
/// </summary>
public sealed record NameAnalysis
{
    /// <summary>Exactly what came in.</summary>
    public required string Raw { get; init; }

    /// <summary>Primary cleaned title.</summary>
    public required string Normalized { get; init; }

    /// <summary>Title-cased form of <see cref="Normalized"/>, for showing to the user only.</summary>
    public required string DisplayName { get; init; }

    /// <summary>All searchable titles, highest weight first, de-duplicated.</summary>
    public IReadOnlyList<NameCandidate> Candidates { get; init; } = [];

    /// <summary>Tokens removed as noise. Kept for diagnostics and for explaining a bad match.</summary>
    public IReadOnlyList<string> RemovedTokens { get; init; } = [];

    /// <summary>True when the input contained Japanese script.</summary>
    public bool HasCjk { get; init; }

    public IEnumerable<string> CandidateValues => Candidates.Select(c => c.Value);
}
