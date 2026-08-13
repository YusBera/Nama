namespace Nama.Core.Identification;

/// <summary>
/// What the user handed to Nama: an executable, plus the folder context around it.
/// </summary>
public sealed class LocalGameTarget
{
    /// <summary>Absolute path to the executable that will be launched.</summary>
    public required string ExecutablePath { get; init; }

    /// <summary>Working directory for the shortcut. Normally the executable's folder.</summary>
    public required string StartDirectory { get; init; }

    /// <summary>
    /// The folder considered to be the game's install root. For nested layouts like
    /// <c>Game/Binaries/Win64/game.exe</c> this walks up past engine folders.
    /// </summary>
    public required string InstallRoot { get; init; }

    public string ExecutableName => Path.GetFileNameWithoutExtension(ExecutablePath);

    public string InstallRootName => new DirectoryInfo(InstallRoot).Name;
}

/// <summary>
/// Name hints pulled out of the file system and executable resources, ranked by how
/// much Nama trusts them.
/// </summary>
public sealed class LocalMetadata
{
    public required LocalGameTarget Target { get; init; }

    /// <summary>Every hint found, best first.</summary>
    public required IReadOnlyList<NameHint> Hints { get; init; }

    /// <summary>The single best raw name to normalize and search with.</summary>
    public string PrimaryRawName => Hints.Count > 0 ? Hints[0].Value : Target.ExecutableName;
}

/// <summary>A candidate raw name plus where it came from and how much it is trusted.</summary>
/// <param name="Value">The raw, un-normalized name.</param>
/// <param name="Origin">Where it was read from.</param>
/// <param name="Weight">Trust in [0,1]; used to order hints and seed confidence.</param>
public readonly record struct NameHint(string Value, NameHintOrigin Origin, double Weight)
{
    public override string ToString() => $"{Value} ({Origin})";
}

public enum NameHintOrigin
{
    /// <summary>The <c>ProductName</c> resource compiled into the executable.</summary>
    ExecutableProductName,

    /// <summary>The <c>FileDescription</c> resource compiled into the executable.</summary>
    ExecutableDescription,

    /// <summary>The executable's own file name.</summary>
    ExecutableFileName,

    /// <summary>The name of the folder holding the executable.</summary>
    ParentFolder,

    /// <summary>The name of the detected install root folder.</summary>
    InstallRootFolder,

    /// <summary>A sibling file or folder that names the game, e.g. <c>Game_Data</c>.</summary>
    SiblingFile,

    /// <summary>Typed by the user in the search box.</summary>
    UserInput,
}
