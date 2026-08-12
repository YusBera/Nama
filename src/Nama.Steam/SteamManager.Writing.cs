using System.Diagnostics;
using System.Runtime.Versioning;
using Nama.Core.Abstractions;
using Nama.Core.Models;
using Nama.Steam.Models;
using Nama.Steam.Vdf;
using Nama.Steam.Writing;

namespace Nama.Steam;

/// <summary>
/// The write half of <see cref="SteamManager"/>. Split from the read half because the
/// safety machinery around it is the most consequential code in the project.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class SteamManager
{
    /// <summary>
    /// Whether a write may proceed. Called before anything is fetched or written, so a
    /// blocked write never leaves stray files behind.
    /// </summary>
    public WriteReadiness CheckWriteReadiness(SteamAccount? account, ShortcutsFile file)
    {
        if (account is null)
        {
            return WriteReadiness.Blocked(WriteBlockReason.NoAccount, "No Steam account was found.");
        }

        // The running-Steam problem is specifically that Steam holds its own copy of the
        // files it owns and rewrites them on exit. It only applies to paths inside the
        // Steam installation — a copy of userdata elsewhere is not at risk.
        if (IsSteamRunning() && IsManagedBySteam(file.Path))
        {
            return WriteReadiness.Blocked(
                WriteBlockReason.SteamRunning,
                "Steam is running. It overwrites shortcuts.vdf when it exits, so the shortcut would be lost. Close Steam and try again.");
        }

        if (!file.RoundTrips)
        {
            return WriteReadiness.Blocked(
                WriteBlockReason.RoundTripFailed,
                $"Nama could not reproduce '{file.Path}' exactly after reading it, so it does not fully understand the file. " +
                "Writing is disabled to avoid damaging the shortcuts already in it.");
        }

        if (!ShortcutFileWriter.CanWrite(file.Path))
        {
            return WriteReadiness.Blocked(
                WriteBlockReason.NotWritable,
                $"'{file.Path}' cannot be written to. It may be locked by another program.");
        }

        return WriteReadiness.Ready;
    }

    /// <summary>
    /// Creates or updates a non-Steam shortcut and applies its artwork.
    /// <para>
    /// Ordering is deliberate: readiness is checked first, then every image is downloaded
    /// into memory, then the shortcut file is written and verified, and only then are the
    /// images flushed to disk. A failure at any point before the verified write leaves the
    /// filesystem untouched.
    /// </para>
    /// </summary>
    public async Task<WriteResult> AddOrUpdateShortcutAsync(
        SteamAccount account,
        ShortcutRequest request,
        IImageDownloader downloader,
        bool dryRun = false,
        CancellationToken ct = default)
    {
        var file = GetExistingShortcuts(account);

        var readiness = CheckWriteReadiness(account, file);
        if (!readiness.CanWrite && !dryRun) return WriteResult.Blocked(readiness);

        var plan = new List<string>();
        var existing = DetectExistingEntry(file, request.ExecutablePath, request.DisplayName);

        if (existing is not null && request.OnDuplicate == DuplicateAction.Fail)
        {
            return new WriteResult
            {
                Success = false,
                Error = $"'{existing.AppName}' is already in your Steam library.",
                WasUpdate = true,
                ExistingEntry = new SteamShortcutSummary(existing.AppName, existing.ExePath, existing.AppId),
            };
        }

        var quotedExe = SteamAppId.Quote(request.ExecutablePath);
        uint appId;
        VdfMap targetNode;
        string? targetKey;

        if (existing is not null)
        {
            // Never recompute the app id of an entry that already exists. Steam assigns its
            // own, artwork filenames follow the stored value, and changing it would point
            // the artwork at a name Steam never reads.
            appId = existing.AppId;
            targetNode = existing.Node;
            targetKey = FindKey(file, existing);

            if (request.OnDuplicate == DuplicateAction.ReplaceEntry)
            {
                plan.Add($"Update entry '{existing.AppName}' -> '{request.DisplayName}'");
                existing.AppName = request.DisplayName;
                existing.Exe = quotedExe;
                existing.StartDir = EnsureTrailingSeparator(request.StartDirectory ?? DirectoryOf(request.ExecutablePath));
                if (request.LaunchOptions is not null) existing.LaunchOptions = request.LaunchOptions;
            }
            else
            {
                plan.Add($"Keep entry '{existing.AppName}' unchanged, replace artwork only");
            }
        }
        else
        {
            appId = SteamAppId.Compute(quotedExe, request.DisplayName);
            targetKey = file.NextKey();
            targetNode = CreateEntry(request, quotedExe, appId);
            plan.Add($"Add new entry '{request.DisplayName}' (appid {appId})");
        }

        // Fetch everything before touching disk.
        var applier = new ArtworkApplier(downloader);
        var fetched = request.Artwork.Count > 0
            ? await applier.FetchAsync(request.Artwork, ct).ConfigureAwait(false)
            : new Dictionary<ArtworkType, DownloadedImage>();

        // The icon has to exist on disk before the shortcut referencing it is serialized,
        // but its path is deterministic, so plan it now and write it with the rest.
        if (fetched.TryGetValue(ArtworkType.Icon, out var icon))
        {
            var iconPath = Path.Combine(ArtworkApplier.IconDirectory, appId + icon.Extension);
            targetNode.SetString("icon", iconPath);
            plan.Add($"Set icon -> {iconPath}");
        }

        foreach (var type in request.Artwork.Keys)
        {
            var stem = ArtworkApplier.GridStem(type, appId);
            if (stem is not null) plan.Add($"Write {type} -> {Path.Combine(account.GridPath, stem)}.*");
        }

        // Fingerprint every entry that must survive untouched.
        var survivors = ShortcutFileWriter.Fingerprint(file.Container);
        if (targetKey is not null) survivors.Remove(targetKey);

        if (existing is null && targetKey is not null) file.Container.Set(targetKey, targetNode);

        var payload = file.Serialize();

        if (dryRun)
        {
            var report = applier.Apply(account, appId, request.Artwork, fetched, dryRun: true);

            return new WriteResult
            {
                Success = readiness.CanWrite,
                Error = readiness.CanWrite ? null : readiness.Message,
                BlockReason = readiness.Reason,
                AppId = appId,
                WasUpdate = existing is not null,
                DryRun = true,
                Artwork = report,
                PlannedActions = [.. plan, $"Write {payload.Length} bytes to {file.Path}"],
            };
        }

        string? backup;
        try
        {
            backup = ShortcutFileWriter.WriteVerified(file.Path, payload, survivors);
        }
        catch (ShortcutWriteException e)
        {
            return new WriteResult { Success = false, Error = e.Message };
        }

        var applyReport = applier.Apply(account, appId, request.Artwork, fetched, dryRun: false);

        return new WriteResult
        {
            Success = true,
            AppId = appId,
            WasUpdate = existing is not null,
            BackupPath = backup,
            Artwork = applyReport,
            PlannedActions = plan,
        };
    }

    /// <summary>Removes a shortcut. Its artwork files are left in place.</summary>
    public WriteResult RemoveShortcut(SteamAccount account, uint appId)
    {
        var file = GetExistingShortcuts(account);

        var readiness = CheckWriteReadiness(account, file);
        if (!readiness.CanWrite) return WriteResult.Blocked(readiness);

        string? removedKey = null;
        foreach (var (key, map) in file.Container.ChildMaps())
        {
            if (new SteamShortcut(map).AppId == appId)
            {
                removedKey = key;
                break;
            }
        }

        if (removedKey is null)
        {
            return new WriteResult { Success = false, Error = $"No shortcut with app id {appId}." };
        }

        var survivors = ShortcutFileWriter.Fingerprint(file.Container);
        survivors.Remove(removedKey);
        file.Container.Remove(removedKey);

        try
        {
            var backup = ShortcutFileWriter.WriteVerified(file.Path, file.Serialize(), survivors);
            return new WriteResult { Success = true, AppId = appId, BackupPath = backup };
        }
        catch (ShortcutWriteException e)
        {
            return new WriteResult { Success = false, Error = e.Message };
        }
    }

    /// <summary>
    /// Asks Steam to shut down and waits for it. Uses Steam's own <c>-shutdown</c> switch
    /// rather than killing the process, so it saves its state normally.
    /// </summary>
    public async Task<bool> ShutdownSteamAsync(SteamInstallation installation, CancellationToken ct = default)
    {
        if (!IsSteamRunning()) return true;

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = installation.ClientExecutablePath,
                Arguments = "-shutdown",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            return false;
        }

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            if (!IsSteamRunning()) return true;
            await Task.Delay(250, ct).ConfigureAwait(false);
        }

        return !IsSteamRunning();
    }

    /// <summary>Relaunches Steam after a write.</summary>
    public bool StartSteam(SteamInstallation installation)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = installation.ClientExecutablePath,
                UseShellExecute = true,
            });

            return true;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            return false;
        }
    }

    /// <summary>Backups of an account's shortcuts file, newest first.</summary>
    public IReadOnlyList<string> ListBackups(SteamAccount account) =>
        ShortcutFileWriter.ListBackups(account.ShortcutsPath);

    // --- helpers ---------------------------------------------------------------------

    private static VdfMap CreateEntry(ShortcutRequest request, string quotedExe, uint appId)
    {
        // Field order matches what Steam itself writes, so a file Nama creates looks
        // exactly like one Steam created.
        var entry = new VdfMap();
        entry.Add("appid", new VdfInt32(SteamAppId.ToShortcutField(appId)));
        entry.Add("AppName", new VdfString(request.DisplayName));
        entry.Add("Exe", new VdfString(quotedExe));
        entry.Add("StartDir", new VdfString(
            EnsureTrailingSeparator(request.StartDirectory ?? DirectoryOf(request.ExecutablePath))));
        entry.Add("icon", new VdfString(string.Empty));
        entry.Add("ShortcutPath", new VdfString(string.Empty));
        entry.Add("LaunchOptions", new VdfString(request.LaunchOptions ?? string.Empty));
        entry.Add("IsHidden", new VdfInt32(0));
        entry.Add("AllowDesktopConfig", new VdfInt32(1));
        entry.Add("AllowOverlay", new VdfInt32(1));
        entry.Add("OpenVR", new VdfInt32(0));
        entry.Add("Devkit", new VdfInt32(0));
        entry.Add("DevkitGameID", new VdfString(string.Empty));
        entry.Add("DevkitOverrideAppID", new VdfInt32(0));
        entry.Add("LastPlayTime", new VdfInt32(0));
        entry.Add("FlatpakAppID", new VdfString(string.Empty));
        entry.Add("sortas", new VdfString(string.Empty));
        entry.Add("tags", new VdfMap());

        return entry;
    }

    private static string? FindKey(ShortcutsFile file, SteamShortcut shortcut)
    {
        foreach (var (key, map) in file.Container.ChildMaps())
        {
            if (ReferenceEquals(map, shortcut.Node)) return key;
        }

        return null;
    }

    /// <summary>
    /// True when a path lives inside the detected Steam installation, and is therefore
    /// subject to being rewritten by a running Steam client.
    /// </summary>
    public bool IsManagedBySteam(string path)
    {
        var installation = FindSteamInstallation();
        if (installation is null) return false;

        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetFullPath(installation.Path).TrimEnd(Path.DirectorySeparatorChar);

            return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // If the path cannot be resolved, assume it is Steam's and stay cautious.
            return true;
        }
    }

    private static string DirectoryOf(string executablePath) =>
        Path.GetDirectoryName(executablePath.Trim('"')) ?? string.Empty;

    /// <summary>Steam stores StartDir with a trailing separator.</summary>
    private static string EnsureTrailingSeparator(string directory) =>
        string.IsNullOrEmpty(directory) || directory.EndsWith(Path.DirectorySeparatorChar)
            ? directory
            : directory + Path.DirectorySeparatorChar;
}
