using Nama.Core.Models;

namespace Nama.Steam.Writing;

/// <summary>What the caller wants written.</summary>
public sealed record ShortcutRequest
{
    /// <summary>Executable to launch. Stored quoted.</summary>
    public required string ExecutablePath { get; init; }

    /// <summary>Steam library display name. Already user-edited by this point.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Working directory. Defaults to the executable's folder.</summary>
    public string? StartDirectory { get; init; }

    public string? LaunchOptions { get; init; }

    /// <summary>Chosen artwork, at most one per slot.</summary>
    public IReadOnlyDictionary<ArtworkType, Artwork> Artwork { get; init; } =
        new Dictionary<ArtworkType, Artwork>();

    /// <summary>What to do when the game is already in the library.</summary>
    public DuplicateAction OnDuplicate { get; init; } = DuplicateAction.Fail;
}

/// <summary>How to handle a game that already has a shortcut. Never silently duplicated.</summary>
public enum DuplicateAction
{
    /// <summary>Refuse and report. The caller is expected to ask the user.</summary>
    Fail,

    /// <summary>Keep the entry exactly as it is and only replace its artwork.</summary>
    UpdateArtwork,

    /// <summary>Overwrite name, target and artwork on the existing entry, in place.</summary>
    ReplaceEntry,
}

/// <summary>Why writing is not currently possible.</summary>
public enum WriteBlockReason
{
    None,

    /// <summary>Steam is running and would overwrite the file on exit.</summary>
    SteamRunning,

    /// <summary>The file did not re-serialize identically, so Nama does not fully understand it.</summary>
    RoundTripFailed,

    /// <summary>The shortcuts file or its folder cannot be written to.</summary>
    NotWritable,

    /// <summary>No Steam account could be resolved.</summary>
    NoAccount,
}

/// <summary>Whether a write may proceed, and why not if it may not.</summary>
public sealed record WriteReadiness(bool CanWrite, WriteBlockReason Reason, string Message)
{
    public static WriteReadiness Ready { get; } = new(true, WriteBlockReason.None, "Ready.");

    public static WriteReadiness Blocked(WriteBlockReason reason, string message) => new(false, reason, message);
}

/// <summary>Outcome of applying artwork.</summary>
public sealed record ArtworkApplyReport
{
    /// <summary>Slots successfully written, with their file paths.</summary>
    public required IReadOnlyDictionary<ArtworkType, string> Applied { get; init; }

    /// <summary>Slots that could not be fetched or written, with the reason.</summary>
    public required IReadOnlyDictionary<ArtworkType, string> Failed { get; init; }

    /// <summary>Absolute path of the icon file, if an icon was applied.</summary>
    public string? IconPath { get; init; }
}

/// <summary>Outcome of a shortcut write.</summary>
public sealed record WriteResult
{
    public required bool Success { get; init; }

    public string? Error { get; init; }

    public WriteBlockReason BlockReason { get; init; } = WriteBlockReason.None;

    /// <summary>The app id the entry uses. Artwork filenames are derived from this.</summary>
    public uint AppId { get; init; }

    /// <summary>True when an existing entry was modified rather than a new one created.</summary>
    public bool WasUpdate { get; init; }

    /// <summary>Where the previous shortcuts.vdf was saved.</summary>
    public string? BackupPath { get; init; }

    /// <summary>True when nothing was written to disk.</summary>
    public bool DryRun { get; init; }

    /// <summary>Set when a duplicate was found and <see cref="DuplicateAction.Fail"/> was in effect.</summary>
    public SteamShortcutSummary? ExistingEntry { get; init; }

    public ArtworkApplyReport? Artwork { get; init; }

    /// <summary>Human-readable summary of what a dry run would have done.</summary>
    public IReadOnlyList<string> PlannedActions { get; init; } = [];

    public static WriteResult Blocked(WriteReadiness readiness) => new()
    {
        Success = false,
        Error = readiness.Message,
        BlockReason = readiness.Reason,
    };
}

/// <summary>A snapshot of an existing entry, for reporting a duplicate without exposing the tree.</summary>
public sealed record SteamShortcutSummary(string AppName, string ExecutablePath, uint AppId);
