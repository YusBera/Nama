using Nama.Core.Models;
using Nama.SteamIntegration.Vdf;

namespace Nama.SteamIntegration;

/// <summary>Raised when Steam's files cannot be read or written.</summary>
public sealed class SteamException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// The only component that knows Steam's on-disk layout. Everything else in Nama works
/// with <see cref="SteamShortcut"/> and <see cref="Artwork"/> and never touches a VDF.
/// </summary>
public sealed class SteamManager
{
    private readonly Func<string, CancellationToken, Task<byte[]?>> _downloadImage;

    /// <param name="downloadImage">
    /// Fetches image bytes for a URL. Injected so this project stays free of HTTP
    /// concerns and can be tested without a network.
    /// </param>
    public SteamManager(Func<string, CancellationToken, Task<byte[]?>> downloadImage)
    {
        _downloadImage = downloadImage;
    }

    /// <summary>Locates Steam, or returns null when it is not installed.</summary>
    public SteamInstallation? FindSteamInstallation(string? overridePath = null) =>
        SteamLocator.FindSteamInstallation(overridePath);

    /// <summary>
    /// Picks the user profile to write to: the caller's preference when it still exists,
    /// otherwise the most recently used account.
    /// </summary>
    public SteamUser? FindLibraryData(SteamInstallation installation, string? preferredAccountId = null)
    {
        if (!string.IsNullOrWhiteSpace(preferredAccountId))
        {
            var preferred = installation.Users.FirstOrDefault(u =>
                string.Equals(u.AccountId, preferredAccountId, StringComparison.Ordinal));
            if (preferred is not null) return preferred;
        }

        return installation.Users.FirstOrDefault();
    }

    /// <summary>
    /// Reads every non-Steam shortcut for a user. A missing file means an empty library,
    /// not an error — Steam only creates it once the first shortcut is added.
    /// </summary>
    /// <exception cref="SteamException">The file exists but could not be read or parsed.</exception>
    public IReadOnlyList<SteamShortcut> GetExistingShortcuts(SteamUser user)
    {
        if (!File.Exists(user.ShortcutsFile)) return [];

        VdfNode root;
        try
        {
            root = BinaryVdf.ParseFile(user.ShortcutsFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SteamException(
                "Could not read Steam's shortcuts file. Close Steam and try again.", ex);
        }
        catch (InvalidDataException ex)
        {
            throw new SteamException(
                "Steam's shortcuts file is not in the expected format and was left untouched.", ex);
        }

        var shortcuts = root["shortcuts"];
        if (shortcuts is null) return [];

        return shortcuts.Children
            .Where(c => c.Value.Kind == VdfKind.Object)
            .Select(c => SteamShortcut.FromVdf(c.Value))
            .ToList();
    }

    /// <summary>
    /// Finds an existing library entry for the same game, so Nama can offer to update it
    /// rather than silently creating a duplicate.
    /// </summary>
    public ExistingEntry? DetectExistingEntry(SteamUser user, string executablePath, string displayName)
    {
        var shortcuts = GetExistingShortcuts(user);
        var quotedExe = SteamAppIds.QuotePath(executablePath);
        var appId = SteamAppIds.ComputeShortcutAppIdSigned(quotedExe, displayName);

        // Same target is the strongest signal: it is the same game however it is named.
        foreach (var shortcut in shortcuts)
            if (PathsEqual(shortcut.ExePathUnquoted, executablePath))
                return new ExistingEntry(shortcut, DuplicateMatch.SameExecutable);

        // An identical app id would collide in Steam's own storage.
        foreach (var shortcut in shortcuts)
            if (shortcut.AppId == appId)
                return new ExistingEntry(shortcut, DuplicateMatch.SameAppId);

        // Same name elsewhere is worth flagging but is more likely a genuine second copy.
        foreach (var shortcut in shortcuts)
            if (string.Equals(shortcut.AppName.Trim(), displayName.Trim(), StringComparison.OrdinalIgnoreCase))
                return new ExistingEntry(shortcut, DuplicateMatch.SameName);

        return null;
    }

    /// <summary>Appends a shortcut. Callers must resolve duplicates first.</summary>
    public void AddShortcut(SteamUser user, SteamShortcut shortcut, bool backup = true)
    {
        var (root, list) = LoadShortcutsDocument(user);

        shortcut.RefreshAppId();
        list.Add(NextIndex(list).ToString(), shortcut.ToVdf());

        WriteShortcutsDocument(user, root, backup);
    }

    /// <summary>
    /// Replaces the entry whose app id matches <paramref name="shortcut"/>'s previous id.
    /// </summary>
    /// <param name="previousAppId">
    /// The app id before any rename. Renaming changes the computed id, so the old value is
    /// needed to find the entry.
    /// </param>
    public void UpdateShortcut(SteamUser user, SteamShortcut shortcut, int previousAppId, bool backup = true)
    {
        var (root, list) = LoadShortcutsDocument(user);

        var replaced = false;
        var rebuilt = VdfNode.NewObject();
        var index = 0;

        foreach (var (_, node) in list.Children)
        {
            if (node.Kind != VdfKind.Object) continue;

            var existing = SteamShortcut.FromVdf(node);

            if (!replaced &&
                (existing.AppId == previousAppId ||
                 PathsEqual(existing.ExePathUnquoted, shortcut.ExePathUnquoted)))
            {
                // Carry over play history so updating artwork does not reset the entry.
                shortcut.LastPlayTime = existing.LastPlayTime;
                shortcut.RefreshAppId();
                rebuilt.Add((index++).ToString(), shortcut.ToVdf());
                replaced = true;
                continue;
            }

            rebuilt.Add((index++).ToString(), node);
        }

        if (!replaced)
        {
            shortcut.RefreshAppId();
            rebuilt.Add((index).ToString(), shortcut.ToVdf());
        }

        root.Set("shortcuts", rebuilt);
        WriteShortcutsDocument(user, root, backup);
    }

    /// <summary>Removes a shortcut by app id and re-indexes the remaining entries.</summary>
    /// <returns>True when an entry was removed.</returns>
    public bool RemoveShortcut(SteamUser user, int appId, bool backup = true)
    {
        var (root, list) = LoadShortcutsDocument(user);

        var rebuilt = VdfNode.NewObject();
        var index = 0;
        var removed = false;

        foreach (var (_, node) in list.Children)
        {
            if (node.Kind != VdfKind.Object) continue;

            if (SteamShortcut.FromVdf(node).AppId == appId && !removed)
            {
                removed = true;
                continue;
            }

            rebuilt.Add((index++).ToString(), node);
        }

        if (!removed) return false;

        root.Set("shortcuts", rebuilt);
        WriteShortcutsDocument(user, root, backup);
        return true;
    }

    /// <summary>
    /// Downloads the chosen artwork and writes it into the user's grid folder using the
    /// file names Steam looks for.
    /// </summary>
    /// <returns>The types written, and the types that failed with a reason.</returns>
    public async Task<(IReadOnlyList<ArtworkType> Applied, IReadOnlyList<(ArtworkType Type, string Reason)> Failed)>
        ApplyArtworkAsync(
            SteamUser user,
            SteamShortcut shortcut,
            IReadOnlyDictionary<ArtworkType, Artwork> selections,
            CancellationToken ct = default)
    {
        var applied = new List<ArtworkType>();
        var failed = new List<(ArtworkType, string)>();

        if (selections.Count == 0) return (applied, failed);

        try
        {
            Directory.CreateDirectory(user.GridPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SteamException($"Could not create Steam's artwork folder at \"{user.GridPath}\".", ex);
        }

        foreach (var (type, artwork) in selections)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var bytes = await _downloadImage(artwork.Url, ct).ConfigureAwait(false);
                if (bytes is null || bytes.Length == 0)
                {
                    failed.Add((type, "The image could not be downloaded."));
                    continue;
                }

                var extension = ExtensionFor(artwork.Url);
                var path = Path.Combine(user.GridPath, GridFileName(shortcut.ArtworkId, type, extension));

                // Steam picks whichever extension it finds first, so stale variants of the
                // same artwork slot must go or they can win over the new file.
                RemoveOtherExtensions(user.GridPath, shortcut.ArtworkId, type, keep: Path.GetFileName(path));

                await File.WriteAllBytesAsync(path, bytes, ct).ConfigureAwait(false);

                // The icon is referenced by path from the shortcut itself, not looked up by name.
                if (type == ArtworkType.Icon) shortcut.Icon = path;

                applied.Add(type);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException)
            {
                failed.Add((type, ex.Message));
            }
        }

        return (applied, failed);
    }

    /// <summary>
    /// Steam's artwork naming scheme, keyed on the unsigned shortcut app id.
    /// These names are what the Steam client globs for when rendering the library.
    /// </summary>
    public static string GridFileName(uint artworkId, ArtworkType type, string extension) => type switch
    {
        ArtworkType.Grid => $"{artworkId}{extension}",
        ArtworkType.Cover => $"{artworkId}p{extension}",
        ArtworkType.Hero => $"{artworkId}_hero{extension}",
        ArtworkType.Logo => $"{artworkId}_logo{extension}",
        ArtworkType.Icon => $"{artworkId}_icon{extension}",
        _ => $"{artworkId}_{type.ToString().ToLowerInvariant()}{extension}",
    };

    /// <summary>Deletes same-slot artwork written with a different extension.</summary>
    private static void RemoveOtherExtensions(string gridPath, uint artworkId, ArtworkType type, string keep)
    {
        foreach (var extension in (string[])[".png", ".jpg", ".jpeg", ".webp", ".ico", ".img"])
        {
            var name = GridFileName(artworkId, type, extension);
            if (string.Equals(name, keep, StringComparison.OrdinalIgnoreCase)) continue;

            try
            {
                var path = Path.Combine(gridPath, name);
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Leaving a stale file behind is not worth failing the whole operation.
            }
        }
    }

    private static string ExtensionFor(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var extension = Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();
            if (extension is ".png" or ".jpg" or ".jpeg" or ".webp" or ".ico")
                return extension == ".jpeg" ? ".jpg" : extension;
        }

        return ".png";
    }

    /// <summary>Loads the shortcuts document, creating an empty one when the file is absent.</summary>
    private (VdfNode Root, VdfNode List) LoadShortcutsDocument(SteamUser user)
    {
        VdfNode root;

        if (File.Exists(user.ShortcutsFile))
        {
            try
            {
                root = BinaryVdf.ParseFile(user.ShortcutsFile);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new SteamException("Could not read Steam's shortcuts file. Close Steam and try again.", ex);
            }
            catch (InvalidDataException ex)
            {
                throw new SteamException(
                    "Steam's shortcuts file is not in the expected format and was left untouched.", ex);
            }
        }
        else
        {
            root = VdfNode.NewObject();
        }

        return (root, root.GetOrCreateObject("shortcuts"));
    }

    /// <summary>
    /// Writes the document atomically, keeping a one-generation backup. Steam holds this
    /// file in memory while running and rewrites it on exit, which is why the caller is
    /// told to restart Steam.
    /// </summary>
    private static void WriteShortcutsDocument(SteamUser user, VdfNode root, bool backup)
    {
        var bytes = BinaryVdf.Serialize(root);

        try
        {
            Directory.CreateDirectory(user.ConfigPath);

            if (backup && File.Exists(user.ShortcutsFile))
                File.Copy(user.ShortcutsFile, user.ShortcutsFile + ".nama.bak", overwrite: true);

            var temp = user.ShortcutsFile + ".nama.tmp";
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, user.ShortcutsFile, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SteamException(
                "Could not write Steam's shortcuts file. Close Steam and try again.", ex);
        }
    }

    /// <summary>Compares two paths for equality, tolerating casing and trailing separators.</summary>
    private static bool PathsEqual(string a, string b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;

        try
        {
            return string.Equals(
                Path.GetFullPath(a.Trim().Trim('"')).TrimEnd('\\'),
                Path.GetFullPath(b.Trim().Trim('"')).TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return string.Equals(a.Trim().Trim('"'), b.Trim().Trim('"'), StringComparison.OrdinalIgnoreCase);
        }
    }

    private static int NextIndex(VdfNode list)
    {
        var max = -1;
        foreach (var (key, _) in list.Children)
            if (int.TryParse(key, out var index) && index > max)
                max = index;
        return max + 1;
    }
}
