namespace Nama.Core.Models;

/// <summary>
/// Provider-independent representation of a game. Every provider maps its own
/// API response into this shape so that nothing downstream (identification,
/// artwork aggregation, UI) needs to know where the data came from.
/// </summary>
public sealed class Game
{
    /// <summary>Best-known canonical title, used for matching and as the default Steam name.</summary>
    public required string CanonicalName { get; init; }

    /// <summary>Title shown in the UI. Defaults to <see cref="CanonicalName"/>.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Original-language (typically Japanese) title, when the provider exposes one.</summary>
    public string? JapaneseName { get; init; }

    /// <summary>Alternate titles, romanizations, abbreviations and regional names.</summary>
    public IReadOnlyList<string> Aliases { get; init; } = [];

    public DateOnly? ReleaseDate { get; init; }
    public string? Developer { get; init; }
    public string? Publisher { get; init; }
    public IReadOnlyList<string> Platforms { get; init; } = [];

    /// <summary>Short blurb, when available. Purely cosmetic.</summary>
    public string? Summary { get; init; }

    /// <summary>Small image used in the candidate list. Not part of the artwork picker.</summary>
    public string? PreviewImageUrl { get; init; }

    /// <summary>
    /// Identifiers on each provider that knows this game, so a confirmed match can be
    /// resolved back to its origin when fetching artwork.
    /// </summary>
    public IReadOnlyList<GameSourceId> SourceIds { get; init; } = [];

    /// <summary>Match confidence in [0,1], assigned during identification rather than by the provider.</summary>
    public double Confidence { get; set; }

    public string EffectiveDisplayName =>
        string.IsNullOrWhiteSpace(DisplayName) ? CanonicalName : DisplayName;

    /// <summary>Every title this game is known by, de-duplicated, for fuzzy scoring.</summary>
    public IEnumerable<string> AllTitles()
    {
        yield return CanonicalName;
        if (!string.IsNullOrWhiteSpace(DisplayName)) yield return DisplayName;
        if (!string.IsNullOrWhiteSpace(JapaneseName)) yield return JapaneseName!;
        foreach (var alias in Aliases)
            if (!string.IsNullOrWhiteSpace(alias))
                yield return alias;
    }

    /// <summary>
    /// This game's identity on one provider, or null when that provider does not know it.
    /// Written as an explicit loop rather than <c>FirstOrDefault</c>: <see cref="GameSourceId"/>
    /// is a struct, so the LINQ form would return a default value that is never null.
    /// </summary>
    public GameSourceId? SourceFor(string providerId)
    {
        foreach (var source in SourceIds)
            if (string.Equals(source.Provider, providerId, StringComparison.OrdinalIgnoreCase))
                return source;

        return null;
    }

    public string? Year => ReleaseDate?.Year.ToString();

    /// <summary>Merges another record of the same game into this one, preferring existing values.</summary>
    public Game MergeWith(Game other)
    {
        var aliases = new List<string>(Aliases);
        foreach (var title in other.AllTitles())
            if (!aliases.Contains(title, StringComparer.OrdinalIgnoreCase) &&
                !string.Equals(title, CanonicalName, StringComparison.OrdinalIgnoreCase))
                aliases.Add(title);

        return new Game
        {
            CanonicalName = CanonicalName,
            DisplayName = EffectiveDisplayName,
            JapaneseName = JapaneseName ?? other.JapaneseName,
            Aliases = aliases,
            ReleaseDate = ReleaseDate ?? other.ReleaseDate,
            Developer = Developer ?? other.Developer,
            Publisher = Publisher ?? other.Publisher,
            Platforms = Platforms.Count > 0 ? Platforms : other.Platforms,
            Summary = Summary ?? other.Summary,
            PreviewImageUrl = PreviewImageUrl ?? other.PreviewImageUrl,
            SourceIds = [.. SourceIds, .. other.SourceIds.Where(o => SourceFor(o.Provider) is null)],
            Confidence = Math.Max(Confidence, other.Confidence),
        };
    }
}

/// <summary>A game's identity on one specific provider.</summary>
/// <param name="Provider">Provider id, e.g. <c>steam</c>, <c>steamgriddb</c>, <c>vndb</c>.</param>
/// <param name="Id">Provider-native identifier.</param>
public readonly record struct GameSourceId(string Provider, string Id)
{
    public override string ToString() => $"{Provider}:{Id}";
}
