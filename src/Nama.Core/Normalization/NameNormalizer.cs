using System.Globalization;
using System.Text;

namespace Nama.Core.Normalization;

/// <summary>
/// Turns a raw file or folder name into a clean title plus a ranked list of search
/// candidates. This is a product feature, not a helper: identification quality is
/// bounded by how well this stage works.
///
/// The pipeline never destroys the raw input — if cleaning removes everything, the
/// result falls back to a lightly-tidied version of what came in.
/// </summary>
public sealed class NameNormalizer
{
    /// <summary>Titles that are legitimately just a noise word, so cleaning must not empty them.</summary>
    private static readonly HashSet<string> ProtectedTitles = new(StringComparer.OrdinalIgnoreCase)
    {
        "portal", "control", "limbo", "inside", "journey", "flow", "gone home", "the witness",
        "hades", "celeste", "cuphead", "braid", "bastion", "transistor", "firewatch", "prey",
        "doom", "rage", "dishonored", "fallout", "half-life", "gris", "abzu", "rime", "spore",
        "free", "steam", "demo", "beta", "patch", "update", "crack", "install", "game",
    };

    /// <summary>
    /// Normalizes <paramref name="raw"/> into a title and a ranked candidate list.
    /// </summary>
    public NormalizationResult Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return NormalizationResult.Empty;

        var input = raw.Trim();
        var containsCjk = NoisePatterns.Cjk.IsMatch(input);
        var removed = new List<string>();

        // A title that is entirely CJK is already canonical — cleaning it with
        // Latin-oriented rules would only damage it.
        if (containsCjk && IsPredominantlyCjk(input))
        {
            var cjkTitle = CollapseWhitespace(NoisePatterns.Bracketed.Replace(input, " ")).Trim();
            if (cjkTitle.Length == 0) cjkTitle = input;
            return new NormalizationResult
            {
                Raw = input,
                Normalized = cjkTitle,
                Candidates = [cjkTitle],
                MatchKey = BuildMatchKey(cjkTitle),
                ContainsCjk = true,
            };
        }

        var working = input;

        // 1. Strip bracketed segments wholesale — they are almost always tags.
        working = CaptureAndRemove(working, NoisePatterns.Bracketed, removed);

        // 2. Collapse every separator except the dot. Underscores and hyphens are word
        //    characters to the regex engine, so "Hades_v1.38290" hides the boundary that
        //    the version pattern needs; dots stay because versions are built from them.
        working = NoisePatterns.WordSeparators.Replace(working, " ");

        // 3. Remove version strings and update counters while the dots survive.
        working = CaptureAndRemove(working, NoisePatterns.UpdateCounter, removed);
        working = CaptureAndRemove(working, NoisePatterns.Version, removed);

        // 4. Now the dots can go too.
        working = NoisePatterns.Separators.Replace(working, " ");
        working = NoisePatterns.StrippablePunctuation.Replace(working, " ");
        working = CollapseWhitespace(working);

        // 5. Drop junk while the tokens are still whole. This has to happen before
        //    camelCase splitting, which would turn "Win64" into "Win 64" and leave two
        //    fragments that no longer match anything in the noise lists.
        //    Leading tokens are kept: a title rarely starts with junk, but often ends with it.
        var tokens = working.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        tokens = StripJunkTokens(tokens, removed);

        // 6. Split camelCase runs, then strip again to catch junk that the split exposed
        //    ("SubaHibiEN" only reveals its trailing language tag once separated).
        working = NoisePatterns.CamelBoundary.Replace(string.Join(' ', tokens), " ");
        tokens = CollapseWhitespace(working).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        tokens = StripJunkTokens(tokens, removed);

        var cleaned = CollapseWhitespace(string.Join(' ', tokens));
        var isFallback = false;

        if (cleaned.Length == 0)
        {
            // Everything was classified as noise. Fall back to a tidied raw name so the
            // user still gets something searchable rather than an empty box.
            cleaned = CollapseWhitespace(
                NoisePatterns.CamelBoundary.Replace(
                    NoisePatterns.Separators.Replace(input, " "), " "));
            isFallback = true;
        }

        var titled = TitleCase(cleaned);

        // A whole-title abbreviation is the canonical name, not merely a candidate:
        // "SubaHibiEN" identifies the game far better as "Subarashiki Hibi".
        var compact = new string(titled.Where(char.IsLetterOrDigit).ToArray());
        var normalized = NoisePatterns.Abbreviations.TryGetValue(compact, out var expanded)
            ? expanded
            : titled;

        // The same title with trailing "Game"/"HD"/"Complete" removed. Kept only as a
        // search candidate: if the padded form finds nothing, the trimmed one still can.
        var trimmed = StripTrailingTitlePlausibleWords(titled);

        var candidates = BuildCandidates(normalized, titled, trimmed, input, containsCjk);

        return new NormalizationResult
        {
            Raw = input,
            Normalized = normalized,
            Candidates = candidates,
            MatchKey = BuildMatchKey(normalized),
            RemovedTokens = removed,
            ContainsCjk = containsCjk,
            IsFallback = isFallback,
        };
    }

    /// <summary>
    /// Builds the ranked search list: the clean title first, then expansions and
    /// progressively shorter fallbacks. De-duplicated, order preserved.
    /// </summary>
    /// <summary>
    /// Peels trailing words that are noise in a packaging sense but plausible in a title,
    /// leaving at least one token. Used only for the alternate search candidate.
    /// </summary>
    private static string StripTrailingTitlePlausibleWords(string value)
    {
        var tokens = value.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();

        while (tokens.Count > 1 && NoisePatterns.TitlePlausibleWords.Contains(tokens[^1]))
            tokens.RemoveAt(tokens.Count - 1);

        return string.Join(' ', tokens);
    }

    private static IReadOnlyList<string> BuildCandidates(
        string normalized,
        string cleaned,
        string trimmed,
        string raw,
        bool containsCjk)
    {
        var candidates = new List<string>();

        void Add(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            var trimmed = CollapseWhitespace(value).Trim();
            if (trimmed.Length < 2) return;
            if (!candidates.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
                candidates.Add(trimmed);
        }

        Add(normalized);

        // The literal cleaned name, kept as a fallback when an abbreviation was expanded
        // and the expansion turns out to be wrong.
        Add(cleaned);

        // The same name without trailing "Game"/"HD"/"Complete", for databases that index
        // the bare title.
        Add(trimmed);

        // Per-token abbreviation expansion, e.g. "WA2 Closing Chapter".
        var tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length > 1)
        {
            var expanded = tokens
                .Select(t => NoisePatterns.Abbreviations.TryGetValue(t, out var e) ? e : t)
                .ToArray();
            var joined = string.Join(' ', expanded);
            if (!joined.Equals(normalized, StringComparison.OrdinalIgnoreCase))
                Add(joined);
        }

        // Subtitle handling: "Game: Subtitle" also searched as just "Game", since providers
        // frequently index the base title only.
        var separatorIndex = normalized.IndexOfAny([':', '-', '–', '—']);
        if (separatorIndex > 2)
            Add(normalized[..separatorIndex]);

        // Trailing-year removal, e.g. "Doom 2016" -> "Doom".
        var withoutYear = NoisePatterns.TrailingYear.Replace(normalized, string.Empty);
        if (withoutYear.Length >= 2)
            Add(withoutYear);

        // Progressive truncation gives short fallbacks a chance when the tail is unknown noise.
        if (tokens.Length > 2)
            Add(string.Join(' ', tokens.Take(tokens.Length - 1)));
        if (tokens.Length > 3)
            Add(string.Join(' ', tokens.Take(2)));

        // Keep the raw form around when it contained CJK, so the original-language title
        // still reaches providers that index it.
        if (containsCjk)
            Add(raw);

        return candidates;
    }

    /// <summary>
    /// Removes junk tokens from the end inward, then any interior release-group tokens.
    /// Stops early if removal would empty a protected title.
    /// </summary>
    private static List<string> StripJunkTokens(List<string> tokens, List<string> removed)
    {
        bool IsJunk(string token)
        {
            var key = token.Trim().Trim('\'', '"');
            if (key.Length == 0) return true;
            if (NoisePatterns.ReleaseGroups.Contains(key)) return true;
            if (NoisePatterns.EngineSuffixes.Contains(key)) return true;

            // Words that double as real title words survive here and are only removed
            // when building the alternate search candidate.
            if (NoisePatterns.TitlePlausibleWords.Contains(key)) return false;

            if (NoisePatterns.NoiseWords.Contains(key)) return true;
            // Bare numbers at the tail are usually build ids. Four digits are far more
            // likely to be part of the title ("Cyberpunk 2077", "Project 1943"), so only
            // longer runs count as junk.
            if (key.Length >= 5 && key.All(char.IsDigit)) return true;
            return false;
        }

        // Peel junk off the end.
        while (tokens.Count > 1 && IsJunk(tokens[^1]))
        {
            removed.Add(tokens[^1]);
            tokens.RemoveAt(tokens.Count - 1);
        }

        // Remove release groups anywhere, since those are never part of a title.
        for (var i = tokens.Count - 1; i >= 0; i--)
        {
            if (tokens.Count <= 1) break;
            if (NoisePatterns.ReleaseGroups.Contains(tokens[i]))
            {
                removed.Add(tokens[i]);
                tokens.RemoveAt(i);
            }
        }

        // A single surviving token that is itself noise is only dropped when it is not a
        // real game title ("Portal" and "Control" must survive).
        if (tokens.Count == 1 &&
            NoisePatterns.NoiseWords.Contains(tokens[0]) &&
            !ProtectedTitles.Contains(tokens[0]))
        {
            removed.Add(tokens[0]);
            tokens.Clear();
        }

        return tokens;
    }

    private static string CaptureAndRemove(string input, System.Text.RegularExpressions.Regex regex, List<string> removed)
    {
        var matches = regex.Matches(input);
        if (matches.Count == 0) return input;

        foreach (System.Text.RegularExpressions.Match m in matches)
        {
            var value = m.Value.Trim();
            if (value.Length > 0) removed.Add(value);
        }

        return regex.Replace(input, " ");
    }

    /// <summary>
    /// Title-cases while preserving tokens that are already meaningfully cased —
    /// acronyms (<c>XIV</c>), stylized names (<c>NieR</c>) and roman numerals.
    ///
    /// Casing is only treated as meaningful when the input is mixed case. Release names
    /// are routinely SHOUTED in full ("ELDEN RING"), and there the capitalization carries
    /// no information, so every token is title-cased instead.
    /// </summary>
    private static string TitleCase(string value)
    {
        var tokens = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return value;

        var allLetters = value.Where(char.IsLetter).ToArray();
        var isShouted = allLetters.Length > 0 && allLetters.All(char.IsUpper);

        var result = new List<string>(tokens.Length);
        foreach (var token in tokens)
        {
            var letters = token.Where(char.IsLetter).ToArray();

            // Short all-caps tokens are acronyms; mixed-case tokens are stylized names.
            var isAllUpper = letters.Length > 0 && letters.All(char.IsUpper);
            var hasInnerUpper = token.Length > 1 && token[1..].Any(char.IsUpper);

            if (!isShouted && isAllUpper && letters.Length <= 4)
                result.Add(token);
            else if (!isShouted && hasInnerUpper)
                result.Add(token);
            else
                result.Add(char.ToUpper(token[0], CultureInfo.InvariantCulture) + token[1..].ToLowerInvariant());
        }

        return string.Join(' ', result);
    }

    /// <summary>
    /// Reduces a title to a comparison key: lowercase, accent-folded, alphanumeric only.
    /// Two titles that differ solely in punctuation or spacing produce the same key.
    /// </summary>
    public static string BuildMatchKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var decomposed = value.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);

        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
                continue;
            if (char.IsLetterOrDigit(ch))
                sb.Append(char.ToLowerInvariant(ch));
        }

        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>
    /// Tokenized comparison key used for order-insensitive matching:
    /// lowercase words, accents folded, sorted, space separated.
    /// </summary>
    public static string[] BuildTokens(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];

        var cleaned = NoisePatterns.Separators.Replace(value, " ");
        cleaned = NoisePatterns.StrippablePunctuation.Replace(cleaned, " ");
        cleaned = NoisePatterns.CamelBoundary.Replace(cleaned, " ");

        return cleaned
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => BuildMatchKey(t))
            .Where(t => t.Length > 0)
            .ToArray();
    }

    private static bool IsPredominantlyCjk(string value)
    {
        var letters = value.Count(char.IsLetter);
        if (letters == 0) return false;
        var cjk = value.Count(c => NoisePatterns.Cjk.IsMatch(c.ToString()));
        return cjk * 2 >= letters;
    }

    private static string CollapseWhitespace(string value) =>
        NoisePatterns.ExcessWhitespace.Replace(value.Replace('\t', ' '), " ").Trim();
}
