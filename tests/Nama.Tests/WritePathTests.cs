using Nama.Core.Abstractions;
using Nama.Core.Models;
using Nama.Steam;
using Nama.Steam.Models;
using Nama.Steam.Vdf;
using Nama.Steam.Writing;

namespace Nama.Tests;

/// <summary>
/// The write path, exercised against a throwaway copy of a real shortcuts.vdf. Nothing
/// here touches the machine's actual Steam library.
/// </summary>
public class WritePathTests : IDisposable
{
    private static readonly string FixturePath =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "shortcuts.real.vdf");

    private readonly string _userData = Path.Combine(Path.GetTempPath(), $"nama-write-{Guid.NewGuid():N}");

    private readonly SteamAccount _account;

    public WritePathTests()
    {
        var config = Path.Combine(_userData, "config");
        Directory.CreateDirectory(config);
        File.Copy(FixturePath, Path.Combine(config, "shortcuts.vdf"));

        _account = new SteamAccount { AccountId = 1, UserDataPath = _userData };
    }

    public void Dispose()
    {
        if (Directory.Exists(_userData)) Directory.Delete(_userData, recursive: true);
    }

    private byte[] Current => File.ReadAllBytes(_account.ShortcutsPath);

    private static ShortcutRequest Request(string name, string exe, DuplicateAction onDuplicate = DuplicateAction.Fail) => new()
    {
        ExecutablePath = exe,
        DisplayName = name,
        OnDuplicate = onDuplicate,
    };

    private sealed class NoDownloader : IImageDownloader
    {
        public Task<DownloadedImage?> DownloadAsync(string url, CancellationToken ct = default) =>
            Task.FromResult<DownloadedImage?>(null);
    }

    // --- adding ------------------------------------------------------------------------

    [Fact]
    public async Task Adding_a_game_preserves_every_existing_entry()
    {
        var before = ShortcutsFile.Load(_account.ShortcutsPath).Shortcuts
            .Select(s => (s.AppName, s.Exe, s.AppIdField)).ToList();

        var result = await new SteamManager().AddOrUpdateShortcutAsync(
            _account, Request("Test Game", @"C:\Games\Test\test.exe"), new NoDownloader());

        Assert.True(result.Success, result.Error);

        var after = ShortcutsFile.Load(_account.ShortcutsPath).Shortcuts
            .Select(s => (s.AppName, s.Exe, s.AppIdField)).ToList();

        Assert.Equal(7, after.Count);
        foreach (var entry in before) Assert.Contains(entry, after);
    }

    [Fact]
    public async Task Adding_a_game_writes_a_usable_entry()
    {
        var result = await new SteamManager().AddOrUpdateShortcutAsync(
            _account, Request("Test Game", @"C:\Games\Test\test.exe"), new NoDownloader());

        var added = ShortcutsFile.Load(_account.ShortcutsPath).Shortcuts.Single(s => s.AppName == "Test Game");

        Assert.Equal("\"C:\\Games\\Test\\test.exe\"", added.Exe);
        Assert.Equal(@"C:\Games\Test\", added.StartDir);
        Assert.Equal(result.AppId, added.AppId);
        Assert.False(result.WasUpdate);
    }

    [Fact]
    public async Task A_new_entry_carries_the_full_field_set_steam_expects()
    {
        await new SteamManager().AddOrUpdateShortcutAsync(
            _account, Request("Test Game", @"C:\Games\Test\test.exe"), new NoDownloader());

        var root = BinaryVdf.Read(Current);
        var entry = root.GetMap("shortcuts")!.ChildMaps().Last().Map;

        foreach (var field in new[]
                 {
                     "appid", "AppName", "Exe", "StartDir", "icon", "ShortcutPath", "LaunchOptions",
                     "IsHidden", "AllowDesktopConfig", "AllowOverlay", "OpenVR", "Devkit",
                     "DevkitGameID", "DevkitOverrideAppID", "LastPlayTime", "FlatpakAppID", "sortas", "tags",
                 })
        {
            Assert.True(entry.Contains(field), $"new entry is missing '{field}'");
        }
    }

    [Fact]
    public async Task The_written_file_still_round_trips()
    {
        await new SteamManager().AddOrUpdateShortcutAsync(
            _account, Request("Test Game", @"C:\Games\Test\test.exe"), new NoDownloader());

        Assert.True(BinaryVdf.RoundTrips(Current, out _));
    }

    [Fact]
    public async Task A_backup_of_the_previous_file_is_kept()
    {
        var original = Current;

        var result = await new SteamManager().AddOrUpdateShortcutAsync(
            _account, Request("Test Game", @"C:\Games\Test\test.exe"), new NoDownloader());

        Assert.NotNull(result.BackupPath);
        Assert.Equal(original, File.ReadAllBytes(result.BackupPath));
    }

    [Fact]
    public async Task Only_the_five_most_recent_backups_are_kept()
    {
        var manager = new SteamManager();

        for (var i = 0; i < 8; i++)
        {
            await manager.AddOrUpdateShortcutAsync(
                _account, Request($"Game {i}", $@"C:\Games\G{i}\g.exe"), new NoDownloader());
        }

        Assert.True(manager.ListBackups(_account).Count <= 5);
    }

    // --- duplicates ---------------------------------------------------------------------

    [Fact]
    public async Task A_duplicate_is_refused_rather_than_silently_added()
    {
        var result = await new SteamManager().AddOrUpdateShortcutAsync(
            _account,
            Request("Umineko Project", @"C:\GAMES\PC GAMES\Umineko\onscripter-ru.exe"),
            new NoDownloader());

        Assert.False(result.Success);
        Assert.NotNull(result.ExistingEntry);
        Assert.Equal("Umineko Project", result.ExistingEntry.AppName);

        // And nothing was written.
        Assert.Equal(6, ShortcutsFile.Load(_account.ShortcutsPath).Shortcuts.Count);
    }

    [Fact]
    public async Task Update_artwork_leaves_the_existing_entry_untouched()
    {
        var before = Current;

        var result = await new SteamManager().AddOrUpdateShortcutAsync(
            _account,
            Request("A Different Name", @"C:\GAMES\PC GAMES\Umineko\onscripter-ru.exe", DuplicateAction.UpdateArtwork),
            new NoDownloader());

        Assert.True(result.Success, result.Error);
        Assert.True(result.WasUpdate);

        // The name was NOT changed, and neither was anything else.
        Assert.Equal(before, Current);
    }

    [Fact]
    public async Task Update_artwork_reuses_the_existing_app_id_rather_than_recomputing_it()
    {
        // Steam assigns its own ids and artwork filenames follow the stored value.
        // Recomputing would point artwork at a filename Steam never reads.
        var existing = ShortcutsFile.Load(_account.ShortcutsPath).Shortcuts
            .Single(s => s.AppName == "Umineko Project");

        var result = await new SteamManager().AddOrUpdateShortcutAsync(
            _account,
            Request("Umineko no Naku Koro ni", @"C:\GAMES\PC GAMES\Umineko\onscripter-ru.exe", DuplicateAction.UpdateArtwork),
            new NoDownloader());

        Assert.Equal(existing.AppId, result.AppId);
        Assert.NotEqual(existing.ComputedAppId, result.AppId);
    }

    [Fact]
    public async Task Replacing_an_entry_renames_it_without_changing_its_app_id()
    {
        var existing = ShortcutsFile.Load(_account.ShortcutsPath).Shortcuts
            .Single(s => s.AppName == "Umineko Project");

        var result = await new SteamManager().AddOrUpdateShortcutAsync(
            _account,
            Request("Umineko no Naku Koro ni", @"C:\GAMES\PC GAMES\Umineko\onscripter-ru.exe", DuplicateAction.ReplaceEntry),
            new NoDownloader());

        Assert.True(result.Success, result.Error);

        var after = ShortcutsFile.Load(_account.ShortcutsPath);
        Assert.Equal(6, after.Shortcuts.Count); // renamed, not duplicated
        Assert.Contains(after.Shortcuts, s => s.AppName == "Umineko no Naku Koro ni");

        // The id must survive the rename, or the artwork is orphaned.
        Assert.Equal(existing.AppId, after.Shortcuts.Single(s => s.AppName == "Umineko no Naku Koro ni").AppId);
    }

    [Fact]
    public async Task A_duplicate_is_detected_by_path_even_under_a_different_name()
    {
        var result = await new SteamManager().AddOrUpdateShortcutAsync(
            _account,
            Request("Completely Different Title", @"c:\games\pc games\umineko\ONSCRIPTER-RU.EXE"),
            new NoDownloader());

        Assert.False(result.Success);
        Assert.NotNull(result.ExistingEntry);
    }

    // --- removal ------------------------------------------------------------------------

    [Fact]
    public async Task Removing_a_shortcut_leaves_the_others_intact()
    {
        var manager = new SteamManager();
        var added = await manager.AddOrUpdateShortcutAsync(
            _account, Request("Temporary", @"C:\Games\Temp\t.exe"), new NoDownloader());

        var result = manager.RemoveShortcut(_account, added.AppId);

        Assert.True(result.Success, result.Error);

        var after = ShortcutsFile.Load(_account.ShortcutsPath);
        Assert.Equal(6, after.Shortcuts.Count);
        Assert.DoesNotContain(after.Shortcuts, s => s.AppName == "Temporary");
    }

    [Fact]
    public void Removing_an_unknown_shortcut_reports_it_and_changes_nothing()
    {
        var before = Current;

        var result = new SteamManager().RemoveShortcut(_account, 12345);

        Assert.False(result.Success);
        Assert.Equal(before, Current);
    }

    // --- dry run ------------------------------------------------------------------------

    [Fact]
    public async Task A_dry_run_writes_nothing()
    {
        var before = Current;

        var result = await new SteamManager().AddOrUpdateShortcutAsync(
            _account, Request("Test Game", @"C:\Games\Test\test.exe"), new NoDownloader(), dryRun: true);

        Assert.True(result.DryRun);
        Assert.NotEmpty(result.PlannedActions);
        Assert.Equal(before, Current);
        Assert.Empty(new SteamManager().ListBackups(_account));
    }

    // --- guards -------------------------------------------------------------------------

    [Fact]
    public void Writing_is_blocked_when_the_file_does_not_round_trip()
    {
        File.WriteAllBytes(_account.ShortcutsPath, [0x00, (byte)'x', 0x00, 0x7F, (byte)'y', 0x00, 0x08]);

        var file = ShortcutsFile.Load(_account.ShortcutsPath);
        var readiness = new SteamManager().CheckWriteReadiness(_account, file);

        Assert.False(file.RoundTrips);
        Assert.False(readiness.CanWrite);
        Assert.Equal(WriteBlockReason.RoundTripFailed, readiness.Reason);
    }

    [Fact]
    public void An_unparseable_file_loads_without_throwing_so_the_reason_can_be_reported()
    {
        File.WriteAllBytes(_account.ShortcutsPath, [0x00, (byte)'x', 0x00, 0x7F, (byte)'y', 0x00, 0x08]);

        var file = ShortcutsFile.Load(_account.ShortcutsPath);

        Assert.False(file.RoundTrips);
        Assert.Empty(file.Shortcuts);
    }

    [Fact]
    public async Task A_blocked_write_returns_a_reason_instead_of_writing()
    {
        File.WriteAllBytes(_account.ShortcutsPath, [0x00, (byte)'x', 0x00, 0x7F, (byte)'y', 0x00, 0x08]);
        var before = Current;

        var result = await new SteamManager().AddOrUpdateShortcutAsync(
            _account, Request("Test Game", @"C:\Games\Test\test.exe"), new NoDownloader());

        Assert.False(result.Success);
        Assert.Equal(WriteBlockReason.RoundTripFailed, result.BlockReason);
        Assert.Equal(before, Current);
    }
}

/// <summary>The guarded-write primitive on its own, including its rollback path.</summary>
public class ShortcutFileWriterTests : IDisposable
{
    private static readonly string FixturePath =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "shortcuts.real.vdf");

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"nama-writer-{Guid.NewGuid():N}");

    private readonly string _path;

    public ShortcutFileWriterTests()
    {
        Directory.CreateDirectory(_directory);
        _path = Path.Combine(_directory, "shortcuts.vdf");
        File.Copy(FixturePath, _path);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void Verification_failure_restores_the_original_file()
    {
        var original = File.ReadAllBytes(_path);

        // A payload that drops every existing entry, while claiming they must survive.
        var empty = new VdfMap();
        empty.Add("shortcuts", new VdfMap());
        var payload = BinaryVdf.Write(empty);

        var survivors = ShortcutFileWriter.Fingerprint(BinaryVdf.Read(original).GetMap("shortcuts")!);

        var exception = Assert.Throws<ShortcutWriteException>(
            () => ShortcutFileWriter.WriteVerified(_path, payload, survivors));

        Assert.Contains("Verification failed", exception.Message);

        // The whole point: a failed write leaves the file exactly as it was.
        Assert.Equal(original, File.ReadAllBytes(_path));
    }

    [Fact]
    public void A_successful_write_leaves_no_temporary_file()
    {
        var root = BinaryVdf.Read(File.ReadAllBytes(_path));
        var survivors = ShortcutFileWriter.Fingerprint(root.GetMap("shortcuts")!);

        ShortcutFileWriter.WriteVerified(_path, BinaryVdf.Write(root), survivors);

        Assert.Empty(Directory.GetFiles(_directory, "*.nama-tmp"));
    }

    [Fact]
    public void Fingerprints_change_when_an_entry_changes_and_not_otherwise()
    {
        var container = BinaryVdf.Read(File.ReadAllBytes(_path)).GetMap("shortcuts")!;
        var before = ShortcutFileWriter.Fingerprint(container);

        new SteamShortcut(container.GetMap("0")!).AppName = "Changed";
        var after = ShortcutFileWriter.Fingerprint(container);

        Assert.NotEqual(before["0"], after["0"]);
        foreach (var key in new[] { "1", "2", "3", "4", "5" }) Assert.Equal(before[key], after[key]);
    }
}
