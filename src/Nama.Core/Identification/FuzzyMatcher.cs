using Nama.Core.Normalization;

namespace Nama.Core.Identification;

/// <summary>
/// Similarity between two titles, 0..1.
/// <para>
/// Exact string comparison is useless here — the whole problem is that the local name and
/// the provider's name never match exactly. Two measures are combined because they fail in
/// opposite directions: Jaro-Winkler handles typos, missing spaces and truncation but is
/// confused by reordering, while token overlap handles reordering and extra words but
/// gives nothing for a run-together name like "eldenring". The larger of the two wins.
/// </para>
/// </summary>
public static class FuzzyMatcher
{
    /// <summary>Best similarity of two titles, 0..1.</summary>
    public static double Similarity(string a, string b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return 0.0;

        // Punctuation-insensitive equality: "Steins;Gate" and "Steins Gate" are the same
        // title, and filesystems cannot store the semicolon anyway.
        var compactA = TextTools.Compact(a);
        var compactB = TextTools.Compact(b);
        if (compactA.Length == 0 || compactB.Length == 0) return 0.0;
        if (compactA == compactB) return 1.0;

        var jaro = JaroWinkler(compactA, compactB);
        var tokens = TokenOverlap(a, b);

        var score = Math.Max(jaro, tokens);

        // One title fully containing the other is a strong signal that the shorter one is
        // the same game — "Elden Ring" inside "Elden Ring Shadow of the Erdtree". It is
        // only a partial match though, so it lifts the floor rather than reaching 1.0.
        if (compactA.Contains(compactB, StringComparison.Ordinal) ||
            compactB.Contains(compactA, StringComparison.Ordinal))
        {
            var ratio = (double)Math.Min(compactA.Length, compactB.Length) / Math.Max(compactA.Length, compactB.Length);
            score = Math.Max(score, 0.70 + (0.25 * ratio));
        }

        return Math.Clamp(score, 0.0, 1.0);
    }

    /// <summary>Best similarity against any of several titles — a game's aliases.</summary>
    public static double BestSimilarity(string value, IEnumerable<string> alternatives)
    {
        var best = 0.0;

        foreach (var alternative in alternatives)
        {
            var score = Similarity(value, alternative);
            if (score > best) best = score;
            if (best >= 1.0) break;
        }

        return best;
    }

    /// <summary>
    /// Dice coefficient over distinct word tokens. Order-insensitive, so it survives
    /// "Hibi, Subarashiki" and tolerates one side carrying extra words.
    /// </summary>
    public static double TokenOverlap(string a, string b)
    {
        var tokensA = Tokenize(a);
        var tokensB = Tokenize(b);

        if (tokensA.Count == 0 || tokensB.Count == 0) return 0.0;

        var shared = tokensA.Count(tokensB.Contains);

        return 2.0 * shared / (tokensA.Count + tokensB.Count);
    }

    private static HashSet<string> Tokenize(string value)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);

        foreach (var part in value.Split(
                     [' ', '\t', '-', '_', '.', ':', ';', ',', '!', '?', '/', '\\', '~', '(', ')', '[', ']'],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var token = TextTools.Compact(part);
            if (token.Length > 0) tokens.Add(token);
        }

        return tokens;
    }

    /// <summary>
    /// Jaro-Winkler similarity. The Winkler prefix bonus matters here: game titles that
    /// share an opening are usually related, and the one the user picked is far more often
    /// a prefix match than a suffix match.
    /// </summary>
    public static double JaroWinkler(string a, string b, double prefixScale = 0.1)
    {
        var jaro = Jaro(a, b);
        if (jaro < 0.7) return jaro; // standard threshold: do not boost weak matches

        var prefix = 0;
        var limit = Math.Min(4, Math.Min(a.Length, b.Length));
        while (prefix < limit && a[prefix] == b[prefix]) prefix++;

        return jaro + (prefix * prefixScale * (1 - jaro));
    }

    public static double Jaro(string a, string b)
    {
        if (a.Length == 0 && b.Length == 0) return 1.0;
        if (a.Length == 0 || b.Length == 0) return 0.0;
        if (a == b) return 1.0;

        var window = Math.Max(0, (Math.Max(a.Length, b.Length) / 2) - 1);
        var matchedA = new bool[a.Length];
        var matchedB = new bool[b.Length];
        var matches = 0;

        for (var i = 0; i < a.Length; i++)
        {
            var start = Math.Max(0, i - window);
            var end = Math.Min(i + window + 1, b.Length);

            for (var j = start; j < end; j++)
            {
                if (matchedB[j] || a[i] != b[j]) continue;

                matchedA[i] = true;
                matchedB[j] = true;
                matches++;
                break;
            }
        }

        if (matches == 0) return 0.0;

        // Transpositions: matched characters that appear in a different order.
        var transpositions = 0;
        var k = 0;
        for (var i = 0; i < a.Length; i++)
        {
            if (!matchedA[i]) continue;

            while (!matchedB[k]) k++;
            if (a[i] != b[k]) transpositions++;
            k++;
        }

        var m = (double)matches;
        return ((m / a.Length) + (m / b.Length) + ((m - (transpositions / 2.0)) / m)) / 3.0;
    }
}
