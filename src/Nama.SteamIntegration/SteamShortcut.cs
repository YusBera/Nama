using Nama.Core.Models;
using Nama.SteamIntegration.Vdf;

namespace Nama.SteamIntegration;

/// <summary>
/// One entry in <c>shortcuts.vdf</c>, exposed as a plain object so nothing outside this
/// project has to know the file format.
/// </summary>
public sealed class SteamShortcut
{
    /// <summary>Signed value stored in the VDF <c>appid</c> field.</summary>
    public int AppId { get; set; }

    /// <summary>Display name shown in the Steam library.</summary>
    public string AppName { get; set; } = string.Empty;

    /// <summary>Target executable, stored quoted exactly as Steam writes it.</summary>
    public string Exe { get; set; } = string.Empty;

    public string StartDir { get; set; } = string.Empty;
    public string Icon { get; set; } = string.Empty;
    public string ShortcutPath { get; set; } = string.Empty;
    public string LaunchOptions { get; set; } = string.Empty;

    public bool IsHidden { get; set; }
    public bool AllowDesktopConfig { get; set; } = true;
    public bool AllowOverlay { get; set; } = true;
    public bool OpenVr { get; set; }
    public bool Devkit { get; set; }
    public string DevkitGameId { get; set; } = string.Empty;
    public int DevkitOverrideAppId { get; set; }
    public int LastPlayTime { get; set; }
    public string FlatpakAppId { get; set; } = string.Empty;

    public List<string> Tags { get; set; } = [];

    /// <summary>Fields Nama did not model, preserved verbatim so nothing is lost on rewrite.</summary>
    public List<KeyValuePair<string, VdfNode>> UnknownFields { get; } = [];

    /// <summary>The unsigned id used for artwork file names.</summary>
    public uint ArtworkId => SteamAppIds.ToUnsigned(AppId);

    /// <summary>Target path with Steam's surrounding quotes removed.</summary>
    public string ExePathUnquoted => Exe.Trim().Trim('"');

    /// <summary>Recomputes <see cref="AppId"/> from the current target and name.</summary>
    public void RefreshAppId() => AppId = SteamAppIds.ComputeShortcutAppIdSigned(Exe, AppName);

    /// <summary>Builds a shortcut for a local game, filling in Steam's usual defaults.</summary>
    public static SteamShortcut Create(string executablePath, string startDirectory, string displayName)
    {
        var shortcut = new SteamShortcut
        {
            AppName = displayName,
            Exe = SteamAppIds.QuotePath(executablePath),
            StartDir = SteamAppIds.QuotePath(startDirectory),
            Icon = string.Empty,
            ShortcutPath = string.Empty,
            LaunchOptions = string.Empty,
            AllowDesktopConfig = true,
            AllowOverlay = true,
            Tags = [],
        };

        shortcut.RefreshAppId();
        return shortcut;
    }

    internal static SteamShortcut FromVdf(VdfNode node)
    {
        var shortcut = new SteamShortcut
        {
            AppId = node.GetInt("appid"),
            AppName = node.GetString("AppName"),
            Exe = node.GetString("Exe"),
            StartDir = node.GetString("StartDir"),
            Icon = node.GetString("icon"),
            ShortcutPath = node.GetString("ShortcutPath"),
            LaunchOptions = node.GetString("LaunchOptions"),
            IsHidden = node.GetBool("IsHidden"),
            AllowDesktopConfig = node.GetBool("AllowDesktopConfig", true),
            AllowOverlay = node.GetBool("AllowOverlay", true),
            OpenVr = node.GetBool("OpenVR"),
            Devkit = node.GetBool("Devkit"),
            DevkitGameId = node.GetString("DevkitGameID"),
            DevkitOverrideAppId = node.GetInt("DevkitOverrideAppID"),
            LastPlayTime = node.GetInt("LastPlayTime"),
            FlatpakAppId = node.GetString("FlatpakAppID"),
        };

        if (node["tags"] is { Kind: VdfKind.Object } tags)
            shortcut.Tags = tags.Children.Select(c => c.Value.StringValue ?? string.Empty).ToList();

        // Older clients wrote "AppName" as "appname"; either way the property above
        // handles it, so only genuinely unrecognized keys are carried forward.
        foreach (var (key, value) in node.Children)
            if (!KnownKeys.Contains(key))
                shortcut.UnknownFields.Add(new KeyValuePair<string, VdfNode>(key, value));

        return shortcut;
    }

    private static readonly HashSet<string> KnownKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "appid", "AppName", "Exe", "StartDir", "icon", "ShortcutPath", "LaunchOptions",
        "IsHidden", "AllowDesktopConfig", "AllowOverlay", "OpenVR", "Devkit",
        "DevkitGameID", "DevkitOverrideAppID", "LastPlayTime", "FlatpakAppID", "tags",
    };

    internal VdfNode ToVdf()
    {
        var node = VdfNode.NewObject();

        node.Set("appid", AppId);
        node.Set("AppName", AppName);
        node.Set("Exe", Exe);
        node.Set("StartDir", StartDir);
        node.Set("icon", Icon);
        node.Set("ShortcutPath", ShortcutPath);
        node.Set("LaunchOptions", LaunchOptions);
        node.Set("IsHidden", IsHidden);
        node.Set("AllowDesktopConfig", AllowDesktopConfig);
        node.Set("AllowOverlay", AllowOverlay);
        node.Set("OpenVR", OpenVr);
        node.Set("Devkit", Devkit);
        node.Set("DevkitGameID", DevkitGameId);
        node.Set("DevkitOverrideAppID", DevkitOverrideAppId);
        node.Set("LastPlayTime", LastPlayTime);
        node.Set("FlatpakAppID", FlatpakAppId);

        foreach (var (key, value) in UnknownFields)
            node.Set(key, value);

        var tags = VdfNode.NewObject();
        for (var i = 0; i < Tags.Count; i++)
            tags.Add(i.ToString(), VdfNode.FromString(Tags[i]));
        node.Set("tags", tags);

        return node;
    }
}

/// <summary>An existing library entry that matches the game the user is adding.</summary>
/// <param name="Shortcut">The entry already present in shortcuts.vdf.</param>
/// <param name="MatchKind">Why Nama considers it a match.</param>
public readonly record struct ExistingEntry(SteamShortcut Shortcut, DuplicateMatch MatchKind);

public enum DuplicateMatch
{
    /// <summary>Same target executable — almost certainly the same game.</summary>
    SameExecutable,

    /// <summary>Same display name pointing somewhere else.</summary>
    SameName,

    /// <summary>Same computed app id, meaning Steam would treat them as one entry.</summary>
    SameAppId,
}

/// <summary>What Nama did when committing a game to the library.</summary>
public enum ShortcutAction
{
    Created,
    Updated,
}

/// <summary>Result of writing a shortcut and its artwork.</summary>
public sealed class AddGameResult
{
    public required SteamShortcut Shortcut { get; init; }
    public required ShortcutAction Action { get; init; }

    /// <summary>Artwork types that were written successfully.</summary>
    public required IReadOnlyList<ArtworkType> AppliedArtwork { get; init; }

    /// <summary>Artwork types that were selected but could not be written, with the reason.</summary>
    public IReadOnlyList<(ArtworkType Type, string Reason)> FailedArtwork { get; init; } = [];

    /// <summary>True when Steam was running, meaning the user must restart it to see changes.</summary>
    public bool RequiresSteamRestart { get; init; }
}
