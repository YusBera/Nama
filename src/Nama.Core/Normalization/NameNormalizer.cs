using System.Text.RegularExpressions;
using Nama.Core.Models;

namespace Nama.Core.Normalization;

/// <summary>
/// Turns a raw executable or folder name into searchable titles.
/// <para>
/// The pipeline is deliberately <em>additive</em>: every intermediate form is kept as a
/// lower-weighted candidate rather than discarded, so an over-aggressive rule degrades
/// the ranking instead of destroying the only usable search term. Normalization is for
/// identification and display only — it never renames anything on disk.
/// </para>
/// </summary>
public sealed partial class NameNormalizer
{
    /// <summary>
    /// Only these extensions are stripped. A conservative list matters: a blanket
    /// "remove everything after the last dot" would turn "Steins.Gate" into "Steins".
    /// </summary>
    private static readonly HashSet<string> StrippableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "exe", "lnk", "url", "bat", "cmd", "com", "msi", "app", "swf", "jar",
        "zip", "rar", "7z", "iso", "tar", "gz", "bin",
    };

    private readonly NormalizationData _data;

    public NameNormalizer(NormalizationData? data = null) => _data = data ?? NormalizationData.Default;

    [GeneratedRegex(@"[\s._+~\-–—/\\]+", RegexOptions.CultureInvariant)]
    private static partial Regex SeparatorPattern { get; }

    [GeneratedRegex(@"\[[^\]]*\]|\([^)]*\)|\{[^}]*\}", RegexOptions.CultureInvariant)]
    private static partial Regex BracketGroupPattern { get; }

    /// <summary>
    /// Version markers as they actually appear, before tokenization.
    /// <para>
    /// This has to run on the un-split string: a version routinely spans what would
    /// otherwise be separators. Splitting first turns "v2.1" into "v2" and "1", and
    /// leaves the "6" of "Update 6" stranded as a plausible-looking sequel number.
    /// </para>
    /// </summary>
    [GeneratedRegex(
        """
        (?<![A-Za-z0-9])
        (?:
            v\d+(?:[._]\d+){0,4}[a-z]?
          | \d+(?:\.\d+){1,4}[a-z]?
          | (?:update|build|rev|revision|patch|hotfix|version|ver|beta|alpha)[ ._\-]*\d+(?:\.\d+)*
          | [rb]\d{4,}
        )
        (?![A-Za-z0-9])
        """,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.IgnorePatternWhitespace)]
    private static partial Regex VersionPhrasePattern { get; }

    /// <summary>Runs the full pipeline over one raw name.</summary>
    public NameAnalysis Normalize(string? raw)
    {
        raw ??= string.Empty;
        var removed = new List<string>();
        var input = raw.Trim();

        // 1. Extension.
        var rawStem = StripExtension(input);

        // 2. Bracketed groups: dropped when they contain noise, inlined otherwise.
        var debracketed = StripBrackets(rawStem, removed, out var bracketVariant);

        // 3-5. Version phrases, then separators, tokens, noise removal.
        var deversioned = RemoveVersionPhrases(debracketed, removed);
        var tokens = Tokenize(deversioned);
        var kept = FilterTokens(tokens, removed, out var withoutEdition);
        var cleaned = TextTools.Tidy(string.Join(' ', kept));

        // 6. A single glued token carries word boundaries in its casing — use them.
        string? camelSplit = null;
        if (kept.Count == 1 && TextTools.HasCamelCaseBoundary(kept[0]))
        {
            var splitTokens = FilterTokens(Tokenize(TextTools.SplitCamelCase(kept[0])), removed, out _);
            camelSplit = TextTools.Tidy(string.Join(' ', splitTokens));
            if (camelSplit.Length > 0) cleaned = camelSplit;
        }

        // Never let the pipeline trim its way down to nothing.
        if (cleaned.Length == 0) cleaned = TextTools.Tidy(rawStem);
        if (cleaned.Length == 0) cleaned = input;

        // 7. Known abbreviation or punctuation-stripped title (Steins;Gate, SubaHibi).
        var expansion = LookupAbbreviation(cleaned) ?? LookupAbbreviation(rawStem);

        var normalized = expansion ?? cleaned;
        var hasCjk = TextTools.ContainsCjk(input);

        return new NameAnalysis
        {
            Raw = raw,
            Normalized = normalized,
            DisplayName = expansion ?? TextTools.ToDisplayCase(cleaned),
            HasCjk = hasCjk,
            RemovedTokens = removed,
            Candidates = BuildCandidates(
                expansion, cleaned, camelSplit, withoutEdition, bracketVariant, rawStem, input),
        };
    }

    /// <summary>Convenience overload for the common case of normalizing a file path's name.</summary>
    public NameAnalysis NormalizeFileName(string path) => Normalize(Path.GetFileName(path));

    // --- pipeline stages -------------------------------------------------------------

    private static string StripExtension(string value)
    {
        var dot = value.LastIndexOf('.');
        if (dot <= 0 || dot == value.Length - 1) return value;

        var extension = value[(dot + 1)..];
        return StrippableExtensions.Contains(extension) ? value[..dot] : value;
    }

    /// <summary>
    /// Drops bracket groups whose contents are noise ("[FitGirl Repack]") and inlines the
    /// rest ("(Director's Cut)"). The all-inlined form is returned separately as a
    /// fallback candidate in case the noise judgement was wrong.
    /// </summary>
    private string StripBrackets(string value, List<string> removed, out string? inlinedVariant)
    {
        inlinedVariant = null;
        if (!value.Contains('[') && !value.Contains('(') && !value.Contains('{')) return value;

        var anyDropped = false;

        var primary = BracketGroupPattern.Replace(value, match =>
        {
            var inner = match.Value[1..^1];
            var innerTokens = Tokenize(inner);
            var isNoise = VersionPhrasePattern.IsMatch(inner) ||
                          innerTokens.Any(t => _data.IsDroppableToken(t) || _data.IsVersionToken(t));

            if (!isNoise) return $" {inner} ";

            anyDropped = true;
            removed.Add(match.Value);
            return " ";
        });

        if (anyDropped)
        {
            inlinedVariant = TextTools.Tidy(BracketGroupPattern.Replace(value, m => $" {m.Value[1..^1]} "));
        }

        return primary;
    }

    /// <summary>
    /// Strips version markers while the string is still intact, so multi-token forms
    /// like "Update 6" and dotted forms like "v2.1" are removed whole rather than
    /// leaving a stray number that reads as a sequel.
    /// </summary>
    private static string RemoveVersionPhrases(string value, List<string> removed)
    {
        return VersionPhrasePattern.Replace(value, match =>
        {
            removed.Add(match.Value);
            return " ";
        });
    }

    /// <summary>Splits on every separator, including dots.</summary>
    private static List<string> Tokenize(string value)
    {
        var tokens = new List<string>();

        foreach (var token in SeparatorPattern.Split(value))
        {
            if (token.Length > 0) tokens.Add(token);
        }

        return tokens;
    }

    /// <summary>
    /// Removes version, scene-group, platform and language tokens.
    /// <para>
    /// Two guards keep this from eating real titles: the first token is never treated as
    /// noise (so "PC Building Simulator" and "Final Fantasy" survive), and the list is
    /// never emptied completely.
    /// </para>
    /// </summary>
    private List<string> FilterTokens(List<string> tokens, List<string> removed, out List<string> withoutEdition)
    {
        var kept = new List<string>(tokens.Count);
        var noEdition = new List<string>(tokens.Count);

        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];

            // Version tokens are unambiguous enough to strip anywhere.
            if (_data.IsVersionToken(token))
            {
                removed.Add(token);
                continue;
            }

            if (i > 0 && _data.IsDroppableToken(token))
            {
                removed.Add(token);
                continue;
            }

            kept.Add(token);

            // Edition markers stay in the primary name — "The Last of Us Remastered" is a
            // different game from "The Last of Us" — but seed a variant without them.
            if (i == 0 || !_data.Edition.Contains(token)) noEdition.Add(token);
        }

        if (kept.Count == 0 && tokens.Count > 0)
        {
            kept.Add(tokens[0]);
            noEdition.Add(tokens[0]);
        }

        withoutEdition = noEdition;
        return kept;
    }

    private string? LookupAbbreviation(string value)
    {
        var key = TextTools.Compact(value);
        if (key.Length == 0) return null;

        return _data.Abbreviations.TryGetValue(key, out var expansion) ? expansion : null;
    }

    private static List<NameCandidate> BuildCandidates(
        string? expansion,
        string cleaned,
        string? camelSplit,
        List<string> withoutEdition,
        string? bracketVariant,
        string rawStem,
        string original)
    {
        var candidates = new List<NameCandidate>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        Add(expansion, NameCandidateKind.AbbreviationExpansion, 1.00);
        Add(cleaned, NameCandidateKind.Normalized, 0.95);

        // Japanese text is preserved verbatim — romanizing it would lose the strongest
        // signal VNDB has.
        foreach (var run in TextTools.ExtractCjkRuns(original))
        {
            Add(run, NameCandidateKind.Cjk, 0.90);
        }

        Add(camelSplit, NameCandidateKind.CamelSplit, 0.70);
        Add(TextTools.Tidy(string.Join(' ', withoutEdition)), NameCandidateKind.Normalized, 0.65);
        Add(bracketVariant, NameCandidateKind.BracketVariant, 0.60);
        Add(TextTools.Tidy(rawStem), NameCandidateKind.RawStem, 0.50);

        return candidates;

        void Add(string? value, NameCandidateKind kind, double weight)
        {
            if (string.IsNullOrWhiteSpace(value)) return;

            // De-duplicate ignoring case and punctuation so "Steins Gate" and "steins-gate"
            // do not both consume a provider query.
            var key = TextTools.Compact(value);
            if (key.Length == 0 || !seen.Add(key)) return;

            candidates.Add(new NameCandidate(value, kind, weight));
        }
    }
}
