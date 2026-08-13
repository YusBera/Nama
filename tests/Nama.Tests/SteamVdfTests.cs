using System.Text;
using Nama.SteamIntegration;
using Nama.SteamIntegration.Vdf;
using Xunit;

namespace Nama.Tests;

public class BinaryVdfTests
{
    [Fact]
    public void Round_trips_a_shortcuts_document()
    {
        var root = VdfNode.NewObject();
        var shortcuts = VdfNode.NewObject();

        var entry = VdfNode.NewObject();
        entry.Set("appid", unchecked((int)0x8ABCDEF0));
        entry.Set("AppName", "Elden Ring");
        entry.Set("Exe", "\"D:\\Games\\Elden Ring\\eldenring.exe\"");
        entry.Set("StartDir", "\"D:\\Games\\Elden Ring\"");
        entry.Set("IsHidden", false);
        entry.Set("AllowOverlay", true);

        var tags = VdfNode.NewObject();
        tags.Add("0", VdfNode.FromString("favourite"));
        entry.Set("tags", tags);

        shortcuts.Add("0", entry);
        root.Set("shortcuts", shortcuts);

        var parsed = BinaryVdf.Parse(BinaryVdf.Serialize(root));

        var reparsed = parsed["shortcuts"]!["0"]!;
        Assert.Equal("Elden Ring", reparsed.GetString("AppName"));
        Assert.Equal("\"D:\\Games\\Elden Ring\\eldenring.exe\"", reparsed.GetString("Exe"));
        Assert.Equal(unchecked((int)0x8ABCDEF0), reparsed.GetInt("appid"));
        Assert.False(reparsed.GetBool("IsHidden"));
        Assert.True(reparsed.GetBool("AllowOverlay"));
        Assert.Equal("favourite", reparsed["tags"]!["0"]!.StringValue);
    }

    [Fact]
    public void Round_trips_non_ascii_names()
    {
        // Japanese titles must survive the UTF-8 round trip intact.
        var root = VdfNode.NewObject();
        var shortcuts = VdfNode.NewObject();

        var entry = VdfNode.NewObject();
        entry.Set("AppName", "素晴らしき日々");
        shortcuts.Add("0", entry);
        root.Set("shortcuts", shortcuts);

        var parsed = BinaryVdf.Parse(BinaryVdf.Serialize(root));
        Assert.Equal("素晴らしき日々", parsed["shortcuts"]!["0"]!.GetString("AppName"));
    }

    [Fact]
    public void Parses_a_hand_written_binary_document()
    {
        // 0x00 object "shortcuts" { 0x00 object "0" { 0x01 string AppName=Test } } end end
        var bytes = new List<byte>();

        bytes.Add(0x00);
        bytes.AddRange(NulTerminated("shortcuts"));
        bytes.Add(0x00);
        bytes.AddRange(NulTerminated("0"));
        bytes.Add(0x01);
        bytes.AddRange(NulTerminated("AppName"));
        bytes.AddRange(NulTerminated("Test"));
        bytes.Add(0x08); // close "0"
        bytes.Add(0x08); // close "shortcuts"
        bytes.Add(0x08); // close document

        var parsed = BinaryVdf.Parse(bytes.ToArray());
        Assert.Equal("Test", parsed["shortcuts"]!["0"]!.GetString("AppName"));
    }

    [Fact]
    public void Rejects_a_truncated_document()
    {
        var bytes = new List<byte> { 0x01 };
        bytes.AddRange(Encoding.UTF8.GetBytes("AppName")); // no NUL terminator

        Assert.Throws<InvalidDataException>(() => BinaryVdf.Parse(bytes.ToArray()));
    }

    [Fact]
    public void Rejects_an_unknown_type_tag()
    {
        var bytes = new List<byte> { 0x7F };
        bytes.AddRange(NulTerminated("key"));

        Assert.Throws<InvalidDataException>(() => BinaryVdf.Parse(bytes.ToArray()));
    }

    [Fact]
    public void Preserves_child_order()
    {
        var root = VdfNode.NewObject();
        var list = VdfNode.NewObject();
        for (var i = 0; i < 5; i++)
        {
            var entry = VdfNode.NewObject();
            entry.Set("AppName", $"Game {i}");
            list.Add(i.ToString(), entry);
        }
        root.Set("shortcuts", list);

        var parsed = BinaryVdf.Parse(BinaryVdf.Serialize(root));
        var children = parsed["shortcuts"]!.Children;

        Assert.Equal(5, children.Count);
        for (var i = 0; i < 5; i++)
            Assert.Equal($"Game {i}", children[i].Value.GetString("AppName"));
    }

    private static byte[] NulTerminated(string value) =>
        [.. Encoding.UTF8.GetBytes(value), 0];
}

public class TextVdfTests
{
    [Fact]
    public void Parses_loginusers_shape()
    {
        const string document = """
            "users"
            {
                "76561198000000001"
                {
                    "AccountName"   "someone"
                    "PersonaName"   "Someone"
                    "MostRecent"    "1"
                }
            }
            """;

        var root = TextVdf.Parse(document);
        var user = root["users"]!["76561198000000001"]!;

        Assert.Equal("Someone", user.GetString("PersonaName"));
        Assert.Equal("1", user.GetString("MostRecent"));
    }

    [Fact]
    public void Skips_comments_and_conditionals()
    {
        const string document = """
            // a comment
            "root"
            {
                "key" "value" [$WIN32]
            }
            """;

        Assert.Equal("value", TextVdf.Parse(document)["root"]!.GetString("key"));
    }

    [Fact]
    public void Rejects_an_unterminated_string()
    {
        Assert.Throws<InvalidDataException>(() => TextVdf.Parse("\"root\" { \"key\" \"unterminated"));
    }
}

public class SteamAppIdTests
{
    [Fact]
    public void Crc32_matches_the_standard_check_vector()
    {
        // The canonical CRC-32/ISO-HDLC check value for "123456789".
        var value = SteamAppIds.Crc32(Encoding.UTF8.GetBytes("123456789"));
        Assert.Equal(0xCBF43926u, value);
    }

    [Fact]
    public void Crc32_of_empty_input_is_zero()
    {
        Assert.Equal(0u, SteamAppIds.Crc32([]));
    }

    [Fact]
    public void Shortcut_app_id_always_has_the_high_bit_set()
    {
        // Steam distinguishes shortcut ids from real app ids by this bit.
        var id = SteamAppIds.ComputeShortcutAppId("\"D:\\Games\\game.exe\"", "Game");
        Assert.True((id & 0x8000_0000u) != 0);
    }

    [Fact]
    public void Shortcut_app_id_is_deterministic()
    {
        var first = SteamAppIds.ComputeShortcutAppId("\"D:\\g.exe\"", "Game");
        var second = SteamAppIds.ComputeShortcutAppId("\"D:\\g.exe\"", "Game");
        Assert.Equal(first, second);
    }

    [Fact]
    public void Renaming_or_retargeting_changes_the_app_id()
    {
        var baseline = SteamAppIds.ComputeShortcutAppId("\"D:\\g.exe\"", "Game");

        Assert.NotEqual(baseline, SteamAppIds.ComputeShortcutAppId("\"D:\\g.exe\"", "Other"));
        Assert.NotEqual(baseline, SteamAppIds.ComputeShortcutAppId("\"E:\\g.exe\"", "Game"));
    }

    [Fact]
    public void Signed_and_unsigned_forms_agree()
    {
        const string exe = "\"D:\\Games\\Elden Ring\\eldenring.exe\"";
        const string name = "Elden Ring";

        var unsigned = SteamAppIds.ComputeShortcutAppId(exe, name);
        var signed = SteamAppIds.ComputeShortcutAppIdSigned(exe, name);

        Assert.Equal(unsigned, SteamAppIds.ToUnsigned(signed));
    }

    [Fact]
    public void Legacy_game_id_packs_the_app_id_into_the_high_word()
    {
        const string exe = "\"D:\\g.exe\"";
        const string name = "Game";

        var appId = SteamAppIds.ComputeShortcutAppId(exe, name);
        var legacy = SteamAppIds.ComputeLegacyGameId(exe, name);

        Assert.Equal(appId, (uint)(legacy >> 32));
        Assert.Equal(0x0200_0000ul, legacy & 0xFFFF_FFFFul);
    }

    [Fact]
    public void QuotePath_adds_quotes_once()
    {
        Assert.Equal("\"D:\\g.exe\"", SteamAppIds.QuotePath("D:\\g.exe"));
        Assert.Equal("\"D:\\g.exe\"", SteamAppIds.QuotePath("\"D:\\g.exe\""));
    }
}
