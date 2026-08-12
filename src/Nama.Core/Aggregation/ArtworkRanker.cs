using Nama.Core.Models;

namespace Nama.Core.Aggregation;

/// <summary>
/// Orders artwork within a slot so the first five are worth showing without expanding.
/// <para>
/// Priority follows the spec: provider score, then popularity, then resolution, then how
/// well the shape fits the slot. Aspect ratio is weighted heavily on purpose — a portrait
/// cover shown in the wide banner slot gets letterboxed by Steam and looks broken no
/// matter how good the image is.
/// </para>
/// </summary>
public static class ArtworkRanker
{
    /// <summary>Shape Steam expects for each slot.</summary>
    private static double TargetAspect(ArtworkType type) => type switch
    {
        ArtworkType.Cover => 600.0 / 900.0,
        ArtworkType.Grid => 460.0 / 215.0,
        ArtworkType.Hero => 1920.0 / 620.0,
        ArtworkType.Logo => 16.0 / 9.0,
        ArtworkType.Icon => 1.0,
        _ => 16.0 / 9.0,
    };

    /// <summary>Resolution at which an image is considered fully sharp for its slot.</summary>
    private static double ReferencePixels(ArtworkType type) => type switch
    {
        ArtworkType.Cover => 600.0 * 900.0,
        ArtworkType.Grid => 920.0 * 430.0,
        ArtworkType.Hero => 1920.0 * 620.0,
        ArtworkType.Logo => 640.0 * 360.0,
        ArtworkType.Icon => 256.0 * 256.0,
        _ => 1920.0 * 1080.0,
    };

    /// <summary>Highest-ranked first.</summary>
    public static IReadOnlyList<Artwork> Rank(IEnumerable<Artwork> artwork, ArtworkType type) =>
        artwork.OrderByDescending(a => Score(a, type)).ToList();

    /// <summary>The five to show before the user expands the section.</summary>
    public static IReadOnlyList<Artwork> Recommended(IEnumerable<Artwork> artwork, ArtworkType type, int count = 5) =>
        Rank(artwork, type).Take(count).ToList();

    /// <summary>Combined desirability, 0..1.</summary>
    public static double Score(Artwork artwork, ArtworkType type)
    {
        var quality = artwork.Score ?? 0.5;
        var popularity = Popularity(artwork.Votes);
        var resolution = Resolution(artwork, type);
        var shape = ShapeFit(artwork, type);

        var score = (quality * 0.35) + (popularity * 0.20) + (resolution * 0.15) + (shape * 0.30);

        // Animated artwork is a deliberate choice, not a default. Rank it below equivalent
        // stills rather than excluding it.
        if (artwork.IsAnimated) score *= 0.85;

        return score;
    }

    /// <summary>
    /// Vote counts are unbounded and long-tailed, so compress them logarithmically:
    /// the gap between 0 and 50 votes should matter far more than 500 versus 1000.
    /// </summary>
    private static double Popularity(int? votes)
    {
        if (votes is null or <= 0) return 0.3;

        return Math.Min(1.0, Math.Log10(votes.Value + 1) / 3.0);
    }

    private static double Resolution(Artwork artwork, ArtworkType type)
    {
        var pixels = (double)artwork.Width * artwork.Height;
        if (pixels <= 0) return 0.5; // unknown dimensions are not a mark against it

        return Math.Min(1.0, pixels / ReferencePixels(type));
    }

    /// <summary>
    /// 1.0 for an exact aspect match, falling off as the shape diverges. Compared as a
    /// ratio rather than a difference so that being twice as wide and half as wide are
    /// penalised equally.
    /// </summary>
    private static double ShapeFit(Artwork artwork, ArtworkType type)
    {
        if (artwork.Width <= 0 || artwork.Height <= 0) return 0.5;

        var target = TargetAspect(type);
        var actual = artwork.AspectRatio;
        var divergence = Math.Abs(Math.Log(actual / target));

        return Math.Exp(-divergence * 1.5);
    }
}
