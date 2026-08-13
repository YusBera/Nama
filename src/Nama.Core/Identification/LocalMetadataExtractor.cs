using System.Diagnostics;

namespace Nama.Core.Identification;

/// <summary>
/// Reads name hints out of the file system and the executable's version resources.
/// Everything here is best-effort: a locked file, a missing folder or an executable
/// with no resources degrades to fewer hints rather than an error.
/// </summary>
public sealed class LocalMetadataExtractor
{
    /// <summary>Folders that sit between the install root and the executable in common engines.</summary>
    private static readonly HashSet<string> EngineFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "bin64", "binaries", "win64", "win32", "x64", "x86", "game", "games",
        "build", "builds", "release", "debug", "app", "application", "client",
        "data", "files", "system", "engine", "runtime", "redist", "launcher",
    };

    /// <summary>File extensions considered launchable when scanning a folder.</summary>
    private static readonly string[] ExecutableExtensions = [".exe", ".bat", ".cmd", ".lnk", ".url"];

    /// <summary>
    /// Resolves a user-supplied path — either an executable or a game folder — into a
    /// launch target plus the name hints Nama can derive from it.
    /// </summary>
    /// <exception cref="FileNotFoundException">No launchable executable could be found.</exception>
    public LocalMetadata Extract(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var full = Path.GetFullPath(path.Trim().Trim('"'));

        var executablePath = Directory.Exists(full)
            ? FindBestExecutable(full) ?? throw new FileNotFoundException(
                $"No executable found in \"{full}\". Pick the game's .exe directly.", full)
            : File.Exists(full)
                ? full
                : throw new FileNotFoundException($"\"{full}\" does not exist.", full);

        var startDirectory = Path.GetDirectoryName(executablePath) ?? full;
        var installRoot = FindInstallRoot(startDirectory, Directory.Exists(full) ? full : null);

        var target = new LocalGameTarget
        {
            ExecutablePath = executablePath,
            StartDirectory = startDirectory,
            InstallRoot = installRoot,
        };

        return new LocalMetadata { Target = target, Hints = CollectHints(target) };
    }

    /// <summary>
    /// Gathers every name hint, ordered by trust. Executable resources come first when
    /// they look like a real product name, otherwise folder names usually win because
    /// repackers rename folders less aggressively than executables.
    /// </summary>
    private static IReadOnlyList<NameHint> CollectHints(LocalGameTarget target)
    {
        var hints = new List<NameHint>();

        // Version resources. Useful when a studio filled them in, but they are whatever
        // the build toolchain left behind, so they rank below the folder the game sits in.
        try
        {
            var info = FileVersionInfo.GetVersionInfo(target.ExecutablePath);

            if (IsUsefulResourceName(info.ProductName))
                hints.Add(new NameHint(info.ProductName!.Trim(), NameHintOrigin.ExecutableProductName, 0.85));

            if (IsUsefulResourceName(info.FileDescription) &&
                !string.Equals(info.FileDescription?.Trim(), info.ProductName?.Trim(), StringComparison.OrdinalIgnoreCase))
                hints.Add(new NameHint(info.FileDescription!.Trim(), NameHintOrigin.ExecutableDescription, 0.70));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable file resources are not worth failing identification over.
        }

        var installRootName = target.InstallRootName;
        var parentName = new DirectoryInfo(target.StartDirectory).Name;
        var exeName = target.ExecutableName;

        // The install root folder is the strongest hint available. Someone deliberately
        // named it after the game; an executable's embedded ProductName is frequently a
        // toolchain default, a launcher stub's name, or the OS vendor's boilerplate.
        if (IsUsefulFolderName(installRootName))
            hints.Add(new NameHint(installRootName, NameHintOrigin.InstallRootFolder, 0.92));

        // The immediate parent only adds value when it differs from the root.
        if (!string.Equals(parentName, installRootName, StringComparison.OrdinalIgnoreCase) &&
            IsUsefulFolderName(parentName))
            hints.Add(new NameHint(parentName, NameHintOrigin.ParentFolder, 0.60));

        // The executable name is weak when it is a generic launcher stub.
        var exeWeight = NoiseLikeExecutable(exeName) ? 0.30 : 0.82;
        hints.Add(new NameHint(exeName, NameHintOrigin.ExecutableFileName, exeWeight));

        foreach (var sibling in FindSiblingHints(target.StartDirectory))
            hints.Add(sibling);

        // Highest trust first; de-duplicate case-insensitively, keeping the best weight.
        return hints
            .GroupBy(h => h.Value.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(h => h.Weight).First())
            .OrderByDescending(h => h.Weight)
            .ToList();
    }

    /// <summary>
    /// Looks for sibling artifacts that embed the title, most usefully Unity's
    /// <c>&lt;Title&gt;_Data</c> folder, which is named after the build's product name.
    /// </summary>
    private static IEnumerable<NameHint> FindSiblingHints(string directory)
    {
        DirectoryInfo[] subdirectories;
        try
        {
            subdirectories = new DirectoryInfo(directory).GetDirectories();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var dir in subdirectories)
        {
            if (!dir.Name.EndsWith("_Data", StringComparison.OrdinalIgnoreCase)) continue;

            var title = dir.Name[..^5];
            if (IsUsefulFolderName(title))
                yield return new NameHint(title, NameHintOrigin.SiblingFile, 0.88);
        }
    }

    /// <summary>
    /// Picks the most plausible game executable in a folder: prefers non-generic names,
    /// then larger files, since launcher stubs and crash handlers are typically small.
    /// </summary>
    public string? FindBestExecutable(string directory)
    {
        List<FileInfo> executables;
        try
        {
            executables = new DirectoryInfo(directory)
                .EnumerateFiles("*", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    MaxRecursionDepth = 4,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.System,
                })
                .Where(f => ExecutableExtensions.Contains(f.Extension, StringComparer.OrdinalIgnoreCase))
                .Take(2000)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (executables.Count == 0) return null;

        var folderName = new DirectoryInfo(directory).Name;

        return executables
            .OrderByDescending(f => ScoreExecutable(f, folderName, directory))
            .First()
            .FullName;
    }

    /// <summary>
    /// Ranks a candidate executable. Name similarity to the folder dominates, because a
    /// game folder and its main binary usually share a name.
    /// </summary>
    private static double ScoreExecutable(FileInfo file, string folderName, string root)
    {
        var name = Path.GetFileNameWithoutExtension(file.Name);
        var score = 0.0;

        if (NoiseLikeExecutable(name)) score -= 3.0;
        if (name.Contains("unins", StringComparison.OrdinalIgnoreCase)) score -= 6.0;
        if (name.Contains("crash", StringComparison.OrdinalIgnoreCase)) score -= 6.0;
        if (name.Contains("redist", StringComparison.OrdinalIgnoreCase)) score -= 6.0;
        if (name.Contains("setup", StringComparison.OrdinalIgnoreCase)) score -= 5.0;
        if (name.Contains("config", StringComparison.OrdinalIgnoreCase)) score -= 4.0;

        score += FuzzyMatcher.Similarity(name, folderName) * 4.0;

        // Files sitting directly in the chosen folder beat ones buried in subfolders.
        var depth = file.FullName[root.Length..].Count(c => c == Path.DirectorySeparatorChar);
        score -= depth * 0.6;

        // Size as a weak tiebreaker: real game binaries are rarely tiny.
        score += Math.Log10(Math.Max(file.Length, 1)) * 0.25;

        if (file.Extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)) score += 1.0;

        return score;
    }

    /// <summary>
    /// Walks up from the executable's folder past engine scaffolding to find the folder
    /// that actually carries the game's name. Never walks above a drive root or above
    /// an explicitly supplied folder.
    /// </summary>
    private static string FindInstallRoot(string startDirectory, string? userSuppliedFolder)
    {
        var current = new DirectoryInfo(startDirectory);

        // If the user handed Nama a folder, that folder is authoritative.
        if (userSuppliedFolder is not null)
            return Path.GetFullPath(userSuppliedFolder);

        for (var i = 0; i < 4; i++)
        {
            var parent = current.Parent;
            if (parent is null || parent.Parent is null) break;
            if (!EngineFolders.Contains(current.Name)) break;
            current = parent;
        }

        return current.FullName;
    }

    /// <summary>A resource string is useful only if it is non-empty and not the engine's default.</summary>
    private static bool IsUsefulResourceName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;

        var trimmed = value.Trim();
        if (trimmed.Length < 2 || trimmed.Length > 120) return false;

        // Engines and toolchains ship these placeholders unchanged in a lot of builds.
        string[] placeholders =
        [
            "unity", "unity technologies", "unreal engine", "ue4game", "ue5game",
            "unrealgame", "godot", "godot engine", "gamemaker", "renpy", "ren'py",
            "rpg maker", "electron", "nw.js", "nwjs", "chromium", "node.js",
            "microsoft windows", "microsoft corporation", "microsoft .net",
            "application", "game", "default", "launcher", "setup", "installer",
            "inno setup", "nullsoft install system", "7-zip", "winrar",
        ];

        if (placeholders.Any(p => trimmed.Equals(p, StringComparison.OrdinalIgnoreCase)))
            return false;

        // Runtime and OS boilerplate is never a game title. This catches the whole family
        // of strings like "Microsoft® Windows® Operating System" that a repacked or
        // stub executable inherits from whatever binary it was built from.
        string[] boilerplate =
        [
            "operating system", "runtime library", "redistributable",
            "visual c++", "directx", "© microsoft",
        ];

        return !boilerplate.Any(b => trimmed.Contains(b, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsUsefulFolderName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (name.Length < 2) return false;

        var trimmed = name.Trim();

        // Drive roots and generic library folders carry no title information.
        if (trimmed.EndsWith(':')) return false;
        if (EngineFolders.Contains(trimmed)) return false;

        string[] libraryFolders = ["games", "steamapps", "common", "program files", "program files (x86)", "downloads", "desktop"];
        return !libraryFolders.Contains(trimmed, StringComparer.OrdinalIgnoreCase);
    }

    private static bool NoiseLikeExecutable(string name) =>
        Normalization.NoisePatterns.GenericExecutableNames.Contains(name.Trim());
}
