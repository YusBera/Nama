using System.Text;
using Nama.Steam;
using Nama.Steam.Models;

namespace Nama.Tests;

public class SteamAppIdTests
{
    private static readonly string FixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "shortcuts.real.vdf");

    private static IReadOnlyList<SteamShortcut> RealShortcuts => ShortcutsFile.Load(FixturePath).Shortcuts;

    [Theory]
    [InlineData("", 0x00000000u)]
    [InlineData("123456789", 0xCBF43926u)] // the standard CRC-32/ISO-HDLC check value
    [InlineData("a", 0xE8B7BE43u)]
    [InlineData("abc", 0x352441C2u)]
    public void Crc32_matches_known_check_values(string input, uint expected)
    {
        Assert.Equal(expected, Crc32.Compute(Encoding.UTF8.GetBytes(input)));
    }

    [Fact]
    public void Generated_ids_always_have_the_high_bit_set()
    {
        var id = SteamAppId.Compute("\"C:\\Games\\test.exe\"", "Test Game");

        Assert.True((id & 0x80000000u) != 0);
    }

    [Fact]
    public void Generated_ids_are_deterministic()
    {
        var first = SteamAppId.Compute("\"C:\\Games\\test.exe\"", "Test Game");
        var second = SteamAppId.Compute("\"C:\\Games\\test.exe\"", "Test Game");

        Assert.Equal(first, second);
    }

    [Fact]
    public void Quoting_the_executable_changes_the_id()
    {
        // A real library contains both quoted and unquoted Exe values, so the id must be
        // computed from the string that will actually be written, not a tidied version.
        var quoted = SteamAppId.Compute("\"C:\\Games\\test.exe\"", "Test Game");
        var bare = SteamAppId.Compute("C:\\Games\\test.exe", "Test Game");

        Assert.NotEqual(quoted, bare);
    }

    [Fact]
    public void Signed_and_unsigned_forms_round_trip()
    {
        foreach (var shortcut in RealShortcuts)
        {
            var field = shortcut.AppIdField;

            Assert.Equal(field, SteamAppId.ToShortcutField(SteamAppId.FromShortcutField(field)));
        }
    }

    [Fact]
    public void Real_stored_ids_all_have_the_high_bit_set()
    {
        // The one property that does hold for Steam-assigned ids: they are stored as
        // negative int32, i.e. the top bit is set.
        foreach (var shortcut in RealShortcuts)
        {
            Assert.True(shortcut.AppIdField < 0, $"{shortcut.AppName} has a non-negative appid field.");
            Assert.True((shortcut.AppId & 0x80000000u) != 0);
        }
    }

    /// <summary>
    /// Locks in a measured fact that contradicts the widely repeated claim that a
    /// shortcut's app id is <c>CRC32(exe + name) | 0x80000000</c>. It is not: for
    /// shortcuts Steam created itself, no CRC32 formulation reproduces the stored id.
    /// <para>
    /// The test exists so that a future change cannot quietly start recomputing ids for
    /// existing entries. Doing so would write artwork under filenames Steam never reads —
    /// a silent failure where every image lands on disk and nothing appears in the library.
    /// </para>
    /// </summary>
    [Fact]
    public void Steam_assigned_ids_are_not_derivable_from_the_exe_and_name()
    {
        foreach (var shortcut in RealShortcuts)
        {
            var name = shortcut.AppName;
            var quoted = shortcut.Exe;
            var bare = shortcut.ExePath;

            foreach (var input in new[] { quoted + name, bare + name, name + quoted, name + bare, quoted, bare, name })
            {
                var crc = Crc32.Compute(Encoding.UTF8.GetBytes(input));

                Assert.NotEqual(shortcut.AppId, crc);
                Assert.NotEqual(shortcut.AppId, crc | 0x80000000u);
            }
        }
    }

    [Fact]
    public void Artwork_slot_stems_follow_steams_naming()
    {
        var slots = SteamManager.ArtworkSlots(3065327086).ToDictionary(s => s.Slot, s => s.Stem);

        Assert.Equal("3065327086", slots["Grid"]);
        Assert.Equal("3065327086p", slots["Cover"]);
        Assert.Equal("3065327086_hero", slots["Hero"]);
        Assert.Equal("3065327086_logo", slots["Logo"]);
    }

    [Fact]
    public void Quote_is_idempotent()
    {
        Assert.Equal("\"C:\\a.exe\"", SteamAppId.Quote("C:\\a.exe"));
        Assert.Equal("\"C:\\a.exe\"", SteamAppId.Quote("\"C:\\a.exe\""));
    }
}
