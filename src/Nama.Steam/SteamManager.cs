using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Nama.Steam.Models;
using Nama.Steam.Vdf;

namespace Nama.Steam;

/// <summary>
/// The only type the rest of Nama talks to about Steam. Everything Steam-format-specific
/// lives behind this: the identification pipeline, the artwork aggregator and the UI have
/// no idea VDF exists.
/// <para>
/// This phase is read-only. Nothing here writes to disk.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class SteamManager
{
    private const string SteamProcessName = "steam";

    /// <summary>
    /// Locates the Steam client. Order matters: the per-user key reflects the running
    /// install, the machine key is the fallback for an install the current user has never
    /// launched, and the default path is a last resort.
    /// </summary>
    public SteamInstallation? FindSteamInstallation()
    {
        foreach (var candidate in EnumerateCandidatePaths())
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;

            // The registry stores this with forward slashes ("c:/program files (x86)/steam").
            var normalized = Path.GetFullPath(candidate.Replace('/', '\\'));
            if (Directory.Exists(Path.Combine(normalized, "userdata")) || File.Exists(Path.Combine(normalized, "steam.exe")))
            {
                return new SteamInstallation(normalized);
            }
        }

        return null;
    }

    private static IEnumerable<string?> EnumerateCandidatePaths()
    {
        yield return ReadRegistry(RegistryHive.CurrentUser, @"Software\Valve\Steam", "SteamPath");
        yield return ReadRegistry(RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath");
        yield return ReadRegistry(RegistryHive.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath");
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
    }

    private static string? ReadRegistry(RegistryHive hive, string subKey, string valueName)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry32);
            using var key = baseKey.OpenSubKey(subKey);
            return key?.GetValue(valueName) as string;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>
    /// Enumerates local accounts, newest login first.
    /// <para>
    /// The <c>userdata</c> folders are the source of truth for which accounts exist;
    /// <c>loginusers.vdf</c> only supplies names and timestamps, and an account can appear
    /// in one without the other.
    /// </para>
    /// </summary>
    public IReadOnlyList<SteamAccount> FindLibraryData(SteamInstallation installation)
    {
        if (!Directory.Exists(installation.UserDataPath)) return [];

        var metadata = ReadLoginUsers(installation.LoginUsersPath);
        var accounts = new List<SteamAccount>();

        foreach (var directory in Directory.EnumerateDirectories(installation.UserDataPath))
        {
            var folderName = Path.GetFileName(directory);

            // "0" and "anonymous" are Steam's own placeholders, not real accounts.
            if (!uint.TryParse(folderName, out var accountId) || accountId == 0) continue;

            metadata.TryGetValue(accountId, out var info);

            accounts.Add(new SteamAccount
            {
                AccountId = accountId,
                SteamId64 = info?.SteamId64 ?? SteamAccount.ToSteamId64(accountId),
                AccountName = info?.AccountName,
                PersonaName = info?.PersonaName,
                Timestamp = info?.Timestamp ?? 0,
                IsMostRecent = info?.IsMostRecent ?? false,
                UserDataPath = directory,
            });
        }

        return accounts
            .OrderByDescending(a => a.IsMostRecent)
            .ThenByDescending(a => a.Timestamp)
            .ThenByDescending(a => a.HasShortcuts)
            .ToList();
    }

    /// <summary>
    /// Picks the account to operate on. Honours an explicit choice when that account still
    /// exists, otherwise takes the most recently used.
    /// </summary>
    public SteamAccount? ResolveAccount(IReadOnlyList<SteamAccount> accounts, uint? preferredAccountId = null)
    {
        if (accounts.Count == 0) return null;

        if (preferredAccountId is { } preferred)
        {
            var match = accounts.FirstOrDefault(a => a.AccountId == preferred);
            if (match is not null) return match;
        }

        return accounts[0];
    }

    private sealed record LoginUser(ulong SteamId64, string? AccountName, string? PersonaName, long Timestamp, bool IsMostRecent);

    private static Dictionary<uint, LoginUser> ReadLoginUsers(string path)
    {
        var result = new Dictionary<uint, LoginUser>();
        if (!File.Exists(path)) return result;

        TextVdf.Node parsed;
        try
        {
            parsed = TextVdf.ParseFile(path);
        }
        catch (Exception e) when (e is IOException or VdfFormatException or UnauthorizedAccessException)
        {
            // Account names are cosmetic; failing to read them must not stop Nama working.
            return result;
        }

        var users = parsed["users"];
        if (users is null) return result;

        foreach (var (steamIdText, node) in users.Objects())
        {
            if (!ulong.TryParse(steamIdText, out var steamId64)) continue;

            result[SteamAccount.ToAccountId(steamId64)] = new LoginUser(
                steamId64,
                node.GetString("AccountName"),
                node.GetString("PersonaName"),
                node.GetInt64("Timestamp") ?? 0,
                node.GetString("MostRecent") == "1" || node.GetString("AutoLogin") == "1");
        }

        return result;
    }

    /// <summary>Reads an account's non-Steam shortcuts. Never throws for a missing file.</summary>
    public ShortcutsFile GetExistingShortcuts(SteamAccount account) => ShortcutsFile.Load(account.ShortcutsPath);

    /// <summary>
    /// Finds an existing entry for a game.
    /// <para>
    /// Executable path is checked first and is the reliable signal — the same game may
    /// have been added under a messier name. Display name is a fallback for the case where
    /// the user previously added it from a different copy of the files.
    /// </para>
    /// </summary>
    public SteamShortcut? DetectExistingEntry(ShortcutsFile file, string executablePath, string? displayName = null)
    {
        var target = NormalizePath(executablePath);

        foreach (var shortcut in file.Shortcuts)
        {
            if (NormalizePath(shortcut.ExePath) == target) return shortcut;
        }

        if (!string.IsNullOrWhiteSpace(displayName))
        {
            foreach (var shortcut in file.Shortcuts)
            {
                if (string.Equals(shortcut.AppName.Trim(), displayName.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    return shortcut;
                }
            }
        }

        return null;
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;

        try
        {
            return Path.GetFullPath(path.Trim('"')).TrimEnd('\\').ToLowerInvariant();
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path.Trim('"').TrimEnd('\\').ToLowerInvariant();
        }
    }

    /// <summary>
    /// True when the Steam client is running.
    /// <para>
    /// Steam keeps its own in-memory copy of shortcuts.vdf and rewrites the file on exit,
    /// so a write made while it is running is silently discarded. Every write path checks
    /// this first.
    /// </para>
    /// </summary>
    public bool IsSteamRunning() => Process.GetProcessesByName(SteamProcessName).Length > 0;

    /// <summary>
    /// Image extensions Steam will actually render. The grid folder also holds
    /// <c>{appid}.json</c> sidecars, which must not be mistaken for artwork.
    /// </summary>
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif", ".tga", ".ico",
    };

    /// <summary>Existing artwork files for a shortcut, keyed by the slot they occupy.</summary>
    public IReadOnlyDictionary<string, string> GetArtworkFiles(SteamAccount account, uint appId)
    {
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(account.GridPath)) return found;

        // Extensions vary per entry in a real install — .png and .jpg both occur for the
        // same slot — so match on the stem and accept whichever image extension is there.
        foreach (var (slot, stem) in ArtworkSlots(appId))
        {
            foreach (var file in Directory.EnumerateFiles(account.GridPath, stem + ".*"))
            {
                if (Path.GetFileNameWithoutExtension(file).Equals(stem, StringComparison.OrdinalIgnoreCase) &&
                    ImageExtensions.Contains(Path.GetExtension(file)))
                {
                    found[slot] = file;
                    break;
                }
            }
        }

        return found;
    }

    /// <summary>The grid-folder filename stem for each artwork slot.</summary>
    public static IEnumerable<(string Slot, string Stem)> ArtworkSlots(uint appId)
    {
        yield return ("Grid", appId.ToString());
        yield return ("Cover", appId + "p");
        yield return ("Hero", appId + "_hero");
        yield return ("Logo", appId + "_logo");
    }
}
