using System.Globalization;
using System.Text;

namespace Nama.Core.Normalization;

/// <summary>String helpers shared by normalization and fuzzy matching.</summary>
public static class TextTools
{
    private static readonly HashSet<string> LowercaseWords = new(StringComparer.OrdinalIgnoreCase)
    {
        // English function words.
        "a", "an", "and", "as", "at", "but", "by", "for", "from", "in", "into",
        "nor", "of", "on", "or", "the", "to", "with", "vs",
        // Romanized Japanese particles, which are conventionally lowercase mid-title
        // ("Umineko no Naku Koro ni", "Maji de Watashi ni Koi Shinasai").
        "no", "ni", "wo", "wa", "ga", "de", "mo", "ka", "na", "ne", "yo", "he",
    };

    private static readonly HashSet<string> RomanNumerals = new(StringComparer.OrdinalIgnoreCase)
    {
        "ii", "iii", "iv", "vi", "vii", "viii", "ix", "xi", "xii", "xiii", "xiv", "xv",
    };

    /// <summary>
    /// Lowercase, alphanumerics only. Used as the abbreviation lookup key and for
    /// punctuation-insensitive comparison, so "Steins;Gate" and "Steins_Gate" collapse
    /// to the same key.
    /// </summary>
    public static string Compact(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (char.IsLetterOrDigit(c)) builder.Append(char.ToLowerInvariant(c));
        }

        return builder.ToString();
    }

    /// <summary>True when the string contains Hiragana, Katakana, or CJK ideographs.</summary>
    public static bool ContainsCjk(string value)
    {
        foreach (var c in value)
        {
            if (IsCjk(c)) return true;
        }

        return false;
    }

    public static bool IsCjk(char c) => c is
        (>= '぀' and <= 'ゟ') or   // Hiragana
        (>= '゠' and <= 'ヿ') or   // Katakana
        (>= '一' and <= '鿿') or   // CJK Unified Ideographs
        (>= '㐀' and <= '䶿') or   // CJK Extension A
        (>= 'ｦ' and <= 'ﾝ');     // Halfwidth Katakana

    /// <summary>
    /// Contiguous runs of CJK text, each at least two characters. Lets a mixed name like
    /// "ホワイトアルバム2 White Album 2" yield its Japanese title as its own search term.
    /// </summary>
    public static IReadOnlyList<string> ExtractCjkRuns(string value)
    {
        var runs = new List<string>();
        var builder = new StringBuilder();

        foreach (var c in value)
        {
            // Digits and long-vowel marks are kept inside a run so "ホワイトアルバム2" stays whole.
            if (IsCjk(c) || (builder.Length > 0 && (char.IsDigit(c) || c is 'ー' or '・' or '＝')))
            {
                builder.Append(c);
            }
            else if (builder.Length > 0)
            {
                Flush(runs, builder);
            }
        }

        Flush(runs, builder);
        return runs;

        static void Flush(List<string> target, StringBuilder builder)
        {
            var run = builder.ToString().Trim();
            if (run.Length >= 2 && ContainsCjk(run)) target.Add(run);
            builder.Clear();
        }
    }

    /// <summary>
    /// Inserts spaces at CamelCase and letter/digit boundaries: "SubaHibiEN2" becomes
    /// "Suba Hibi EN 2". Left alone when the input is a single case (ELDENRING, eldenring),
    /// since there is no boundary information to use.
    /// </summary>
    public static string SplitCamelCase(string value)
    {
        if (value.Length < 2) return value;

        var builder = new StringBuilder(value.Length + 8);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (i > 0 && NeedsBreakBefore(value, i)) builder.Append(' ');
            builder.Append(c);
        }

        return builder.ToString();

        static bool NeedsBreakBefore(string s, int i)
        {
            var prev = s[i - 1];
            var cur = s[i];

            // lower|digit -> upper   e.g. "SubaHibi" -> "Suba Hibi"
            if (char.IsUpper(cur) && (char.IsLower(prev) || char.IsDigit(prev))) return true;

            // UPPER -> Upper+lower   e.g. "ENGame" -> "EN Game"
            if (char.IsUpper(prev) && char.IsUpper(cur) && i + 1 < s.Length && char.IsLower(s[i + 1])) return true;

            // letter -> digit and digit -> letter   e.g. "Album2" -> "Album 2"
            if (char.IsLetter(prev) && char.IsDigit(cur)) return true;
            if (char.IsDigit(prev) && char.IsLetter(cur)) return true;

            return false;
        }
    }

    /// <summary>True when the string has an internal case boundary worth splitting on.</summary>
    public static bool HasCamelCaseBoundary(string value)
    {
        for (var i = 1; i < value.Length; i++)
        {
            if (char.IsUpper(value[i]) && char.IsLower(value[i - 1])) return true;
        }

        return false;
    }

    /// <summary>
    /// Title-cases for display only — never for matching. Leaves CJK untouched, keeps
    /// existing intercapped words (NieR, McDonald) and roman numerals intact, and
    /// lowercases function words except in first or last position.
    /// <para>
    /// A multi-word name that already mixes upper and lower case is returned unchanged.
    /// That casing is information — someone typed it — and re-casing it does real damage:
    /// a folder named "Love for these Clothes of Desire!" would come back as "...for These
    /// Clothes...". Only names carrying no case information (ALL CAPS, all lowercase, or a
    /// single word) are re-cased.
    /// </para>
    /// </summary>
    public static string ToDisplayCase(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || ContainsCjk(value)) return value;

        var words = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (words.Length > 1 && HasDeliberateCasing(value)) return value;
        for (var i = 0; i < words.Length; i++)
        {
            var word = words[i];
            var core = word.Trim('(', ')', '[', ']', ':', ',', '.', '!', '?', '"', '\'');

            if (RomanNumerals.Contains(core))
            {
                words[i] = word.ToUpperInvariant();
                continue;
            }

            // Preserve deliberate casing the user or a provider already applied.
            if (HasCamelCaseBoundary(word) || (word.Length > 1 && word.ToUpperInvariant() == word && word.Any(char.IsLetter)))
            {
                continue;
            }

            if (i != 0 && i != words.Length - 1 && LowercaseWords.Contains(core))
            {
                words[i] = word.ToLowerInvariant();
                continue;
            }

            words[i] = Capitalize(word);
        }

        return string.Join(' ', words);

        static string Capitalize(string word)
        {
            var lower = word.ToLower(CultureInfo.InvariantCulture);
            foreach (var (index, c) in lower.Select((c, i) => (i, c)))
            {
                if (char.IsLetter(c))
                {
                    return string.Concat(lower[..index], char.ToUpperInvariant(c), lower[(index + 1)..]);
                }
            }

            return lower;
        }
    }

    /// <summary>
    /// True when the text contains both upper and lower case letters, i.e. whoever wrote
    /// it made casing choices worth preserving.
    /// </summary>
    public static bool HasDeliberateCasing(string value)
    {
        var hasUpper = false;
        var hasLower = false;

        foreach (var c in value)
        {
            if (char.IsUpper(c)) hasUpper = true;
            else if (char.IsLower(c)) hasLower = true;

            if (hasUpper && hasLower) return true;
        }

        return false;
    }

    /// <summary>Collapses runs of whitespace and trims stray leading/trailing punctuation.</summary>
    public static string Tidy(string value)
    {
        var collapsed = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Trim(' ', '-', '_', '.', ',', ':', ';', '~', '+', '&');
    }
}
