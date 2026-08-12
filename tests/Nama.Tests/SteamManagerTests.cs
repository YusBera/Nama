using Nama.Steam;
using Nama.Steam.Models;
using Nama.Steam.Vdf;

namespace Nama.Tests;

public class TextVdfTests
{
    /// <summary>Shape and content taken from a real loginusers.vdf.</summary>
    private const string LoginUsers = """
        "users"
        {
            "76561197960278074"
            {
                "AccountName"  "secondary_user"
                "PersonaName"  "Secondary User"
                "AutoLogin"    "0"
                "Timestamp"    "1786540661"
            }
            "76561197960265729"
            {
                "AccountName"  "primary_user"
                "PersonaName"  "Primary User"
                "AutoLogin"    "1"
                "Timestamp"    "1786547631"
            }
        }
        """;

    [Fact]
    public void Parses_nested_objects_and_values()
    {
        var root = TextVdf.Parse(LoginUsers);
        var users = root["users"];

        Assert.NotNull(users);
        Assert.Equal(2, users.Objects().Count());
        Assert.Equal("Primary User", users["76561197960265729"]!.GetString("PersonaName"));
        Assert.Equal(1786547631L, users["76561197960265729"]!.GetInt64("Timestamp"));
    }

    [Fact]
    public void Key_lookup_ignores_case()
    {
        var root = TextVdf.Parse(LoginUsers);

        Assert.NotNull(root["USERS"]);
        Assert.Equal("secondary_user", root["users"]!["76561197960278074"]!.GetString("accountname"));
    }

    [Fact]
    public void Handles_comments_and_escapes()
    {
        var root = TextVdf.Parse("""
            // leading comment
            "root"
            {
                "path"    "C:\\Games\\Test"   // trailing comment
                "quoted"  "say \"hi\""
            }
            """);

        Assert.Equal(@"C:\Games\Test", root["root"]!.GetString("path"));
        Assert.Equal("say \"hi\"", root["root"]!.GetString("quoted"));
    }

    [Fact]
    public void Missing_keys_return_null_rather_than_throwing()
    {
        var root = TextVdf.Parse(LoginUsers);

        Assert.Null(root["nope"]);
        Assert.Null(root["users"]!.GetString("nope"));
        Assert.Null(root["users"]!.GetInt64("nope"));
    }
}

public class SteamAccountTests
{
    [Fact]
    public void Converts_between_steamid64_and_the_userdata_folder_name()
    {
        Assert.Equal(1u, SteamAccount.ToAccountId(76561197960265729UL));
        Assert.Equal(76561197960265729UL, SteamAccount.ToSteamId64(1u));
    }

    [Fact]
    public void Conversion_round_trips()
    {
        foreach (var accountId in new[] { 1u, 123456789u, 1422695769u, uint.MaxValue })
        {
            Assert.Equal(accountId, SteamAccount.ToAccountId(SteamAccount.ToSteamId64(accountId)));
        }
    }
}

public class DuplicateDetectionTests
{
    private static readonly string FixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "shortcuts.real.vdf");

    private static ShortcutsFile Load() => ShortcutsFile.Load(FixturePath);

    [Fact]
    public void Finds_an_existing_entry_by_executable_path()
    {
        var found = new SteamManager().DetectExistingEntry(
            Load(),
            @"C:\GAMES\PC GAMES\Umineko\onscripter-ru.exe");

        Assert.NotNull(found);
        Assert.Equal("Umineko Project", found.AppName);
    }

    [Theory]
    // The stored Exe is quoted; the path Nama holds will not be. Casing and trailing
    // separators vary too. All of these must resolve to the same entry.
    [InlineData(@"c:\games\pc games\umineko\onscripter-ru.exe")]
    [InlineData(@"C:\GAMES\PC GAMES\Umineko\..\Umineko\onscripter-ru.exe")]
    [InlineData("\"C:\\GAMES\\PC GAMES\\Umineko\\onscripter-ru.exe\"")]
    public void Path_matching_is_insensitive_to_quoting_case_and_form(string path)
    {
        Assert.NotNull(new SteamManager().DetectExistingEntry(Load(), path));
    }

    [Fact]
    public void Falls_back_to_display_name_when_the_path_differs()
    {
        var found = new SteamManager().DetectExistingEntry(
            Load(),
            @"D:\SomewhereElse\game.exe",
            "Diablo IV");

        Assert.NotNull(found);
        Assert.Equal("Diablo IV", found.AppName);
    }

    [Fact]
    public void Returns_null_for_a_game_that_is_not_there()
    {
        Assert.Null(new SteamManager().DetectExistingEntry(
            Load(),
            @"D:\Games\Nothing\nothing.exe",
            "Definitely Not Added"));
    }

    [Fact]
    public void Matches_a_japanese_display_name()
    {
        var found = new SteamManager().DetectExistingEntry(Load(), @"X:\none.exe", "らぶらぼ");

        Assert.NotNull(found);
    }
}
