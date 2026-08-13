using Nama.Core.Identification;
using Nama.Core.Models;
using Nama.Providers.Local;
using Xunit;

namespace Nama.Tests;

public class PeIconReaderTests
{
    /// <summary>
    /// A real, always-present Windows executable with a modern multi-size icon.
    /// </summary>
    private static string SystemExecutable =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe");

    private static string ShellExecutable =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");

    [Fact]
    public void Extracts_an_icon_from_a_real_windows_executable()
    {
        var path = File.Exists(SystemExecutable) ? SystemExecutable : ShellExecutable;
        Assert.True(File.Exists(path), $"expected a system executable at {path}");

        Assert.True(PeIconReader.TryExtract(path, out var icon), $"no icon extracted from {path}");

        Assert.NotEmpty(icon.Data);
        Assert.True(icon.Width > 0 && icon.Height > 0);
        Assert.True(icon.Extension is ".png" or ".ico", $"unexpected extension {icon.Extension}");
    }

    [Fact]
    public void Prefers_the_largest_available_icon()
    {
        var path = File.Exists(SystemExecutable) ? SystemExecutable : ShellExecutable;

        Assert.True(PeIconReader.TryExtract(path, out var icon));

        // Windows system binaries ship a 256x256 frame; anything smaller means the
        // "largest wins" selection is not working.
        Assert.True(icon.Width >= 128, $"expected a large icon, got {icon.Width}x{icon.Height}");
    }

    [Fact]
    public void Produces_bytes_a_decoder_can_recognize()
    {
        var path = File.Exists(SystemExecutable) ? SystemExecutable : ShellExecutable;
        Assert.True(PeIconReader.TryExtract(path, out var icon));

        if (icon.Extension == ".png")
        {
            // PNG magic number.
            Assert.Equal(0x89, icon.Data[0]);
            Assert.Equal((byte)'P', icon.Data[1]);
            Assert.Equal((byte)'N', icon.Data[2]);
            Assert.Equal((byte)'G', icon.Data[3]);
        }
        else
        {
            // ICONDIR: reserved 0, type 1, exactly one image.
            Assert.Equal(0, BitConverter.ToUInt16(icon.Data, 0));
            Assert.Equal(1, BitConverter.ToUInt16(icon.Data, 2));
            Assert.Equal(1, BitConverter.ToUInt16(icon.Data, 4));

            // The declared payload must actually fit inside the container.
            var length = BitConverter.ToUInt32(icon.Data, 14);
            var offset = BitConverter.ToUInt32(icon.Data, 18);
            Assert.Equal(22u, offset);
            Assert.Equal((uint)icon.Data.Length, offset + length);
        }
    }

    [Fact]
    public void Rejects_a_file_that_is_not_a_pe_image()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nama-not-pe-{Guid.NewGuid():N}.exe");
        File.WriteAllText(path, "this is not an executable");

        try
        {
            Assert.False(PeIconReader.TryExtract(path, out _));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Rejects_a_truncated_pe_header()
    {
        // "MZ" followed by a bogus PE offset must not read out of bounds.
        Assert.False(PeIconReader.TryExtract(new byte[] { 0x4D, 0x5A, 0x00, 0x00 }, out _));

        var header = new byte[0x40];
        header[0] = 0x4D;
        header[1] = 0x5A;
        BitConverter.GetBytes(0x7FFF_FFFF).CopyTo(header, 0x3C);
        Assert.False(PeIconReader.TryExtract(header, out _));
    }

    [Fact]
    public void Rejects_a_missing_file()
    {
        Assert.False(PeIconReader.TryExtract(@"Z:\does\not\exist.exe", out _));
    }

    [Fact]
    public void Rejects_empty_input()
    {
        Assert.False(PeIconReader.TryExtract(ReadOnlySpan<byte>.Empty, out _));
    }

    [Fact]
    public void Never_throws_on_a_corrupted_executable()
    {
        // This parser runs against whatever executable the user points Nama at, so a
        // malformed or truncated file has to fail cleanly rather than take the app down.
        // The seed is fixed so a failure is always reproducible.
        var path = File.Exists(SystemExecutable) ? SystemExecutable : ShellExecutable;
        var original = File.ReadAllBytes(path);
        var random = new Random(20260813);

        for (var iteration = 0; iteration < 300; iteration++)
        {
            var mutated = (byte[])original.Clone();

            // Corrupt the headers, where all the offsets the parser trusts actually live.
            for (var i = 0; i < 24; i++)
                mutated[random.Next(Math.Min(4096, mutated.Length))] = (byte)random.Next(256);

            // Half the time, truncate as well.
            var candidate = random.Next(2) == 0
                ? mutated
                : mutated[..random.Next(1, mutated.Length)];

            var exception = Record.Exception(() => PeIconReader.TryExtract(candidate, out _));
            Assert.True(exception is null, $"iteration {iteration} threw {exception?.GetType().Name}: {exception?.Message}");
        }
    }
}

public sealed class ExecutableIconProviderTests : IDisposable
{
    private readonly string _staging =
        Path.Combine(Path.GetTempPath(), "nama-icon-tests", Guid.NewGuid().ToString("N"));

    private static string SystemExecutable =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe");

    [Fact]
    public async Task Yields_an_icon_artwork_pointing_at_a_staged_file()
    {
        var path = File.Exists(SystemExecutable)
            ? SystemExecutable
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");

        var provider = new ExecutableIconProvider(Target(path), _staging);

        var artwork = await provider.GetArtworkAsync(TestGame, [ArtworkType.Icon]);

        var item = Assert.Single(artwork);
        Assert.Equal(ArtworkType.Icon, item.Type);
        Assert.Equal("Game files", item.Source);

        // The URL must resolve to a file that actually exists, since the Steam writer
        // reads it back through the same path.
        var uri = new Uri(item.Url);
        Assert.True(uri.IsFile);
        Assert.True(File.Exists(uri.LocalPath));
        Assert.True(new FileInfo(uri.LocalPath).Length > 0);
    }

    [Fact]
    public async Task Returns_nothing_when_icons_were_not_requested()
    {
        var provider = new ExecutableIconProvider(Target(SystemExecutable), _staging);
        Assert.Empty(await provider.GetArtworkAsync(TestGame, [ArtworkType.Cover]));
    }

    [Fact]
    public async Task Returns_nothing_for_an_executable_without_an_icon()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nama-blank-{Guid.NewGuid():N}.exe");
        File.WriteAllText(path, "not a real executable");

        try
        {
            var provider = new ExecutableIconProvider(Target(path), _staging);
            Assert.Empty(await provider.GetArtworkAsync(TestGame, [ArtworkType.Icon]));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Only_advertises_icon_support()
    {
        var provider = new ExecutableIconProvider(Target(SystemExecutable), _staging);
        Assert.Equal([ArtworkType.Icon], provider.SupportedTypes);
    }

    private static LocalGameTarget Target(string exe) => new()
    {
        ExecutablePath = exe,
        StartDirectory = Path.GetDirectoryName(exe) ?? ".",
        InstallRoot = Path.GetDirectoryName(exe) ?? ".",
    };

    private static readonly Game TestGame = new() { CanonicalName = "Test Game" };

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_staging)) Directory.Delete(_staging, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
