using Nama.Core.Models;

namespace Nama.Core.Identification;

/// <summary>Where a local name guess came from. Determines how much it is trusted.</summary>
public enum CandidateOrigin
{
    /// <summary>The executable's own filename.</summary>
    ExecutableName,

    /// <summary>The folder directly containing the executable.</summary>
    FolderName,

    /// <summary>A folder further up, used when the immediate parent is a build directory.</summary>
    ParentFolderName,

    /// <summary>ProductName or FileDescription from the executable's version resource.</summary>
    ExecutableMetadata,

    /// <summary>A neighbouring file whose name carries the title, typically a .url shortcut.</summary>
    SiblingFile,
}

/// <summary>One searchable title derived from the selected file, with its provenance.</summary>
/// <param name="Value">The search term.</param>
/// <param name="Origin">Where it came from.</param>
/// <param name="Weight">Trust, 0..1. Multiplied into the match score.</param>
/// <param name="RawSource">The untouched text it was derived from, for display and diagnostics.</param>
public sealed record LocalNameCandidate(string Value, CandidateOrigin Origin, double Weight, string RawSource);

/// <summary>Everything Nama could learn about a game from the filesystem alone.</summary>
public sealed class ExtractionResult
{
    /// <summary>The executable Steam will launch.</summary>
    public required string ExecutablePath { get; init; }

    /// <summary>Working directory for the shortcut, normally the executable's folder.</summary>
    public required string StartDirectory { get; init; }

    /// <summary>Search terms, highest weight first, de-duplicated.</summary>
    public required IReadOnlyList<LocalNameCandidate> Candidates { get; init; }

    /// <summary>Normalization of the strongest candidate. Supplies the default display name.</summary>
    public required NameAnalysis Primary { get; init; }

    /// <summary>True when the executable's own name carried no title information.</summary>
    public bool ExecutableNameWasGeneric { get; init; }

    /// <summary>Set when a folder was selected and no executable could be found inside it.</summary>
    public string? Warning { get; init; }

    public string BestGuess => Primary.DisplayName;
}
