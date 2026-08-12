using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Nama.Core.Normalization;

/// <summary>
/// The rule sets driving <see cref="NameNormalizer"/>.
/// <para>
/// Defaults ship embedded in the assembly so the library never depends on files sitting
/// next to the binary. A user file of the same name under <c>%APPDATA%\Nama\data\</c> is
/// merged on top — lists are unioned, dictionary entries overwrite — which is what keeps
/// the rules editable without a rebuild.
/// </para>
/// </summary>
public sealed class NormalizationData
{
    private static readonly Lazy<NormalizationData> LazyDefault = new(() => Load());

    /// <summary>Shared instance using embedded defaults plus any user override directory.</summary>
    public static NormalizationData Default => LazyDefault.Value;

    public required IReadOnlySet<string> ReleaseGroups { get; init; }

    public required IReadOnlySet<string> Noise { get; init; }

    public required IReadOnlySet<string> Language { get; init; }

    public required IReadOnlySet<string> Edition { get; init; }

    public required IReadOnlyList<Regex> VersionPatterns { get; init; }

    /// <summary>Compacted key (lowercase, alphanumeric only) to full title.</summary>
    public required IReadOnlyDictionary<string, string> Abbreviations { get; init; }

    /// <summary>Executable stems carrying no title information, including engine runtimes.</summary>
    public required IReadOnlySet<string> GenericExecutables { get; init; }

    /// <summary>Directory names that are build output rather than a title.</summary>
    public required IReadOnlySet<string> BuildDirectories { get; init; }

    /// <summary>
    /// Folders games are kept in. Never a title, so they must not become search
    /// candidates — otherwise every game under "D:\PC GAMES" contributes the same
    /// meaningless guess.
    /// </summary>
    public required IReadOnlySet<string> LibraryDirectories { get; init; }

    /// <summary>True when the token is noise of any kind that should be dropped outright.</summary>
    public bool IsDroppableToken(string token) =>
        Noise.Contains(token) || Language.Contains(token) || ReleaseGroups.Contains(token);

    public bool IsVersionToken(string token)
    {
        foreach (var pattern in VersionPatterns)
        {
            if (pattern.IsMatch(token)) return true;
        }

        return false;
    }

    /// <summary>
    /// Loads the rule sets. <paramref name="overrideDirectory"/> defaults to
    /// <c>%APPDATA%\Nama\data</c>; pass a path in tests to isolate, or a non-existent
    /// path to use embedded defaults only.
    /// </summary>
    public static NormalizationData Load(string? overrideDirectory = null)
    {
        overrideDirectory ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Nama",
            "data");

        var groups = ReadStringSet("release-groups.json", "groups", overrideDirectory);

        var noiseDoc = ReadDocument("noise-tokens.json", overrideDirectory);
        var noise = ReadSet(noiseDoc, "noise");
        var language = ReadSet(noiseDoc, "language");
        var edition = ReadSet(noiseDoc, "edition");
        var versionPatterns = ReadList(noiseDoc, "versionPatterns")
            .Select(p => new Regex($"^(?:{p})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            .ToList();

        var execDoc = ReadDocument("generic-executables.json", overrideDirectory);
        var generic = ReadSet(execDoc, "genericNames");
        generic.UnionWith(ReadSet(execDoc, "engineNames"));
        var buildDirs = ReadSet(execDoc, "buildDirectories");
        var libraryDirs = ReadSet(execDoc, "libraryDirectories");

        return new NormalizationData
        {
            ReleaseGroups = groups,
            Noise = noise,
            Language = language,
            Edition = edition,
            VersionPatterns = versionPatterns,
            Abbreviations = ReadAbbreviations(overrideDirectory),
            GenericExecutables = generic,
            BuildDirectories = buildDirs,
            LibraryDirectories = libraryDirs,
        };
    }

    // --- loading helpers -------------------------------------------------------------

    private static List<JsonDocument> ReadDocument(string fileName, string overrideDirectory)
    {
        var docs = new List<JsonDocument> { JsonDocument.Parse(ReadEmbedded(fileName)) };

        var userPath = Path.Combine(overrideDirectory, fileName);
        if (File.Exists(userPath))
        {
            try
            {
                docs.Add(JsonDocument.Parse(File.ReadAllText(userPath)));
            }
            catch (JsonException)
            {
                // A malformed user override must never break identification — fall back
                // to embedded defaults silently rather than taking the app down.
            }
        }

        return docs;
    }

    private static string ReadEmbedded(string fileName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resource = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(fileName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"Embedded normalization data '{fileName}' is missing from {assembly.GetName().Name}.");

        using var stream = assembly.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static HashSet<string> ReadSet(List<JsonDocument> docs, string property)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var doc in docs)
        {
            if (!doc.RootElement.TryGetProperty(property, out var array)) continue;
            foreach (var item in array.EnumerateArray())
            {
                if (item.GetString() is { Length: > 0 } value) set.Add(value);
            }
        }

        return set;
    }

    private static List<string> ReadList(List<JsonDocument> docs, string property)
    {
        var list = new List<string>();
        foreach (var doc in docs)
        {
            if (!doc.RootElement.TryGetProperty(property, out var array)) continue;
            foreach (var item in array.EnumerateArray())
            {
                if (item.GetString() is { Length: > 0 } value) list.Add(value);
            }
        }

        return list;
    }

    private static HashSet<string> ReadStringSet(string fileName, string property, string overrideDirectory) =>
        ReadSet(ReadDocument(fileName, overrideDirectory), property);

    private static Dictionary<string, string> ReadAbbreviations(string overrideDirectory)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var doc in ReadDocument("abbreviations.json", overrideDirectory))
        {
            if (!doc.RootElement.TryGetProperty("abbreviations", out var obj)) continue;
            foreach (var entry in obj.EnumerateObject())
            {
                if (entry.Value.GetString() is { Length: > 0 } value)
                {
                    map[TextTools.Compact(entry.Name)] = value;
                }
            }
        }

        return map;
    }
}
