using Nama.Core.Normalization;

namespace Nama.Core.Identification;

/// <summary>
/// String similarity tuned for game titles. Exact string equality is useless here —
/// "ELDEN-RING-v1.12.2-FITGIRL" and "Elden Ring" must score highly, while
/// "Elden Ring" and "Elden Ring Nightreign" must stay clearly distinguishable.
/// </summary>
public static class FuzzyMatcher
{
    /// <summary>
    /// Similarity in [0,1] between two titles, combining a character-level score
    /// (typo tolerance) with a token-set score (word order and extra words).
    /// </summary>
    public static double Similarity(string? a, string? b)
    {
        var keyA = NameNormalizer.BuildMatchKey(a);
        var keyB = NameNormalizer.BuildMatchKey(b);

        if (keyA.Length == 0 || keyB.Length == 0) return 0;
        if (keyA == keyB) return 1.0;

        var character = JaroWinkler(keyA, keyB);
        var tokenScore = TokenSetSimilarity(a!, b!);

        // Whichever view is more favourable dominates, but the other still pulls the
        // score, so a good match on both axes beats a good match on only one.
        var best = Math.Max(character, tokenScore);
        var other = Math.Min(character, tokenScore);
        var combined = (best * 0.75) + (other * 0.25);

        // Containment bonus: a normalized name that is a clean prefix of the candidate
        // (or vice versa) is very often the right game.
        if (keyA.StartsWith(keyB, StringComparison.Ordinal) || keyB.StartsWith(keyA, StringComparison.Ordinal))
        {
            var ratio = (double)Math.Min(keyA.Length, keyB.Length) / Math.Max(keyA.Length, keyB.Length);
            combined = Math.Max(combined, 0.70 + (0.28 * ratio));
        }

        return Math.Clamp(combined, 0, 1);
    }

    /// <summary>
    /// Best similarity between <paramref name="query"/> and any of the candidate's titles.
    /// Aliases and Japanese titles count exactly as much as the canonical name.
    /// </summary>
    public static double BestSimilarity(string query, IEnumerable<string> titles)
    {
        var best = 0.0;
        foreach (var title in titles)
        {
            var score = Similarity(query, title);
            if (score > best) best = score;
            if (best >= 0.999) break;
        }
        return best;
    }

    /// <summary>
    /// Order-insensitive word overlap, weighted by word length so that matching
    /// "Ring" counts for less than matching "Nightreign".
    /// </summary>
    public static double TokenSetSimilarity(string a, string b)
    {
        var tokensA = NameNormalizer.BuildTokens(a);
        var tokensB = NameNormalizer.BuildTokens(b);

        if (tokensA.Length == 0 || tokensB.Length == 0) return 0;

        var setA = new HashSet<string>(tokensA, StringComparer.Ordinal);
        var setB = new HashSet<string>(tokensB, StringComparer.Ordinal);

        double Weight(string token) => Math.Sqrt(token.Length);

        var intersectionWeight = 0.0;
        foreach (var token in setA)
        {
            if (setB.Contains(token))
            {
                intersectionWeight += Weight(token);
                continue;
            }

            // Near-miss on an individual word (plural, typo, romanization variant).
            var bestPartial = 0.0;
            foreach (var other in setB)
            {
                var s = JaroWinkler(token, other);
                if (s > bestPartial) bestPartial = s;
            }
            if (bestPartial >= 0.90)
                intersectionWeight += Weight(token) * bestPartial * 0.8;
        }

        var unionWeight = setA.Sum(Weight) + setB.Sum(Weight) - intersectionWeight;
        return unionWeight <= 0 ? 0 : Math.Clamp(intersectionWeight / unionWeight, 0, 1);
    }

    /// <summary>
    /// Jaro-Winkler similarity. Chosen over Levenshtein because it rewards a shared
    /// prefix, which matches how sequels and editions are named.
    /// </summary>
    public static double JaroWinkler(string a, string b)
    {
        var jaro = Jaro(a, b);
        if (jaro < 0.7) return jaro;

        var prefix = 0;
        var max = Math.Min(4, Math.Min(a.Length, b.Length));
        while (prefix < max && a[prefix] == b[prefix]) prefix++;

        return jaro + (prefix * 0.1 * (1 - jaro));
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
