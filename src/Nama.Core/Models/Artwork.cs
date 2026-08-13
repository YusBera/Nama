namespace Nama.Core.Models;

/// <summary>
/// The kinds of artwork Nama knows how to present. Not every provider supplies
/// every type; the UI only renders sections that actually have results.
/// </summary>
public enum ArtworkType
{
    /// <summary>Small square icon. Written to the shortcut's icon field.</summary>
    Icon,

    /// <summary>Wide capsule shown in the Steam library grid (460x215 / 920x430).</summary>
    Grid,

    /// <summary>Wide banner behind the library page header (1920x620).</summary>
    Hero,

    /// <summary>Vertical library capsule (600x900).</summary>
    Cover,

    /// <summary>Transparent logo overlaid on the hero.</summary>
    Logo,

    /// <summary>Free-form background / promotional art. Not applied to Steam directly.</summary>
    Background,
}

/// <summary>Normalized artwork item, independent of the provider that supplied it.</summary>
public sealed class Artwork
{
    public required string Id { get; init; }
    public required ArtworkType Type { get; init; }

    /// <summary>Full-resolution image URL, downloaded when the user commits.</summary>
    public required string Url { get; init; }

    /// <summary>Smaller preview URL. Falls back to <see cref="Url"/> when absent.</summary>
    public string? ThumbnailUrl { get; init; }

    /// <summary>Human-readable provider name shown as a badge on the tile.</summary>
    public required string Source { get; init; }

    public int Width { get; init; }
    public int Height { get; init; }

    /// <summary>Provider-supplied rating/upvotes, if any. Used for recommendation ordering.</summary>
    public double? Score { get; init; }

    public string? Author { get; init; }

    /// <summary>Provider style tag (e.g. <c>alternate</c>, <c>material</c>, <c>white_logo</c>).</summary>
    public string? Style { get; init; }

    /// <summary>True when the provider flags this as animated (APNG/webm). Steam ignores these.</summary>
    public bool IsAnimated { get; init; }

    public string PreviewUrl => string.IsNullOrWhiteSpace(ThumbnailUrl) ? Url : ThumbnailUrl!;

    public double AspectRatio => Height > 0 ? (double)Width / Height : 0;

    public string Dimensions => Width > 0 && Height > 0 ? $"{Width}x{Height}" : string.Empty;
}

public static class ArtworkTypeInfo
{
    /// <summary>The aspect ratio Steam expects for each artwork type, used when ranking.</summary>
    public static double IdealAspect(ArtworkType type) => type switch
    {
        ArtworkType.Icon => 1.0,
        ArtworkType.Grid => 460.0 / 215.0,
        ArtworkType.Hero => 1920.0 / 620.0,
        ArtworkType.Cover => 600.0 / 900.0,
        ArtworkType.Logo => 2.0,
        _ => 16.0 / 9.0,
    };

    public static string Label(ArtworkType type) => type switch
    {
        ArtworkType.Icon => "ICON",
        ArtworkType.Grid => "GRID",
        ArtworkType.Hero => "HERO",
        ArtworkType.Cover => "COVER",
        ArtworkType.Logo => "LOGO",
        ArtworkType.Background => "BACKGROUND",
        _ => type.ToString().ToUpperInvariant(),
    };

    public static string Description(ArtworkType type) => type switch
    {
        ArtworkType.Icon => "Shown next to the game in your library list",
        ArtworkType.Grid => "Wide capsule in the library grid",
        ArtworkType.Hero => "Banner across the top of the game page",
        ArtworkType.Cover => "Vertical capsule in the library shelf",
        ArtworkType.Logo => "Transparent logo laid over the banner",
        ArtworkType.Background => "Promotional background art",
        _ => string.Empty,
    };

    /// <summary>Position of a type in <see cref="DisplayOrder"/>; unlisted types sort last.</summary>
    public static int OrderIndex(ArtworkType type)
    {
        var index = Array.IndexOf(DisplayOrder, type);
        return index >= 0 ? index : int.MaxValue;
    }

    /// <summary>Display order of the artwork sections in the picker.</summary>
    public static readonly ArtworkType[] DisplayOrder =
    [
        ArtworkType.Cover,
        ArtworkType.Grid,
        ArtworkType.Hero,
        ArtworkType.Logo,
        ArtworkType.Icon,
        ArtworkType.Background,
    ];

    /// <summary>Types Nama can actually write into a Steam library entry.</summary>
    public static readonly IReadOnlyList<ArtworkType> SteamApplicable =
    [
        ArtworkType.Cover,
        ArtworkType.Grid,
        ArtworkType.Hero,
        ArtworkType.Logo,
        ArtworkType.Icon,
    ];
}
