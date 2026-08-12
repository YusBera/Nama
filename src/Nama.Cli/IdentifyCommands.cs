using Nama.Core.Aggregation;
using Nama.Core.Identification;
using Nama.Core.Models;
using Nama.Providers;
using Nama.Steam;
using Nama.Steam.Models;
using Nama.Steam.Writing;
using Nama.Storage;

namespace Nama.Cli;

/// <summary>Identification and the write path, driven from the command line.</summary>
internal static class IdentifyCommands
{
    /// <summary>Shows what Nama makes of a path, without contacting any provider.</summary>
    public static int Inspect(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("inspect: expected a path.");
            return 1;
        }

        var result = new CandidateExtractor().Extract(args[0]);

        Console.WriteLine($"executable  {result.ExecutablePath}");
        Console.WriteLine($"startdir    {result.StartDirectory}");
        Console.WriteLine($"best guess  {result.BestGuess}");
        Console.WriteLine($"generic exe {result.ExecutableNameWasGeneric}");
        if (result.Warning is not null) Console.WriteLine($"warning     {result.Warning}");

        Console.WriteLine();
        Console.WriteLine("candidates");
        foreach (var candidate in result.Candidates.Take(12))
        {
            Console.WriteLine($"  {candidate.Weight:0.000}  {candidate.Origin,-20}  {candidate.Value}");
        }

        return 0;
    }

    /// <summary>Full identification: path to ranked provider matches.</summary>
    public static async Task<int> IdentifyAsync(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("identify: expected a path.");
            return 1;
        }

        var settings = NamaSettings.Load();
        using var cache = new SqliteSearchCache();
        using var providers = ProviderFactory.Create(
            ProviderOptionsFrom(settings), cache);

        var identifier = new GameIdentifier(
            new CandidateExtractor(), new GameSearchAggregator(providers.GameProviders));

        var started = DateTime.UtcNow;
        var result = await identifier.IdentifyAsync(args[0]).ConfigureAwait(false);

        Console.WriteLine($"executable  {result.Extraction.ExecutablePath}");
        Console.WriteLine($"best guess  {result.Extraction.BestGuess}");
        Console.WriteLine($"queries     {string.Join(" | ", result.QueriesUsed)}");
        Console.WriteLine($"confident   {result.IsConfident}");
        Console.WriteLine($"elapsed     {(DateTime.UtcNow - started).TotalMilliseconds:0} ms");
        Console.WriteLine();

        if (result.Matches.Count == 0)
        {
            Console.WriteLine("No matches. Try: nama search \"<name>\"");
            return 0;
        }

        foreach (var match in result.Matches.Take(8))
        {
            var year = match.ReleaseDate?.Year.ToString() ?? "----";
            Console.WriteLine($"  {match.Confidence:0.000}  [{match.Source}:{match.SourceId}] {match.Name}");
            Console.WriteLine($"           {match.Developer ?? "unknown"} · {year}");
        }

        return 0;
    }

    /// <summary>
    /// Adds a game to Steam. Dry run by default — a live write needs <c>--commit</c>.
    /// </summary>
    public static async Task<int> AddAsync(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("""
                add <path> [options]
                  --commit             Actually write. Without this, nothing is written.
                  --name "<name>"      Override the Steam display name.
                  --pick <n>           Choose the nth match (1-based, default 1).
                  --userdata <dir>     Use a different Steam userdata folder (for safe testing).
                  --no-artwork         Skip artwork entirely.
                """);
            return 1;
        }

        var path = args[0];
        var commit = args.Contains("--commit");
        var noArtwork = args.Contains("--no-artwork");
        var nameOverride = ValueOf(args, "--name");
        var userDataOverride = ValueOf(args, "--userdata");
        var pick = int.TryParse(ValueOf(args, "--pick"), out var p) ? Math.Max(1, p) : 1;

        var settings = NamaSettings.Load();
        using var cache = new SqliteSearchCache();
        using var providers = ProviderFactory.Create(
            ProviderOptionsFrom(settings), cache);

        var manager = new SteamManager();

        // --- resolve the Steam account -------------------------------------------------
        SteamAccount? account;
        if (userDataOverride is not null)
        {
            account = new SteamAccount
            {
                AccountId = 0,
                UserDataPath = userDataOverride,
                PersonaName = "sandbox",
            };
            Console.WriteLine($"account     SANDBOX {userDataOverride}");
        }
        else
        {
            var installation = manager.FindSteamInstallation();
            if (installation is null)
            {
                Console.Error.WriteLine("Steam installation not found.");
                return 1;
            }

            account = manager.ResolveAccount(
                manager.FindLibraryData(installation), settings.PreferredSteamAccountId);

            if (account is null)
            {
                Console.Error.WriteLine("No Steam account found.");
                return 1;
            }

            Console.WriteLine($"account     {account.Label} ({account.AccountId})");
        }

        // --- identify ------------------------------------------------------------------
        var identifier = new GameIdentifier(
            new CandidateExtractor(), new GameSearchAggregator(providers.GameProviders));

        var identification = await identifier.IdentifyAsync(path).ConfigureAwait(false);

        if (identification.Matches.Count < pick)
        {
            Console.Error.WriteLine($"Only {identification.Matches.Count} matches; cannot pick #{pick}.");
            return 1;
        }

        var chosen = identification.Matches[pick - 1];
        var displayName = nameOverride ?? chosen.Name;

        Console.WriteLine($"identified  [{chosen.Source}:{chosen.SourceId}] {chosen.Name} ({chosen.Confidence:0.000})");
        Console.WriteLine($"steam name  {displayName}");

        // --- artwork -------------------------------------------------------------------
        var selections = new Dictionary<ArtworkType, Artwork>();

        if (!noArtwork)
        {
            var game = Game.FromCandidate(chosen);
            var collection = await new ArtworkAggregator(providers.ArtworkProviders)
                .GetArtworkAsync(game.Ref).ConfigureAwait(false);

            // The CLI takes the top recommendation per slot; the UI is where a human chooses.
            foreach (var type in collection.AvailableTypes)
            {
                if (type == ArtworkType.Background) continue;

                var best = ArtworkRanker.Recommended(collection.OfType(type), type, 1).FirstOrDefault();
                if (best is not null) selections[type] = best;
            }

            Console.WriteLine($"artwork     {selections.Count} slots: {string.Join(", ", selections.Keys)}");
        }

        // --- write ---------------------------------------------------------------------
        var request = new ShortcutRequest
        {
            ExecutablePath = identification.Extraction.ExecutablePath,
            DisplayName = displayName,
            StartDirectory = identification.Extraction.StartDirectory,
            Artwork = selections,
            OnDuplicate = DuplicateAction.UpdateArtwork,
        };

        var downloader = new HttpImageDownloader(providers.Http);
        var result = await manager.AddOrUpdateShortcutAsync(
            account, request, downloader, dryRun: !commit).ConfigureAwait(false);

        Console.WriteLine();
        Console.WriteLine(commit ? "=== WRITE ===" : "=== DRY RUN (no files written; pass --commit to write) ===");

        foreach (var action in result.PlannedActions) Console.WriteLine($"  {action}");

        Console.WriteLine();
        Console.WriteLine($"success     {result.Success}");
        Console.WriteLine($"appid       {result.AppId}");
        Console.WriteLine($"update      {result.WasUpdate}");
        if (result.BackupPath is not null) Console.WriteLine($"backup      {result.BackupPath}");
        if (result.Error is not null) Console.WriteLine($"error       {result.Error}  [{result.BlockReason}]");

        if (result.Artwork is not null)
        {
            // In a dry run these paths are a plan, not an outcome — saying "ok" would
            // read as though the files had been written.
            var verb = commit && result.Success ? "ok  " : "plan";
            foreach (var (type, file) in result.Artwork.Applied) Console.WriteLine($"  {verb}  {type,-11} {file}");
            foreach (var (type, why) in result.Artwork.Failed) Console.WriteLine($"  fail  {type,-11} {why}");
        }

        return result.Success ? 0 : 1;
    }

    private static ProviderOptions ProviderOptionsFrom(NamaSettings settings) => new()
    {
        SteamGridDbApiKey = settings.SteamGridDbApiKey,
        EnableDlsite = settings.ExperimentalDlsiteEnabled,
        EnableVndb = settings.ExperimentalVndbEnabled,
    };

    private static string? ValueOf(string[] args, string flag)
    {
        var index = Array.IndexOf(args, flag);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
