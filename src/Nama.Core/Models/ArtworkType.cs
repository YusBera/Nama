namespace Nama.Core.Models;

/// <summary>
/// The artwork slots Nama understands. Not every provider supplies every type; the UI
/// simply hides the ones that come back empty.
/// </summary>
public enum ArtworkType
{
    /// <summary>Small square icon. Stored as a file on disk and referenced by the shortcut.</summary>
    Icon,

    /// <summary>Wide capsule shown in the library grid (460x215 / 920x430).</summary>
    Grid,

    /// <summary>Wide banner behind the library detail page.</summary>
    Hero,

    /// <summary>Portrait library capsule (600x900).</summary>
    Cover,

    /// <summary>Transparent title logo overlaid on the hero.</summary>
    Logo,

    /// <summary>General background / promotional art. Not written to Steam directly.</summary>
    Background,
}
