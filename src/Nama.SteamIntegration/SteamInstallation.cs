using System.Diagnostics;
using Microsoft.Win32;
using Nama.SteamIntegration.Vdf;

namespace Nama.SteamIntegration;

/// <summary>A discovered Steam installation.</summary>
public sealed class SteamInstallation
{
    /// <summary>Root of the Steam install, e.g. <c>C:\Program Files (x86)\Steam</c>.</summary>
    public required string InstallPath { get; init; }

    /// <summary>Local user profiles found under <c>userdata</c>.</summary>
    public required IReadOnlyList<SteamUser> Users { get; init; }

    public string UserDataPath => Path.Combine(InstallPath, "userdata");

    public string SteamExecutable => Path.Combine(InstallPath, "steam.exe");

    /// <summary>True when a Steam process is currently running.</summary>
    public static bool IsSteamRunning()
    {
        try
        {
            return Process.GetProcessesByName("steam").Length > 0;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}

/// <summary>A local Steam account with its own shortcuts and artwork folders.</summary>
public sealed class SteamUser
{
    /// <summary>The 32-bit account id, which is also the <c>userdata</c> folder name.</summary>
    public required string AccountId { get; init; }

    /// <summary>Profile name from <c>loginusers.vdf</c>, when it could be read.</summary>
    public string? PersonaName { get; init; }

    /// <summary>Absolute path to this user's <c>config</c> folder.</summary>
    public required string ConfigPath { get; init; }

    /// <summary>True for the account Steam logged into most recently.</summary>
    public bool IsMostRecent { get; init; }

    public string ShortcutsFile => Path.Combine(ConfigPath, "shortcuts.vdf");

    /// <summary>Folder Steam reads custom artwork from.</summary>
    public string GridPath => Path.Combine(ConfigPath, "grid");

    public string DisplayLabel => string.IsNullOrWhiteSpace(PersonaName) ? AccountId : PersonaName!;

    public override string ToString() => DisplayLabel;
}

/// <summary>
/// Locates Steam and its local user profiles. Every lookup is best-effort: a missing
/// registry key or an unreadable file narrows the results rather than throwing.
/// </summary>
public static class SteamLocator
{
    /// <summary>
    /// Finds the Steam installation, or null when Steam is not installed.
    /// </summary>
    /// <param name="overridePath">A user-supplied path that wins over auto-detection.</param>
    public static SteamInstallation? FindSteamInstallation(string? overridePath = null)
    {
        var installPath = ResolveInstallPath(overridePath);
        if (installPath is null) return null;

        return new SteamInstallation
        {
            InstallPath = installPath,
            Users = FindUsers(installPath),
        };
    }

    private static string? ResolveInstallPath(string? overridePath)
    {
        if (!string.IsNullOrWhiteSpace(overridePath) && LooksLikeSteam(overridePath))
            return Path.GetFullPath(overridePath);

        foreach (var candidate in EnumerateCandidatePaths())
            if (LooksLikeSteam(candidate))
                return Path.GetFullPath(candidate);

        return null;
    }

    private static IEnumerable<string> EnumerateCandidatePaths()
    {
        // The per-user key is the most reliable: it reflects the running client.
        var fromUser = ReadRegistry(Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath");
        if (fromUser is not null) yield return fromUser.Replace('/', '\\');

        var fromMachine = ReadRegistry(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath")
                          ?? ReadRegistry(Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath");
        if (fromMachine is not null) yield return fromMachine;

        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam");
    }

    private static string? ReadRegistry(RegistryKey root, string subKey, string valueName)
    {
        try
        {
            using var key = root.OpenSubKey(subKey);
            return key?.GetValue(valueName) as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    /// <summary>A folder counts as a Steam install if it has the pieces Nama needs.</summary>
    private static bool LooksLikeSteam(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        try
        {
            if (!Directory.Exists(path)) return false;

            return File.Exists(Path.Combine(path, "steam.exe")) ||
                   Directory.Exists(Path.Combine(path, "userdata")) ||
                   Directory.Exists(Path.Combine(path, "steamapps"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Enumerates local accounts, ordering the most recently used first so the UI can
    /// default to the account the user is actually logged into.
    /// </summary>
    public static IReadOnlyList<SteamUser> FindUsers(string installPath)
    {
        var userDataPath = Path.Combine(installPath, "userdata");
        if (!Directory.Exists(userDataPath)) return [];

        var mostRecentAccountId = FindMostRecentAccountId(installPath);
        var personaNames = ReadPersonaNames(installPath);
        var users = new List<SteamUser>();

        IEnumerable<string> directories;
        try
        {
            directories = Directory.EnumerateDirectories(userDataPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        foreach (var directory in directories)
        {
            var accountId = new DirectoryInfo(directory).Name;

            // "0" and "anonymous" are placeholder profiles with no real config.
            if (!uint.TryParse(accountId, out var numericId) || numericId == 0) continue;

            var configPath = Path.Combine(directory, "config");
            if (!Directory.Exists(configPath)) continue;

            users.Add(new SteamUser
            {
                AccountId = accountId,
                ConfigPath = configPath,
                PersonaName = personaNames.GetValueOrDefault(accountId),
                IsMostRecent = accountId == mostRecentAccountId,
            });
        }

        return users
            .OrderByDescending(u => u.IsMostRecent)
            .ThenByDescending(u => LastWrite(u.ShortcutsFile))
            .ToList();
    }

    private static DateTime LastWrite(string path)
    {
        try
        {
            return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DateTime.MinValue;
        }
    }

    /// <summary>
    /// Determines the active account. Prefers the running client's registry value, then
    /// falls back to the <c>MostRecent</c> flag in <c>loginusers.vdf</c>.
    /// </summary>
    private static string? FindMostRecentAccountId(string installPath)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam\ActiveProcess");
            if (key?.GetValue("ActiveUser") is int activeUser && activeUser != 0)
                return activeUser.ToString();
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // Fall through to loginusers.vdf.
        }

        foreach (var (accountId, _, isMostRecent) in ReadLoginUsers(installPath))
            if (isMostRecent)
                return accountId;

        return null;
    }

    private static Dictionary<string, string> ReadPersonaNames(string installPath)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (accountId, personaName, _) in ReadLoginUsers(installPath))
            if (!string.IsNullOrWhiteSpace(personaName))
                names[accountId] = personaName;

        return names;
    }

    /// <summary>
    /// Reads <c>config\loginusers.vdf</c>, converting each 64-bit Steam ID to the 32-bit
    /// account id that names the <c>userdata</c> folder.
    /// </summary>
    private static IEnumerable<(string AccountId, string? PersonaName, bool IsMostRecent)> ReadLoginUsers(string installPath)
    {
        // SteamID64 = accountId + this base value for individual accounts.
        const ulong individualBase = 76561197960265728ul;

        var path = Path.Combine(installPath, "config", "loginusers.vdf");

        VdfNode root;
        try
        {
            if (!File.Exists(path)) yield break;
            root = TextVdf.ParseFile(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            yield break;
        }

        var users = root["users"];
        if (users is null) yield break;

        foreach (var (steamId64, node) in users.Children)
        {
            if (!ulong.TryParse(steamId64, out var id64) || id64 < individualBase) continue;

            var accountId = (id64 - individualBase).ToString();
            var personaName = node.GetString("PersonaName");
            var isMostRecent = node.GetString("MostRecent") == "1";

            yield return (accountId, string.IsNullOrWhiteSpace(personaName) ? null : personaName, isMostRecent);
        }
    }
}
