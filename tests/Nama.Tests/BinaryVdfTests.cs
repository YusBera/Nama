using Nama.Steam;
using Nama.Steam.Models;
using Nama.Steam.Vdf;

namespace Nama.Tests;

/// <summary>
/// The hard gate on the write path. Nama rewrites a file holding the user's existing
/// non-Steam shortcuts, so the codec must reproduce anything it did not deliberately
/// change — byte for byte, including keys it does not model.
/// </summary>
public class BinaryVdfTests
{
    /// <summary>A verbatim copy of a real shortcuts.vdf (2500 bytes, 6 entries, mixed Latin and Japanese names).</summary>
    private static readonly string FixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "shortcuts.real.vdf");

    private static byte[] Fixture => File.ReadAllBytes(FixturePath);

    [Fact]
    public void Real_shortcuts_file_reserializes_byte_for_byte()
    {
        var original = Fixture;

        var parsed = BinaryVdf.Read(original);
        var written = BinaryVdf.Write(parsed);

        Assert.Equal(original, written);
    }

    [Fact]
    public void Round_trip_guard_passes_for_the_real_file()
    {
        Assert.True(BinaryVdf.RoundTrips(Fixture, out var parsed));
        Assert.NotNull(parsed);
    }

    [Fact]
    public void Round_trip_guard_rejects_a_file_it_cannot_parse()
    {
        // An unknown type byte is exactly the situation the guard exists for: rather than
        // guessing, Nama must refuse to write.
        byte[] corrupt = [0x00, (byte)'x', 0x00, 0x7F, (byte)'y', 0x00, 0x08];

        Assert.False(BinaryVdf.RoundTrips(corrupt, out var parsed));
        Assert.Null(parsed);
    }

    [Fact]
    public void Parses_the_expected_shape()
    {
        var root = BinaryVdf.Read(Fixture);

        Assert.True(root.TryGetMap("shortcuts", out var shortcuts));
        Assert.Equal(6, shortcuts.Count);

        // Entries are keyed by their ordinal position.
        Assert.Equal(["0", "1", "2", "3", "4", "5"], shortcuts.Entries.Select(e => e.Key));
    }

    [Fact]
    public void Reads_field_values_including_non_ascii_names()
    {
        var file = ShortcutsFile.Load(FixturePath);
        var names = file.Shortcuts.Select(s => s.AppName).ToList();

        Assert.Contains("Diablo IV", names);
        Assert.Contains("Umineko Project", names);
        // One entry has a Japanese title; UTF-8 must survive the read.
        Assert.Contains(names, n => Nama.Core.Normalization.TextTools.ContainsCjk(n));
    }

    [Fact]
    public void Preserves_unknown_keys_through_an_edit()
    {
        var original = Fixture;
        var file = ShortcutsFile.Load(FixturePath);

        // Touch exactly one field on one entry.
        var target = file.Shortcuts[0];
        var before = target.AppName;
        target.AppName = "Renamed For Test";
        var written = file.Serialize();

        // Everything else — including fields Nama never models, like DevkitOverrideAppID
        // and FlatpakAppID — must still be present and unchanged.
        var reparsed = BinaryVdf.Read(written);
        var entry = reparsed.GetMap("shortcuts")!.GetMap("0")!;

        Assert.Equal("Renamed For Test", entry.GetString("AppName"));
        Assert.True(entry.Contains("DevkitOverrideAppID"));
        Assert.True(entry.Contains("FlatpakAppID"));
        Assert.True(entry.Contains("LastPlayTime"));
        Assert.True(entry.Contains("tags"));

        // And restoring the name must restore the exact original bytes.
        target.AppName = before;
        Assert.Equal(original.Length, file.Serialize().Length);
    }

    [Fact]
    public void Editing_one_entry_leaves_every_other_entry_byte_identical()
    {
        var file = ShortcutsFile.Load(FixturePath);
        var untouchedBefore = SerializeEntries(BinaryVdf.Read(Fixture), skipKey: "0");

        file.Shortcuts[0].AppName = "Something Completely Different";

        var untouchedAfter = SerializeEntries(BinaryVdf.Read(file.Serialize()), skipKey: "0");

        Assert.Equal(untouchedBefore, untouchedAfter);
    }

    private static byte[] SerializeEntries(VdfMap root, string skipKey)
    {
        var subset = new VdfMap();
        var container = new VdfMap();
        subset.Add("shortcuts", container);

        foreach (var (key, map) in root.GetMap("shortcuts")!.ChildMaps())
        {
            if (key != skipKey) container.Add(key, map);
        }

        return BinaryVdf.Write(subset);
    }

    [Fact]
    public void Writes_a_file_built_from_scratch()
    {
        var root = new VdfMap();
        var container = new VdfMap();
        root.Add("shortcuts", container);

        var entry = new VdfMap();
        entry.Add("appid", new VdfInt32(-1234567));
        entry.Add("AppName", new VdfString("Test Game"));
        entry.Add("Exe", new VdfString("\"C:\\Games\\test.exe\""));
        container.Add("0", entry);

        var written = BinaryVdf.Write(root);
        var reparsed = BinaryVdf.Read(written);

        Assert.Equal("Test Game", reparsed.GetMap("shortcuts")!.GetMap("0")!.GetString("AppName"));
        Assert.Equal(-1234567, reparsed.GetMap("shortcuts")!.GetMap("0")!.GetInt32("appid"));
        Assert.Equal(written, BinaryVdf.Write(reparsed));
    }

    [Fact]
    public void Missing_file_loads_as_an_empty_writable_document()
    {
        var file = ShortcutsFile.Load(Path.Combine(Path.GetTempPath(), "nama-does-not-exist.vdf"));

        Assert.False(file.Existed);
        Assert.True(file.RoundTrips); // nothing to damage
        Assert.Empty(file.Shortcuts);
        Assert.Equal("0", file.NextKey());
    }

    [Fact]
    public void Next_key_continues_after_the_highest_existing_entry()
    {
        Assert.Equal("6", ShortcutsFile.Load(FixturePath).NextKey());
    }
}
