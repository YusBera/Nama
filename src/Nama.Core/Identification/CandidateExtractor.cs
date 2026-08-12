using System.Diagnostics;
using Nama.Core.Models;
using Nama.Core.Normalization;

namespace Nama.Core.Identification;

/// <summary>
/// Works out what to search for, given nothing but a path.
/// <para>
/// No single signal is reliable. An executable is often named after its engine rather than
/// its game (<c>onscripter-ru.exe</c>, <c>Game.exe</c>), a folder is often a repack string,
/// and version metadata is frequently blank or wrong. So every source contributes a
/// weighted guess and the provider results decide between them.
/// </para>
/// </summary>
public sealed partial class CandidateExtractor(NormalizationData? data = null, NameNormalizer? normalizer = null)
{
    private readonly NormalizationData _data = data ?? NormalizationData.Default;
    private readonly NameNormalizer _normalizer = normalizer ?? new NameNormalizer(data);

    /// <summary>Executables that are never the game itself.</summary>
    private static readonly string[] NeverTheGame =
    [
        "unins", "uninstall", "setup", "install", "vcredist", "directx", "dxsetup",
        "dotnetfx", "crashhandler", "crashreport", "crashpad", "ue4prereqsetup",
        "ue prereqsetup", "oalinst", "config", "settings", "updater", "patcher",
        "redist", "activation", "readme", "help", "support", "benchmark",
    ];

    /// <summary>Extracts search terms from a file or folder path.</summary>
    public ExtractionResult Extract(string path)
    {
        path = path.Trim().Trim('"');

        var (executable, warning) = ResolveExecutable(path);
        var directory = executable is not null
            ? Path.GetDirectoryName(executable) ?? path
            : path;

        var raw = new List<(string Text, CandidateOrigin Origin, double Weight)>();

        var executableStem = executable is not null ? Path.GetFileNameWithoutExtension(executable) : null;
        var folderName = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar));

        var isGeneric = executableStem is not null && IsUninformativeExecutableName(executableStem, folderName);

        if (executableStem is not null)
        {
            // A generic or engine name is kept, but demoted far below the folder — it is
            // occasionally still right (a game genuinely called "Limbo.exe").
            raw.Add((executableStem, CandidateOrigin.ExecutableName, isGeneric ? 0.25 : 0.90));
        }

        AddFolderCandidates(directory, raw, boostFolder: isGeneric || executableStem is null);
        AddMetadataCandidates(executable, raw);
        AddSiblingCandidates(directory, raw);
        AddStoreCodeCandidates(directory, raw);

        return new ExtractionResult
        {
            ExecutablePath = executable ?? path,
            StartDirectory = directory,
            Candidates = BuildCandidates(raw, out var primary),
            Primary = primary,
            ExecutableNameWasGeneric = isGeneric,
            Warning = warning,
        };
    }

    /// <summary>True when an executable's name is in the known generic/engine list.</summary>
    public bool IsGenericExecutableName(string stem) => _data.GenericExecutables.Contains(stem);

    /// <summary>
    /// True when the executable's name should not be trusted as the title.
    /// <para>
    /// Beyond the known-names list, two heuristics catch the cases that list cannot
    /// enumerate. Both were prompted by real files in this library.
    /// </para>
    /// </summary>
    public bool IsUninformativeExecutableName(string stem, string? folderName)
    {
        if (IsGenericExecutableName(stem)) return true;

        // A Japanese game whose executable is plain ASCII: the binary is the engine, the
        // folder is the title. "らぶらぼ\bolero01sys.exe" is exactly this shape, and
        // without the rule the engine name wins.
        if (folderName is not null && TextTools.ContainsCjk(folderName) && !TextTools.ContainsCjk(stem)) return true;

        return LooksLikeEngineBinary(stem);
    }

    /// <summary>
    /// Matches word + padded digits + word, the shape of engine and per-title runtime
    /// binaries ("bolero01sys", "advdata02win"). Deliberately narrow: it must not catch
    /// real titles like "Portal2", "re4" or "l4d2", so it requires at least two digits
    /// with letters on both sides.
    /// </summary>
    public static bool LooksLikeEngineBinary(string stem) => EngineBinaryPattern.IsMatch(stem);

    [System.Text.RegularExpressions.GeneratedRegex(
        @"^[a-z]{3,}\d{2,}[a-z]{2,}$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex EngineBinaryPattern { get; }

    /// <summary>True when a folder is a container for games rather than one game's folder.</summary>
    public bool IsLibraryFolder(string name) => _data.LibraryDirectories.Contains(name.Trim());

    // --- executable resolution --------------------------------------------------------

    private (string? Executable, string? Warning) ResolveExecutable(string path)
    {
        if (File.Exists(path)) return (path, null);

        if (!Directory.Exists(path)) return (null, $"'{path}' does not exist.");

        var executable = FindMainExecutable(path);
        return executable is not null
            ? (executable, null)
            : (null, $"No executable found in '{Path.GetFileName(path)}'.");
    }

    /// <summary>
    /// Picks the most likely game executable in a folder.
    /// <para>
    /// Prefers shallow files over deeply nested ones, names resembling the folder, and
    /// larger binaries — installers and crash handlers are small and usually sit beside
    /// the real game.
    /// </para>
    /// </summary>
    public string? FindMainExecutable(string directory)
    {
        List<string> executables;
        try
        {
            executables = Directory
                .EnumerateFiles(directory, "*.exe", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    MaxRecursionDepth = 3,
                    IgnoreInaccessible = true,
                })
                .Take(400)
                .ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (executables.Count == 0) return null;

        var folderKey = TextTools.Compact(Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar)));

        return executables
            .Select(file => (File: file, Score: ScoreExecutable(file, directory, folderKey)))
            .Where(x => x.Score > double.MinValue)
            .OrderByDescending(x => x.Score)
            .Select(x => x.File)
            .FirstOrDefault();
    }

    private double ScoreExecutable(string file, string root, string folderKey)
    {
        var stem = Path.GetFileNameWithoutExtension(file);
        var lower = stem.ToLowerInvariant();

        foreach (var excluded in NeverTheGame)
        {
            if (lower.Contains(excluded, StringComparison.Ordinal)) return double.MinValue;
        }

        var score = 0.0;

        // Depth: the game binary is usually at or near the top.
        var relative = Path.GetRelativePath(root, file);
        var depth = relative.Count(c => c == Path.DirectorySeparatorChar);
        score -= depth * 2.0;

        // A name matching the folder is a strong signal.
        var stemKey = TextTools.Compact(stem);
        if (stemKey.Length > 0 && folderKey.Length > 0)
        {
            if (stemKey == folderKey) score += 5.0;
            else if (folderKey.Contains(stemKey, StringComparison.Ordinal) ||
                     stemKey.Contains(folderKey, StringComparison.Ordinal)) score += 3.0;
        }

        // Engine binaries are real launch targets even though their names are useless.
        if (_data.GenericExecutables.Contains(stem)) score += 1.5;

        try
        {
            var size = new FileInfo(file).Length;
            score += Math.Min(3.0, size / (20.0 * 1024 * 1024)); // up to +3 at 60 MB
        }
        catch (IOException)
        {
            // Size is a nicety.
        }

        return score;
    }

    // --- candidate sources ------------------------------------------------------------

    private void AddFolderCandidates(
        string directory, List<(string, CandidateOrigin, double)> raw, bool boostFolder)
    {
        var current = new DirectoryInfo(directory);
        if (current.Parent is null && !Directory.Exists(directory)) return;

        var folderName = current.Name;

        // A build directory is not a title; step up until we find something meaningful.
        var walkedUp = 0;
        while (_data.BuildDirectories.Contains(folderName) && current.Parent is not null && walkedUp < 3)
        {
            current = current.Parent;
            folderName = current.Name;
            walkedUp++;
        }

        if (string.IsNullOrWhiteSpace(folderName)) return;

        // A drive root ("C:\") is not a game name.
        if (current.Parent is null) return;

        if (!IsLibraryFolder(folderName))
        {
            raw.Add((folderName, walkedUp > 0 ? CandidateOrigin.ParentFolderName : CandidateOrigin.FolderName,
                boostFolder ? 1.00 : 0.85));
        }

        // Repacks often nest as "<Title>\<Title> <version>\", so the grandparent is worth
        // a look — but only when it is a real title. Without this check every game under
        // "D:\PC GAMES" would contribute "PC GAMES" as a candidate.
        var grandparent = current.Parent;
        if (grandparent?.Parent is not null &&
            !_data.BuildDirectories.Contains(grandparent.Name) &&
            !IsLibraryFolder(grandparent.Name))
        {
            raw.Add((grandparent.Name, CandidateOrigin.ParentFolderName, 0.45));
        }
    }

    private static void AddMetadataCandidates(string? executable, List<(string, CandidateOrigin, double)> raw)
    {
        if (executable is null || !File.Exists(executable)) return;

        try
        {
            var info = FileVersionInfo.GetVersionInfo(executable);

            if (IsUsefulMetadata(info.ProductName)) raw.Add((info.ProductName!, CandidateOrigin.ExecutableMetadata, 0.70));
            if (IsUsefulMetadata(info.FileDescription)) raw.Add((info.FileDescription!, CandidateOrigin.ExecutableMetadata, 0.55));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Version resources are optional and frequently absent.
        }
    }

    /// <summary>
    /// Rejects the placeholder values engines ship with. Unity writes its own product name
    /// into every unconfigured build, and those would otherwise outrank the folder.
    /// </summary>
    private static bool IsUsefulMetadata(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length < 3) return false;

        var lower = value.Trim().ToLowerInvariant();

        // Descriptions, not titles. A real game's ProductName is a name, not a sentence —
        // "scenario player program for bolero01" is a description of the engine.
        if (lower.Contains(" for ", StringComparison.Ordinal) ||
            lower.Contains("program", StringComparison.Ordinal) ||
            lower.Contains("engine", StringComparison.Ordinal) ||
            lower.Contains("runtime", StringComparison.Ordinal) ||
            lower.Contains("player", StringComparison.Ordinal) ||
            lower.Split(' ').Length > 8)
        {
            return false;
        }

        return lower is not ("unity" or "unity player" or "unity technologies" or "defaultcompany"
            or "product name" or "game" or "myapp" or "application" or "unreal engine"
            or "gamemaker studio" or "renpy" or "ren'py" or "adobe air");
    }

    private static void AddSiblingCandidates(string directory, List<(string, CandidateOrigin, double)> raw)
    {
        try
        {
            // GOG and repack installers commonly drop "<Game Name>.url" beside the binary,
            // which is often the cleanest title anywhere on disk.
            foreach (var file in Directory.EnumerateFiles(directory, "*.url").Take(5))
            {
                var stem = Path.GetFileNameWithoutExtension(file);
                if (stem.Length >= 3 && !stem.StartsWith("support", StringComparison.OrdinalIgnoreCase))
                {
                    raw.Add((stem, CandidateOrigin.SiblingFile, 0.60));
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Store product codes are stronger identifiers than a noisy executable name. DLsite
    /// downloads commonly retain an RJ/VJ/BJ code in the containing folder, archive, readme,
    /// or installer name, so inspect only the immediate directory (never crawl a library).
    /// </summary>
    private static void AddStoreCodeCandidates(string directory, List<(string, CandidateOrigin, double)> raw)
    {
        try
        {
            var values = new List<string> { Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar)) };
            values.AddRange(Directory.EnumerateFileSystemEntries(directory).Take(100)
                .Select(Path.GetFileName).OfType<string>());

            foreach (var value in values)
            foreach (System.Text.RegularExpressions.Match match in DlsiteCodePattern.Matches(value))
                raw.Add((match.Value.ToUpperInvariant(), CandidateOrigin.SiblingFile, 1.10));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    [System.Text.RegularExpressions.GeneratedRegex(
        @"(?<![A-Z0-9])(?:RJ|VJ|BJ)\d{6,10}(?![A-Z0-9])",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase |
        System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex DlsiteCodePattern { get; }

    // --- assembly ---------------------------------------------------------------------

    private IReadOnlyList<LocalNameCandidate> BuildCandidates(
        List<(string Text, CandidateOrigin Origin, double Weight)> raw, out NameAnalysis primary)
    {
        var results = new List<LocalNameCandidate>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        NameAnalysis? best = null;
        var bestWeight = double.MinValue;

        foreach (var (text, origin, weight) in raw.OrderByDescending(r => r.Weight))
        {
            var analysis = _normalizer.Normalize(text);

            if (weight > bestWeight)
            {
                bestWeight = weight;
                best = analysis;
            }

            // Each source contributes all of its normalization variants, scaled by how
            // much the source itself is trusted.
            foreach (var candidate in analysis.Candidates)
            {
                var key = TextTools.Compact(candidate.Value);
                if (key.Length == 0 || !seen.Add(key)) continue;

                results.Add(new LocalNameCandidate(candidate.Value, origin, weight * candidate.Weight, text));
            }
        }

        primary = best ?? _normalizer.Normalize(string.Empty);

        return results.OrderByDescending(c => c.Weight).ToList();
    }
}
