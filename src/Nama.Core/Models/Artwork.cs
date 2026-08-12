namespace Nama.Core.Models;

/// <summary>
/// A single piece of candidate artwork, normalized across providers. Ranking metadata
/// (<see cref="Score"/>, <see cref="Votes"/>, dimensions) is optional because providers
/// expose wildly different amounts of it.
/// </summary>
public sealed record Artwork
{
    /// <summary>Provider-local identifier. Unique only within <see cref="Source"/>.</summary>
    public required string Id { get; init; }

    public required ArtworkType Type { get; init; }

    /// <summary>Full-resolution image URL.</summary>
    public required string Url { get; init; }

    /// <summary>Smaller preview URL. Falls back to <see cref="Url"/> when the provider has none.</summary>
    public string? ThumbnailUrl { get; init; }

    /// <summary>Provider source id, e.g. "steamgriddb". Shown as a small label on the tile.</summary>
    public required string Source { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }

    /// <summary>
    /// Quality signal normalized to 0..1, higher is better. Null when the provider offers
    /// none. Providers must map their own scale into this range — raw values are not
    /// comparable across sources, and the ranker sorts a single merged list.
    /// </summary>
    public double? Score { get; init; }

    /// <summary>Popularity signal (upvotes / downloads) where available.</summary>
    public int? Votes { get; init; }

    public string? Author { get; init; }

    /// <summary>Provider style tag, e.g. "alternate", "blurred", "white_logo".</summary>
    public string? Style { get; init; }

    /// <summary>Animated artwork needs different handling and is deprioritised in ranking.</summary>
    public bool IsAnimated { get; init; }

    /// <summary>
    /// Flagged as sexual content by the source. Recorded rather than filtered — visual
    /// novel cover art is routinely flagged, and silently hiding a game's actual cover
    /// would be the wrong default for the library this is built for. The UI can act on it.
    /// </summary>
    public bool IsNsfw { get; init; }

    /// <summary>Preview URL if present, otherwise the full image.</summary>
    public string PreviewUrl => ThumbnailUrl ?? Url;

    public double AspectRatio => Height == 0 ? 0 : (double)Width / Height;
}
