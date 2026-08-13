using System.Text;
using Nama.Core.Models;
using Nama.SteamIntegration;
using Xunit;

namespace Nama.Tests;

/// <summary>
/// Exercises SteamManager against a throwaway directory laid out like a real Steam
/// userdata folder, so the shortcut and artwork writes are verified end to end.
/// </summary>
public sealed class SteamManagerTests : IDisposable
{
    private readonly string _root;
    private readonly SteamUser _user;
    private readonly SteamManager _manager;

    public SteamManagerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "nama-tests", Guid.NewGuid().ToString("N"));
        var config = Path.Combine(_root, "userdata", "12345678", "config");
        Directory.CreateDirectory(config);

        _user = new SteamUser
        {
            AccountId = "12345678",
            ConfigPath = config,
            PersonaName = "Tester",
        };

        // Stand-in downloader so no network is involved.
        _manager = new SteamManager((_, _) => Task.FromResult<byte[]?>(Encoding.UTF8.GetBytes("png-bytes")));
    }

    [Fact]
    public void Missing_shortcuts_file_reads_as_an_empty_library()
    {
        Assert.Empty(_manager.GetExistingShortcuts(_user));
    }

    [Fact]
    public void Adds_and_reads_back_a_shortcut()
    {
        var shortcut = SteamShortcut.Create(@"D:\Games\Elden Ring\eldenring.exe", @"D:\Games\Elden Ring", "Elden Ring");
        _manager.AddShortcut(_user, shortcut, backup: false);

        var stored = Assert.Single(_manager.GetExistingShortcuts(_user));
        Assert.Equal("Elden Ring", stored.AppName);
        Assert.Equal(@"""D:\Games\Elden Ring\eldenring.exe""", stored.Exe);
        Assert.Equal(shortcut.AppId, stored.AppId);
    }

    [Fact]
    public void Adds_multiple_shortcuts_without_overwriting()
    {
        _manager.AddShortcut(_user, SteamShortcut.Create(@"D:\a\a.exe", @"D:\a", "A"), backup: false);
        _manager.AddShortcut(_user, SteamShortcut.Create(@"D:\b\b.exe", @"D:\b", "B"), backup: false);

        var stored = _manager.GetExistingShortcuts(_user);
        Assert.Equal(2, stored.Count);
        Assert.Contains(stored, s => s.AppName == "A");
        Assert.Contains(stored, s => s.AppName == "B");
    }

    [Fact]
    public void Detects_a_duplicate_by_executable_even_when_renamed()
    {
        _manager.AddShortcut(_user, SteamShortcut.Create(@"D:\Games\g.exe", @"D:\Games", "Old Name"), backup: false);

        var existing = _manager.DetectExistingEntry(_user, @"D:\Games\g.exe", "A Totally Different Name");

        Assert.NotNull(existing);
        Assert.Equal(DuplicateMatch.SameExecutable, existing!.Value.MatchKind);
        Assert.Equal("Old Name", existing.Value.Shortcut.AppName);
    }

    [Fact]
    public void Detects_a_duplicate_by_name_when_the_target_differs()
    {
        _manager.AddShortcut(_user, SteamShortcut.Create(@"D:\one\g.exe", @"D:\one", "Elden Ring"), backup: false);

        var existing = _manager.DetectExistingEntry(_user, @"D:\two\g.exe", "Elden Ring");

        Assert.NotNull(existing);
        Assert.Equal(DuplicateMatch.SameName, existing!.Value.MatchKind);
    }

    [Fact]
    public void Reports_no_duplicate_for_an_unrelated_game()
    {
        _manager.AddShortcut(_user, SteamShortcut.Create(@"D:\one\g.exe", @"D:\one", "Elden Ring"), backup: false);

        Assert.Null(_manager.DetectExistingEntry(_user, @"D:\two\h.exe", "Hades"));
    }

    [Fact]
    public void Updating_a_renamed_shortcut_replaces_it_rather_than_duplicating()
    {
        var original = SteamShortcut.Create(@"D:\Games\g.exe", @"D:\Games", "Old Name");
        _manager.AddShortcut(_user, original, backup: false);

        var renamed = SteamShortcut.Create(@"D:\Games\g.exe", @"D:\Games", "New Name");
        _manager.UpdateShortcut(_user, renamed, original.AppId, backup: false);

        var stored = Assert.Single(_manager.GetExistingShortcuts(_user));
        Assert.Equal("New Name", stored.AppName);
    }

    [Fact]
    public void Updating_preserves_play_time_from_the_replaced_entry()
    {
        var original = SteamShortcut.Create(@"D:\Games\g.exe", @"D:\Games", "Game");
        original.LastPlayTime = 1_700_000_000;
        _manager.AddShortcut(_user, original, backup: false);

        var replacement = SteamShortcut.Create(@"D:\Games\g.exe", @"D:\Games", "Game Renamed");
        _manager.UpdateShortcut(_user, replacement, original.AppId, backup: false);

        Assert.Equal(1_700_000_000, Assert.Single(_manager.GetExistingShortcuts(_user)).LastPlayTime);
    }

    [Fact]
    public void Removes_a_shortcut_and_reindexes_the_rest()
    {
        var a = SteamShortcut.Create(@"D:\a\a.exe", @"D:\a", "A");
        var b = SteamShortcut.Create(@"D:\b\b.exe", @"D:\b", "B");
        _manager.AddShortcut(_user, a, backup: false);
        _manager.AddShortcut(_user, b, backup: false);

        Assert.True(_manager.RemoveShortcut(_user, a.AppId, backup: false));

        var stored = Assert.Single(_manager.GetExistingShortcuts(_user));
        Assert.Equal("B", stored.AppName);
    }

    [Fact]
    public void Removing_a_missing_shortcut_reports_false()
    {
        _manager.AddShortcut(_user, SteamShortcut.Create(@"D:\a\a.exe", @"D:\a", "A"), backup: false);
        Assert.False(_manager.RemoveShortcut(_user, 12345, backup: false));
    }

    [Fact]
    public void Preserves_unknown_fields_written_by_other_tools()
    {
        var shortcut = SteamShortcut.Create(@"D:\a\a.exe", @"D:\a", "A");
        shortcut.UnknownFields.Add(new KeyValuePair<string, SteamIntegration.Vdf.VdfNode>(
            "SomeFutureField", SteamIntegration.Vdf.VdfNode.FromString("keep me")));

        _manager.AddShortcut(_user, shortcut, backup: false);

        var stored = Assert.Single(_manager.GetExistingShortcuts(_user));
        Assert.Contains(stored.UnknownFields, f => f.Key == "SomeFutureField");
    }

    [Fact]
    public async Task Applies_artwork_using_Steams_file_naming_scheme()
    {
        var shortcut = SteamShortcut.Create(@"D:\Games\g.exe", @"D:\Games", "Game");

        var selections = new Dictionary<ArtworkType, Artwork>
        {
            [ArtworkType.Grid] = Art(ArtworkType.Grid, "https://example.test/a.png"),
            [ArtworkType.Cover] = Art(ArtworkType.Cover, "https://example.test/b.png"),
            [ArtworkType.Hero] = Art(ArtworkType.Hero, "https://example.test/c.png"),
            [ArtworkType.Logo] = Art(ArtworkType.Logo, "https://example.test/d.png"),
            [ArtworkType.Icon] = Art(ArtworkType.Icon, "https://example.test/e.png"),
        };

        var (applied, failed) = await _manager.ApplyArtworkAsync(_user, shortcut, selections);

        Assert.Empty(failed);
        Assert.Equal(5, applied.Count);

        var id = shortcut.ArtworkId;
        Assert.True(File.Exists(Path.Combine(_user.GridPath, $"{id}.png")));
        Assert.True(File.Exists(Path.Combine(_user.GridPath, $"{id}p.png")));
        Assert.True(File.Exists(Path.Combine(_user.GridPath, $"{id}_hero.png")));
        Assert.True(File.Exists(Path.Combine(_user.GridPath, $"{id}_logo.png")));
        Assert.True(File.Exists(Path.Combine(_user.GridPath, $"{id}_icon.png")));
    }

    [Fact]
    public async Task Applying_an_icon_points_the_shortcut_at_the_written_file()
    {
        var shortcut = SteamShortcut.Create(@"D:\Games\g.exe", @"D:\Games", "Game");

        await _manager.ApplyArtworkAsync(_user, shortcut, new Dictionary<ArtworkType, Artwork>
        {
            [ArtworkType.Icon] = Art(ArtworkType.Icon, "https://example.test/e.png"),
        });

        Assert.True(File.Exists(shortcut.Icon));
        Assert.EndsWith("_icon.png", shortcut.Icon);
    }

    [Fact]
    public async Task Replacing_artwork_clears_the_previous_file_extension()
    {
        var shortcut = SteamShortcut.Create(@"D:\Games\g.exe", @"D:\Games", "Game");

        await _manager.ApplyArtworkAsync(_user, shortcut, new Dictionary<ArtworkType, Artwork>
        {
            [ArtworkType.Grid] = Art(ArtworkType.Grid, "https://example.test/a.jpg"),
        });

        Assert.True(File.Exists(Path.Combine(_user.GridPath, $"{shortcut.ArtworkId}.jpg")));

        await _manager.ApplyArtworkAsync(_user, shortcut, new Dictionary<ArtworkType, Artwork>
        {
            [ArtworkType.Grid] = Art(ArtworkType.Grid, "https://example.test/a.png"),
        });

        // A leftover .jpg would win the lookup and show the old artwork.
        Assert.False(File.Exists(Path.Combine(_user.GridPath, $"{shortcut.ArtworkId}.jpg")));
        Assert.True(File.Exists(Path.Combine(_user.GridPath, $"{shortcut.ArtworkId}.png")));
    }

    [Fact]
    public async Task Reports_artwork_that_could_not_be_downloaded()
    {
        var manager = new SteamManager((_, _) => Task.FromResult<byte[]?>(null));
        var shortcut = SteamShortcut.Create(@"D:\Games\g.exe", @"D:\Games", "Game");

        var (applied, failed) = await manager.ApplyArtworkAsync(_user, shortcut, new Dictionary<ArtworkType, Artwork>
        {
            [ArtworkType.Grid] = Art(ArtworkType.Grid, "https://example.test/a.png"),
        });

        Assert.Empty(applied);
        Assert.Single(failed);
    }

    [Fact]
    public void Writes_a_backup_when_asked()
    {
        _manager.AddShortcut(_user, SteamShortcut.Create(@"D:\a\a.exe", @"D:\a", "A"), backup: false);
        _manager.AddShortcut(_user, SteamShortcut.Create(@"D:\b\b.exe", @"D:\b", "B"), backup: true);

        Assert.True(File.Exists(_user.ShortcutsFile + ".nama.bak"));
    }

    [Fact]
    public void Surfaces_a_friendly_error_for_a_corrupt_shortcuts_file()
    {
        File.WriteAllBytes(_user.ShortcutsFile, [0x7F, 0x41, 0x42, 0x43]);

        var ex = Assert.Throws<SteamException>(() => _manager.GetExistingShortcuts(_user));
        Assert.Contains("not in the expected format", ex.Message);
    }

    [Theory]
    [InlineData(ArtworkType.Grid, "123.png")]
    [InlineData(ArtworkType.Cover, "123p.png")]
    [InlineData(ArtworkType.Hero, "123_hero.png")]
    [InlineData(ArtworkType.Logo, "123_logo.png")]
    [InlineData(ArtworkType.Icon, "123_icon.png")]
    public void Grid_file_names_follow_Steams_conventions(ArtworkType type, string expected)
    {
        Assert.Equal(expected, SteamManager.GridFileName(123u, type, ".png"));
    }

    private static Artwork Art(ArtworkType type, string url) => new()
    {
        Id = url,
        Type = type,
        Url = url,
        Source = "Test",
        Width = 600,
        Height = 900,
    };

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is harmless.
        }
    }
}
