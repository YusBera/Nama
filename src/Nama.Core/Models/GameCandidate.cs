namespace Nama.Core.Models;

/// <summary>
/// A single search hit from one <see cref="Abstractions.IGameProvider"/>, mapped into
/// Nama's own shape. Providers never leak their raw API types past this point.
/// </summary>
public sealed record GameCandidate
{
    /// <summary>Provider source id, e.g. "steam", "vndb".</summary>
    public required string Source { get; init; }

    /// <summary>Identifier within that provider.</summary>
    public required string SourceId { get; init; }

    /// <summary>Primary title as the provider reports it.</summary>
    public required string Name { get; init; }

    /// <summary>Original-language title where the provider distinguishes it.</summary>
    public string? JapaneseName { get; init; }

    public IReadOnlyList<string> Aliases { get; init; } = [];

    public DateOnly? ReleaseDate { get; init; }

    public string? Developer { get; init; }

    public string? Publisher { get; init; }

    public IReadOnlyList<string> Platforms { get; init; } = [];

    /// <summary>Small cover shown in the result list.</summary>
    public string? CoverUrl { get; init; }

    /// <summary>
    /// Match confidence against the local name candidates, 0..1. Assigned by the
    /// identification pipeline, not by the provider.
    /// </summary>
    public double Confidence { get; init; }

    /// <summary>Every title string this candidate can be matched against.</summary>
    public IEnumerable<string> AllTitles()
    {
        yield return Name;
        if (!string.IsNullOrWhiteSpace(JapaneseName)) yield return JapaneseName;
        foreach (var alias in Aliases)
        {
            if (!string.IsNullOrWhiteSpace(alias)) yield return alias;
        }
    }
}
