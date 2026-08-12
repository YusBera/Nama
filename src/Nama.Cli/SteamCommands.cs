using Nama.Steam;
using Nama.Steam.Models;

namespace Nama.Cli;

/// <summary>Read-only inspection of the local Steam installation.</summary>
internal static class SteamCommands
{
    public static int Info()
    {
        var manager = new SteamManager();

        var installation = manager.FindSteamInstallation();
        if (installation is null)
        {
            Console.Error.WriteLine("Steam installation not found.");
            return 1;
        }

        Console.WriteLine($"steam        {installation.Path}");
        Console.WriteLine($"running      {manager.IsSteamRunning()}");
        Console.WriteLine();

        var accounts = manager.FindLibraryData(installation);
        var selected = manager.ResolveAccount(accounts);

        Console.WriteLine("accounts (most recent first)");
        foreach (var account in accounts)
        {
            var marker = account.AccountId == selected?.AccountId ? "->" : "  ";
            Console.WriteLine(
                $" {marker} {account.AccountId,-12} {account.Label,-16} " +
                $"shortcuts={account.HasShortcuts,-5} recent={account.IsMostRecent}");
        }

        if (selected is null) return 0;

        Console.WriteLine();
        Console.WriteLine($"shortcuts for {selected.Label}");

        var file = manager.GetExistingShortcuts(selected);
        Console.WriteLine($"  path         {file.Path}");
        Console.WriteLine($"  exists       {file.Existed}");
        Console.WriteLine($"  round-trips  {file.RoundTrips}{(file.RoundTrips ? "" : "   << WRITES DISABLED")}");
        Console.WriteLine($"  entries      {file.Shortcuts.Count}");
        Console.WriteLine();

        foreach (var shortcut in file.Shortcuts)
        {
            var computed = shortcut.ComputedAppId;
            var stored = shortcut.AppId;
            var match = computed == stored ? "match" : $"MISMATCH (computed {computed})";

            Console.WriteLine($"  {shortcut.AppName}");
            Console.WriteLine($"    exe        {shortcut.Exe}");
            Console.WriteLine($"    appid      {stored} [{shortcut.AppIdField}] {match}");

            var artwork = manager.GetArtworkFiles(selected, stored);
            Console.WriteLine(artwork.Count == 0
                ? "    artwork    (none)"
                : $"    artwork    {string.Join(", ", artwork.Select(a => $"{a.Key}={Path.GetExtension(a.Value)}"))}");
        }

        return 0;
    }
}
