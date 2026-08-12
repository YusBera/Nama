using Nama.Core.Normalization;

namespace Nama.Cli;

/// <summary>
/// Harness for driving the Nama backend without any UI. Not throwaway: it stays as the
/// integration-test surface and becomes the argument handler behind the context menu.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0) return PrintUsage();

        var rest = args.Skip(1).ToArray();

        return args[0].ToLowerInvariant() switch
        {
            "normalize" => Normalize(rest),
            "steam" => SteamCommands.Info(),
            "inspect" => IdentifyCommands.Inspect(rest),
            "identify" => await IdentifyCommands.IdentifyAsync(rest).ConfigureAwait(false),
            "add" => await IdentifyCommands.AddAsync(rest).ConfigureAwait(false),
            "search" => await ProviderCommands.SearchAsync(rest).ConfigureAwait(false),
            "artwork" => await ProviderCommands.ArtworkAsync(rest).ConfigureAwait(false),
            "key" => ProviderCommands.SetKey(rest),
            "clear-cache" => await ProviderCommands.ClearCacheAsync().ConfigureAwait(false),
            "-h" or "--help" or "help" => PrintUsage(),
            _ => Unknown(args[0]),
        };
    }

    private static int Normalize(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("normalize: expected a name or path.");
            return 1;
        }

        var normalizer = new NameNormalizer();

        foreach (var input in args)
        {
            // Accept either a bare name or a real path.
            var name = Path.IsPathRooted(input) ? Path.GetFileName(input.TrimEnd('\\', '/')) : input;
            var result = normalizer.Normalize(name);

            Console.WriteLine($"raw         {result.Raw}");
            Console.WriteLine($"normalized  {result.Normalized}");
            Console.WriteLine($"display     {result.DisplayName}");
            Console.WriteLine($"cjk         {result.HasCjk}");

            if (result.RemovedTokens.Count > 0)
            {
                Console.WriteLine($"removed     {string.Join(", ", result.RemovedTokens)}");
            }

            Console.WriteLine("candidates");
            foreach (var candidate in result.Candidates)
            {
                Console.WriteLine($"  {candidate.Weight:0.00}  {candidate.Kind,-22}  {candidate.Value}");
            }

            Console.WriteLine();
        }

        return 0;
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"Unknown command '{command}'.");
        PrintUsage();
        return 1;
    }

    private static int PrintUsage()
    {
        Console.WriteLine("""
            nama <command> [args]

              normalize <name|path>...   Show how a name is cleaned and what will be searched.
              steam                      Inspect the local Steam install (read-only).
              inspect <path>             Show name candidates from a file or folder (offline).
              identify <path>            Identify a game from a path, with ranked matches.
              add <path> [--commit]      Add to Steam. Dry run unless --commit is given.
              search <name>              Normalize, then search every available provider.
              artwork <source:id>...     Fetch and rank artwork, e.g. steam:1245620 vndb:v7771
              key [<api-key>]            Show or set the SteamGridDB key (stored encrypted).
              clear-cache                Empty the local response cache.
            """);
        return 0;
    }
}
