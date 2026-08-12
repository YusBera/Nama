using Nama.Core.Abstractions;
using Nama.Core.Models;
using Nama.Steam.Models;

namespace Nama.Steam.Writing;

/// <summary>
/// Puts chosen artwork where Steam looks for it.
/// <para>
/// Steam finds artwork purely by filename in <c>userdata\{id}\config\grid</c>, derived from
/// the shortcut's app id. Two details matter and both were confirmed against a real
/// library: the extension varies per file, and Steam will happily use a stale leftover of a
/// different extension — so an old <c>{appid}.jpg</c> has to be removed before a new
/// <c>{appid}.png</c> is written, or the replacement appears to do nothing.
/// </para>
/// </summary>
public sealed class ArtworkApplier(IImageDownloader downloader)
{
    /// <summary>
    /// The icon is not a grid file — Steam stores a path to it in the shortcut and reads
    /// that file directly, so it must live somewhere permanent that Nama owns.
    /// </summary>
    public static string IconDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Nama", "icons");

    /// <summary>Filename stem in the grid folder for a slot, or null if it is not stored there.</summary>
    public static string? GridStem(ArtworkType type, uint appId) => type switch
    {
        ArtworkType.Grid => appId.ToString(),
        ArtworkType.Cover => appId + "p",
        ArtworkType.Hero => appId + "_hero",
        ArtworkType.Logo => appId + "_logo",
        _ => null, // Icon lives elsewhere; Background is not a Steam slot at all.
    };

    /// <summary>
    /// Downloads every selected image before anything is written, so a failed download
    /// cannot leave a half-applied set on disk.
    /// </summary>
    public async Task<IReadOnlyDictionary<ArtworkType, DownloadedImage>> FetchAsync(
        IReadOnlyDictionary<ArtworkType, Artwork> selections, CancellationToken ct = default)
    {
        var fetched = new Dictionary<ArtworkType, DownloadedImage>();

        var downloads = await Task.WhenAll(selections.Select(async pair =>
            (pair.Key, Image: await downloader.DownloadAsync(pair.Value.Url, ct).ConfigureAwait(false))))
            .ConfigureAwait(false);

        foreach (var (type, image) in downloads)
        {
            if (image is not null) fetched[type] = image;
        }

        return fetched;
    }

    /// <summary>Writes fetched images to disk. Set <paramref name="dryRun"/> to plan without writing.</summary>
    public ArtworkApplyReport Apply(
        SteamAccount account,
        uint appId,
        IReadOnlyDictionary<ArtworkType, Artwork> selections,
        IReadOnlyDictionary<ArtworkType, DownloadedImage> fetched,
        bool dryRun)
    {
        var applied = new Dictionary<ArtworkType, string>();
        var failed = new Dictionary<ArtworkType, string>();
        string? iconPath = null;

        foreach (var (type, _) in selections)
        {
            if (!fetched.TryGetValue(type, out var image))
            {
                failed[type] = "Could not download the image.";
                continue;
            }

            if (type == ArtworkType.Background)
            {
                // Steam has no background slot for non-Steam shortcuts.
                failed[type] = "Steam has no background slot for non-Steam games.";
                continue;
            }

            try
            {
                if (type == ArtworkType.Icon)
                {
                    iconPath = WriteIcon(appId, image, dryRun);
                    applied[type] = iconPath;
                    continue;
                }

                applied[type] = WriteGridImage(account, type, appId, image, dryRun);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                failed[type] = e.Message;
            }
        }

        return new ArtworkApplyReport { Applied = applied, Failed = failed, IconPath = iconPath };
    }

    private static string WriteGridImage(
        SteamAccount account, ArtworkType type, uint appId, DownloadedImage image, bool dryRun)
    {
        var stem = GridStem(type, appId)
                   ?? throw new InvalidOperationException($"{type} has no grid filename.");

        // Steam cannot render every format; fall back to writing it as .png rather than
        // producing a file it will ignore.
        var extension = ImageFormat.IsSteamCompatible(image.Extension) ? image.Extension : ".png";
        var target = Path.Combine(account.GridPath, stem + extension);

        if (dryRun) return target;

        Directory.CreateDirectory(account.GridPath);
        RemoveStaleVariants(account.GridPath, stem, keep: target);
        File.WriteAllBytes(target, image.Bytes);

        return target;
    }

    private static string WriteIcon(uint appId, DownloadedImage image, bool dryRun)
    {
        var target = Path.Combine(IconDirectory, appId + image.Extension);
        if (dryRun) return target;

        Directory.CreateDirectory(IconDirectory);
        RemoveStaleVariants(IconDirectory, appId.ToString(), keep: target);
        File.WriteAllBytes(target, image.Bytes);

        return target;
    }

    /// <summary>
    /// Deletes same-stem files with a different extension. Without this, replacing a
    /// <c>.jpg</c> cover with a <c>.png</c> leaves both on disk and Steam may keep showing
    /// the old one.
    /// </summary>
    private static void RemoveStaleVariants(string directory, string stem, string keep)
    {
        if (!Directory.Exists(directory)) return;

        foreach (var file in Directory.EnumerateFiles(directory, stem + ".*"))
        {
            if (!Path.GetFileNameWithoutExtension(file).Equals(stem, StringComparison.OrdinalIgnoreCase)) continue;
            if (file.Equals(keep, StringComparison.OrdinalIgnoreCase)) continue;
            if (!ImageFormat.IsSteamCompatible(Path.GetExtension(file)) &&
                !Path.GetExtension(file).Equals(".ico", StringComparison.OrdinalIgnoreCase))
            {
                continue; // leave Steam's own .json sidecars alone
            }

            try
            {
                File.Delete(file);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A locked leftover is a display quirk, not a reason to fail the write.
            }
        }
    }
}
