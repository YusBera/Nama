using Nama.Core.Identification;
using Xunit;

namespace Nama.Tests;

/// <summary>
/// Covers hint gathering against a real temporary folder tree. These use actual files
/// because the whole point of the extractor is reading the file system correctly.
/// </summary>
public sealed class LocalMetadataExtractorTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "nama-extract-tests", Guid.NewGuid().ToString("N"));

    private readonly LocalMetadataExtractor _extractor = new();

    [Fact]
    public void Prefers_the_install_folder_over_a_boilerplate_product_name()
    {
        // Regression: copying a Windows binary into a game folder made the extractor
        // report "Microsoft® Windows® Operating System" as the game's name, because the
        // embedded ProductName resource outranked the folder.
        var system = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe");
        if (!File.Exists(system)) return;

        var folder = Path.Combine(_root, "Steins_Gate_0-v1.0-CODEX");
        Directory.CreateDirectory(folder);
        var exe = Path.Combine(folder, "SG0.exe");
        File.Copy(system, exe);

        var metadata = _extractor.Extract(exe);

        Assert.Equal("Steins_Gate_0-v1.0-CODEX", metadata.PrimaryRawName);
        Assert.DoesNotContain(metadata.Hints, h => h.Value.Contains("Operating System", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Uses_the_folder_name_when_the_executable_has_no_resources()
    {
        var folder = Path.Combine(_root, "ELDEN-RING-v1.12.2-FITGIRL");
        Directory.CreateDirectory(folder);
        var exe = Path.Combine(folder, "eldenring.exe");
        File.WriteAllBytes(exe, [0x4D, 0x5A, 0x90, 0x00]);

        var metadata = _extractor.Extract(exe);

        Assert.Equal("ELDEN-RING-v1.12.2-FITGIRL", metadata.PrimaryRawName);
        Assert.Equal(exe, metadata.Target.ExecutablePath);
    }

    [Fact]
    public void Walks_up_past_engine_folders_to_find_the_install_root()
    {
        // Unreal layout: <Game>/Binaries/Win64/<Game>-Win64-Shipping.exe
        var root = Path.Combine(_root, "Hollow Knight Silksong");
        var nested = Path.Combine(root, "Binaries", "Win64");
        Directory.CreateDirectory(nested);
        var exe = Path.Combine(nested, "Silksong-Win64-Shipping.exe");
        File.WriteAllBytes(exe, [0x4D, 0x5A]);

        var metadata = _extractor.Extract(exe);

        Assert.Equal(root, metadata.Target.InstallRoot);
        Assert.Equal("Hollow Knight Silksong", metadata.PrimaryRawName);
    }

    [Fact]
    public void Picks_up_a_unity_data_folder_as_a_hint()
    {
        var folder = Path.Combine(_root, "somefolder");
        Directory.CreateDirectory(Path.Combine(folder, "Hollow Knight_Data"));
        var exe = Path.Combine(folder, "game.exe");
        File.WriteAllBytes(exe, [0x4D, 0x5A]);

        var metadata = _extractor.Extract(exe);

        Assert.Contains(metadata.Hints, h =>
            h.Origin == NameHintOrigin.SiblingFile && h.Value == "Hollow Knight");
    }

    [Fact]
    public void Chooses_the_most_plausible_executable_in_a_folder()
    {
        var folder = Path.Combine(_root, "Celeste");
        Directory.CreateDirectory(folder);

        // Decoys that must lose to the real game binary.
        File.WriteAllBytes(Path.Combine(folder, "unins000.exe"), new byte[5000]);
        File.WriteAllBytes(Path.Combine(folder, "UnityCrashHandler64.exe"), new byte[5000]);
        File.WriteAllBytes(Path.Combine(folder, "vcredist_x64.exe"), new byte[9000]);
        File.WriteAllBytes(Path.Combine(folder, "Celeste.exe"), new byte[4000]);

        var metadata = _extractor.Extract(folder);

        Assert.Equal("Celeste.exe", Path.GetFileName(metadata.Target.ExecutablePath));
    }

    [Fact]
    public void Treats_a_supplied_folder_as_the_install_root()
    {
        var folder = Path.Combine(_root, "My Game");
        var nested = Path.Combine(folder, "bin");
        Directory.CreateDirectory(nested);
        File.WriteAllBytes(Path.Combine(nested, "MyGame.exe"), new byte[4000]);

        var metadata = _extractor.Extract(folder);

        Assert.Equal(folder, metadata.Target.InstallRoot);
    }

    [Fact]
    public void Ranks_a_generic_executable_name_below_the_folder()
    {
        var folder = Path.Combine(_root, "Doki Doki Literature Club");
        Directory.CreateDirectory(folder);
        var exe = Path.Combine(folder, "game.exe");
        File.WriteAllBytes(exe, [0x4D, 0x5A]);

        var metadata = _extractor.Extract(exe);

        var folderHint = metadata.Hints.First(h => h.Origin == NameHintOrigin.InstallRootFolder);
        var exeHint = metadata.Hints.First(h => h.Origin == NameHintOrigin.ExecutableFileName);

        Assert.True(folderHint.Weight > exeHint.Weight);
        Assert.Equal("Doki Doki Literature Club", metadata.PrimaryRawName);
    }

    [Fact]
    public void Reports_a_missing_path_clearly()
    {
        Assert.Throws<FileNotFoundException>(() => _extractor.Extract(Path.Combine(_root, "nope.exe")));
    }

    [Fact]
    public void Reports_a_folder_with_no_executable()
    {
        var folder = Path.Combine(_root, "Empty");
        Directory.CreateDirectory(folder);

        Assert.Throws<FileNotFoundException>(() => _extractor.Extract(folder));
    }

    [Fact]
    public void Rejects_blank_input()
    {
        Assert.ThrowsAny<ArgumentException>(() => _extractor.Extract("  "));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
