using Nama.Steam.Vdf;

namespace Nama.Steam.Models;

/// <summary>A located Steam client installation.</summary>
public sealed record SteamInstallation(string Path)
{
    public string ConfigPath => System.IO.Path.Combine(Path, "config");

    public string UserDataPath => System.IO.Path.Combine(Path, "userdata");

    public string LoginUsersPath => System.IO.Path.Combine(ConfigPath, "loginusers.vdf");

    public string ClientExecutablePath => System.IO.Path.Combine(Path, "steam.exe");
}

/// <summary>
/// One local Steam account. Shortcuts and artwork are per-account, so picking the right
/// one is the difference between the game appearing and silently going nowhere.
/// </summary>
public sealed record SteamAccount
{
    /// <summary>steamID3 — the numeric <c>userdata</c> folder name.</summary>
    public required uint AccountId { get; init; }

    /// <summary>steamID64, as keyed in <c>loginusers.vdf</c>. Zero when the account is not in that file.</summary>
    public ulong SteamId64 { get; init; }

    public string? AccountName { get; init; }

    public string? PersonaName { get; init; }

    /// <summary>Last login time from <c>loginusers.vdf</c>. Used to pick a default.</summary>
    public long Timestamp { get; init; }

    /// <summary>Set by Steam's <c>MostRecent</c> or <c>AutoLogin</c> flag.</summary>
    public bool IsMostRecent { get; init; }

    public required string UserDataPath { get; init; }

    public string ConfigPath => System.IO.Path.Combine(UserDataPath, "config");

    public string ShortcutsPath => System.IO.Path.Combine(ConfigPath, "shortcuts.vdf");

    public string GridPath => System.IO.Path.Combine(ConfigPath, "grid");

    /// <summary>True when this account already has non-Steam shortcuts.</summary>
    public bool HasShortcuts => File.Exists(ShortcutsPath);

    public string Label => PersonaName ?? AccountName ?? AccountId.ToString();

    /// <summary>Converts a steamID64 to the steamID3 used as the userdata folder name.</summary>
    public static uint ToAccountId(ulong steamId64) => (uint)(steamId64 - 76561197960265728UL);

    public static ulong ToSteamId64(uint accountId) => accountId + 76561197960265728UL;
}

/// <summary>
/// A single non-Steam shortcut, backed by its node in the parsed tree. Property setters
/// write straight through, so fields Nama does not model are carried along untouched.
/// </summary>
public sealed class SteamShortcut(VdfMap node)
{
    internal VdfMap Node { get; } = node;

    /// <summary>The display name shown in the Steam library.</summary>
    public string AppName
    {
        get => Node.GetString("AppName") ?? string.Empty;
        set => Node.SetString("AppName", value);
    }

    /// <summary>Target executable, normally stored quoted. Kept verbatim — the app id depends on it.</summary>
    public string Exe
    {
        get => Node.GetString("Exe") ?? string.Empty;
        set => Node.SetString("Exe", value);
    }

    public string StartDir
    {
        get => Node.GetString("StartDir") ?? string.Empty;
        set => Node.SetString("StartDir", value);
    }

    /// <summary>Absolute path to an icon file. Steam does not copy it, so it must stay put.</summary>
    public string Icon
    {
        get => Node.GetString("icon") ?? string.Empty;
        set => Node.SetString("icon", value);
    }

    public string LaunchOptions
    {
        get => Node.GetString("LaunchOptions") ?? string.Empty;
        set => Node.SetString("LaunchOptions", value);
    }

    /// <summary>The raw signed <c>appid</c> field.</summary>
    public int AppIdField
    {
        get => Node.GetInt32("appid") ?? 0;
        set => Node.SetInt32("appid", value);
    }

    /// <summary>The stored app id as unsigned — this is what artwork filenames use.</summary>
    public uint AppId => SteamAppId.FromShortcutField(AppIdField);

    /// <summary>The app id implied by the current Exe and AppName.</summary>
    public uint ComputedAppId => SteamAppId.Compute(Exe, AppName);

    /// <summary>Executable path with Steam's quoting removed.</summary>
    public string ExePath => Exe.Trim('"');
}

/// <summary>
/// A loaded <c>shortcuts.vdf</c>, together with the bytes it came from.
/// </summary>
public sealed class ShortcutsFile
{
    private ShortcutsFile(string path, VdfMap root, VdfMap container, byte[] originalBytes, bool roundTrips)
    {
        Path = path;
        Root = root;
        Container = container;
        OriginalBytes = originalBytes;
        RoundTrips = roundTrips;
    }

    public string Path { get; }

    /// <summary>The implicit root map.</summary>
    public VdfMap Root { get; }

    /// <summary>The <c>shortcuts</c> map, whose children are keyed "0", "1", …</summary>
    public VdfMap Container { get; }

    /// <summary>Bytes as read from disk. Empty for a file that did not exist.</summary>
    public byte[] OriginalBytes { get; }

    /// <summary>
    /// False when re-serializing the parsed tree did not reproduce the original bytes.
    /// The write path must refuse to run in that case — see <see cref="BinaryVdf.RoundTrips"/>.
    /// </summary>
    public bool RoundTrips { get; }

    public bool Existed => OriginalBytes.Length > 0;

    public IReadOnlyList<SteamShortcut> Shortcuts =>
        Container.ChildMaps().Select(child => new SteamShortcut(child.Map)).ToList();

    /// <summary>
    /// Reads a shortcuts file, or produces an empty one if it does not exist yet — a
    /// perfectly normal state for an account that has never added a non-Steam game.
    /// </summary>
    public static ShortcutsFile Load(string path)
    {
        if (!File.Exists(path)) return CreateEmpty(path);

        var bytes = File.ReadAllBytes(path);
        if (bytes.Length == 0) return CreateEmpty(path);

        var roundTrips = BinaryVdf.RoundTrips(bytes, out var parsed);

        if (parsed is null)
        {
            // Unparseable. Loading must still succeed so the caller can report a clear
            // reason rather than taking an exception — and RoundTrips being false blocks
            // every write, so the file on disk is safe from us.
            var placeholder = new VdfMap();
            var empty = new VdfMap();
            placeholder.Add("shortcuts", empty);

            return new ShortcutsFile(path, placeholder, empty, bytes, roundTrips: false);
        }

        if (!parsed.TryGetMap("shortcuts", out var container))
        {
            container = new VdfMap();
            parsed.Set("shortcuts", container);
        }

        return new ShortcutsFile(path, parsed, container, bytes, roundTrips);
    }

    private static ShortcutsFile CreateEmpty(string path)
    {
        var root = new VdfMap();
        var container = new VdfMap();
        root.Add("shortcuts", container);

        // A file that does not exist cannot be damaged, so writing is always safe.
        return new ShortcutsFile(path, root, container, [], roundTrips: true);
    }

    /// <summary>Serializes the current tree.</summary>
    public byte[] Serialize() => BinaryVdf.Write(Root);

    /// <summary>The next free numeric key for a new entry.</summary>
    public string NextKey()
    {
        var highest = -1;
        foreach (var entry in Container.Entries)
        {
            if (int.TryParse(entry.Key, out var index) && index > highest) highest = index;
        }

        return (highest + 1).ToString();
    }
}
