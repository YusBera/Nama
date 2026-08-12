using Nama.Core.Aggregation;
using Nama.Core.Models;
using Nama.Core.Normalization;
using Nama.Providers;
using Nama.Storage;

namespace Nama.Cli;

/// <summary>Drives the provider layer from the command line, before any UI exists.</summary>
internal static class ProviderCommands
{
    private static (ProviderSet Providers, SqliteSearchCache Cache) Build()
    {
        var settings = NamaSettings.Load();
        var cache = new SqliteSearchCache();

        var options = new ProviderOptions
        {
            SteamGridDbApiKey = settings.SteamGridDbApiKey,
            EnableDlsite = settings.ExperimentalDlsiteEnabled,
            EnableVndb = settings.ExperimentalVndbEnabled,
        };

        return (ProviderFactory.Create(options, cache), cache);
    }

    /// <summary>Normalizes the input, then searches every available provider with the result.</summary>
    public static async Task<int> SearchAsync(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("search: expected a name.");
            return 1;
        }

        var raw = string.Join(' ', args);
        var analysis = new NameNormalizer().Normalize(raw);

        Console.WriteLine($"raw         {analysis.Raw}");
        Console.WriteLine($"normalized  {analysis.Normalized}");
        Console.WriteLine();

        var (providers, cache) = Build();
        using (providers)
        using (cache)
        {
            Report(providers);

            var aggregator = new GameSearchAggregator(providers.GameProviders);
            var started = DateTime.UtcNow;

            // The identification pipeline (phase 4) will weight these; here the point is
            // simply to confirm every provider answers.
            var results = await aggregator.SearchManyAsync(
                analysis.Candidates.Take(2).Select(c => c.Value)).ConfigureAwait(false);

            Console.WriteLine($"{results.Candidates.Count} results in {(DateTime.UtcNow - started).TotalMilliseconds:0} ms");
            if (results.FailedProviders.Count > 0)
            {
                Console.WriteLine($"failed: {string.Join(", ", results.FailedProviders)}");
            }

            Console.WriteLine();

            foreach (var candidate in results.Candidates)
            {
                var year = candidate.ReleaseDate?.Year.ToString() ?? "----";
                Console.WriteLine($"  [{candidate.Source}:{candidate.SourceId}] {candidate.Name}");
                Console.WriteLine($"      {candidate.Developer ?? "unknown"} · {year}");

                if (!string.IsNullOrWhiteSpace(candidate.JapaneseName))
                {
                    Console.WriteLine($"      jp: {candidate.JapaneseName}");
                }

                if (candidate.Aliases.Count > 0)
                {
                    Console.WriteLine($"      aliases: {string.Join(" | ", candidate.Aliases.Take(3))}");
                }
            }
        }

        return 0;
    }

    /// <summary>Fetches and ranks artwork for an already-identified game, e.g. <c>steam:1245620</c>.</summary>
    public static async Task<int> ArtworkAsync(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("artwork: expected <source:id> [more...], e.g. steam:1245620 vndb:v7771");
            return 1;
        }

        var ids = new List<KeyValuePair<string, string>>();
        foreach (var arg in args)
        {
            var separator = arg.IndexOf(':');
            if (separator <= 0)
            {
                Console.Error.WriteLine($"artwork: '{arg}' is not <source:id>.");
                return 1;
            }

            ids.Add(new KeyValuePair<string, string>(arg[..separator], arg[(separator + 1)..]));
        }

        var (providers, cache) = Build();
        using (providers)
        using (cache)
        {
            Report(providers);

            var game = new GameRef(ids, ids[0].Value);
            var started = DateTime.UtcNow;
            var collection = await new ArtworkAggregator(providers.ArtworkProviders)
                .GetArtworkAsync(game).ConfigureAwait(false);

            Console.WriteLine($"{collection.All.Count} images in {(DateTime.UtcNow - started).TotalMilliseconds:0} ms");
            if (collection.SkippedProviders.Count > 0)
            {
                Console.WriteLine($"skipped: {string.Join(", ", collection.SkippedProviders)}");
            }

            if (collection.FailedProviders.Count > 0)
            {
                Console.WriteLine($"failed:  {string.Join(", ", collection.FailedProviders)}");
            }

            Console.WriteLine();

            foreach (var type in collection.AvailableTypes)
            {
                var all = collection.OfType(type);
                var recommended = ArtworkRanker.Recommended(all, type);

                Console.WriteLine($"{type.ToString().ToUpperInvariant()}  ({all.Count} available, showing top {recommended.Count})");

                foreach (var art in recommended)
                {
                    var votes = art.Votes is { } v ? $"{v}v" : "-";
                    var nsfw = art.IsNsfw ? " nsfw" : string.Empty;
                    Console.WriteLine(
                        $"   {ArtworkRanker.Score(art, type):0.000}  {art.Width,5}x{art.Height,-5} " +
                        $"{art.Source,-12} {votes,-7}{nsfw}  {art.Url}");
                }

                Console.WriteLine();
            }
        }

        return 0;
    }

    private static void Report(ProviderSet providers)
    {
        var unavailable = providers.GameProviders.Cast<object>()
            .Concat(providers.ArtworkProviders)
            .Distinct()
            .Where(p => p is Core.Abstractions.IGameProvider { IsAvailable: false }
                          or Core.Abstractions.IArtworkProvider { IsAvailable: false })
            .Select(p => p is Core.Abstractions.IGameProvider g ? g.DisplayName : ((Core.Abstractions.IArtworkProvider)p).DisplayName)
            .Distinct()
            .ToList();

        if (unavailable.Count > 0)
        {
            Console.WriteLine($"unavailable: {string.Join(", ", unavailable)}  (set a key with: nama key <steamgriddb-key>)");
            Console.WriteLine();
        }
    }

    /// <summary>Stores the SteamGridDB key, DPAPI-encrypted for the current user.</summary>
    public static int SetKey(string[] args)
    {
        var settings = NamaSettings.Load();

        if (args.Length == 0)
        {
            Console.WriteLine(settings.SteamGridDbApiKey is { Length: > 0 } existing
                ? $"SteamGridDB key is set ({existing.Length} chars, stored encrypted)."
                : "No SteamGridDB key set. Get one at https://www.steamgriddb.com/profile/preferences/api");
            return 0;
        }

        settings.SteamGridDbApiKey = args[0];
        settings.Save();

        Console.WriteLine($"Key saved, encrypted for this Windows user, to {NamaSettings.FilePath}");
        return 0;
    }

    public static async Task<int> ClearCacheAsync()
    {
        using var cache = new SqliteSearchCache();
        var before = cache.Count();
        await cache.ClearAsync().ConfigureAwait(false);

        Console.WriteLine($"Cleared {before} cached responses from {cache.DatabasePath}");
        return 0;
    }
}
