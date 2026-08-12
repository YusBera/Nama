using Nama.Core.Models;

namespace Nama.Core.Abstractions;

/// <summary>
/// A source of artwork for an already-confirmed game. Results from every available
/// provider are merged into one list per <see cref="ArtworkType"/>; the user picks an
/// image, never a source.
/// </summary>
public interface IArtworkProvider
{
    /// <summary>Stable lowercase id, e.g. "steamgriddb". Surfaced as the tile's source label.</summary>
    string SourceId { get; }

    string DisplayName { get; }

    /// <summary>False when a prerequisite is missing (typically an API key).</summary>
    bool IsAvailable { get; }

    /// <summary>Artwork types this provider can supply at all. Used to skip pointless calls.</summary>
    IReadOnlyCollection<ArtworkType> SupportedTypes { get; }

    /// <summary>
    /// True when this provider can act on the given game — usually "does the ref carry an
    /// id I understand", though name-searchable providers may accept any ref.
    /// </summary>
    bool CanResolve(GameRef game);

    /// <summary>
    /// Fetch all artwork this provider has. Must not throw for ordinary failures — return
    /// an empty list instead.
    /// </summary>
    Task<IReadOnlyList<Artwork>> GetArtworkAsync(GameRef game, CancellationToken ct = default);
}
