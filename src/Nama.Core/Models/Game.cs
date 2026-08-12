namespace Nama.Core.Models;

/// <summary>
/// A game the user has confirmed. Provider-independent: everything downstream
/// (artwork aggregation, the Steam writer, the UI) works against this, never against a
/// provider response.
/// </summary>
public sealed record Game
{
    /// <summary>Best-known true title, used as the default Steam display name.</summary>
    public required string CanonicalName { get; init; }

    /// <summary>
    /// What actually gets written to Steam. Starts as <see cref="CanonicalName"/> and is
    /// user-editable before the shortcut is created.
    /// </summary>
    public required string DisplayName { get; init; }

    public string? JapaneseName { get; init; }

    public IReadOnlyList<string> Aliases { get; init; } = [];

    public DateOnly? ReleaseDate { get; init; }

    public string? Developer { get; init; }

    public string? Publisher { get; init; }

    public IReadOnlyList<string> Platforms { get; init; } = [];

    /// <summary>Provider ids kept so artwork can be resolved from every source that knows this game.</summary>
    public required GameRef Ref { get; init; }

    /// <summary>Identification confidence, 0..1.</summary>
    public double Confidence { get; init; }

    /// <summary>Aggregated artwork from every enabled provider, un-ranked.</summary>
    public IReadOnlyList<Artwork> Artwork { get; init; } = [];

    public static Game FromCandidate(GameCandidate candidate) => new()
    {
        CanonicalName = candidate.Name,
        DisplayName = candidate.Name,
        JapaneseName = candidate.JapaneseName,
        Aliases = candidate.Aliases,
        ReleaseDate = candidate.ReleaseDate,
        Developer = candidate.Developer,
        Publisher = candidate.Publisher,
        Platforms = candidate.Platforms,
        Confidence = candidate.Confidence,
        Ref = new GameRef(
            [new KeyValuePair<string, string>(candidate.Source, candidate.SourceId)],
            candidate.Name,
            candidate.JapaneseName),
    };
}
