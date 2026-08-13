namespace Nama.Storage;

/// <summary>
/// The single place that knows where Nama keeps its files, so nothing else has to
/// build paths by hand.
/// </summary>
public static class NamaPaths
{
    /// <summary>Roaming config directory: <c>%APPDATA%\Nama</c>.</summary>
    public static string ConfigDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Nama");

    /// <summary>Local cache directory: <c>%LOCALAPPDATA%\Nama</c>. Safe to delete at any time.</summary>
    public static string CacheDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nama", "Cache");

    public static string SettingsFile => Path.Combine(ConfigDirectory, "settings.json");

    public static string SearchCacheDirectory => Path.Combine(CacheDirectory, "search");

    public static string ImageCacheDirectory => Path.Combine(CacheDirectory, "images");

    /// <summary>Where downloaded artwork is staged before being copied into Steam's grid folder.</summary>
    public static string ArtworkStagingDirectory => Path.Combine(CacheDirectory, "artwork");

    /// <summary>Creates a directory if needed and returns it, so callers can inline the call.</summary>
    public static string Ensure(string directory)
    {
        Directory.CreateDirectory(directory);
        return directory;
    }
}
