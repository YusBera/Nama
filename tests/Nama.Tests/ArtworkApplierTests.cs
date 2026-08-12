using Nama.Core.Abstractions;
using Nama.Core.Models;
using Nama.Steam.Models;
using Nama.Steam.Writing;

namespace Nama.Tests;

public class ImageFormatTests
{
    [Fact]
    public void Detects_formats_from_their_leading_bytes()
    {
        Assert.Equal(".png", ImageFormat.Detect([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0]));
        Assert.Equal(".jpg", ImageFormat.Detect([0xFF, 0xD8, 0xFF, 0xE0, 0, 0, 0, 0, 0, 0, 0, 0]));
        Assert.Equal(".gif", ImageFormat.Detect("GIF89a......"u8));
        Assert.Equal(".webp", ImageFormat.Detect("RIFF____WEBP"u8));
        Assert.Equal(".bmp", ImageFormat.Detect("BM__________"u8));
    }

    [Fact]
    public void Rejects_content_that_is_not_an_image()
    {
        Assert.Null(ImageFormat.Detect("<!DOCTYPE html><html>"u8));
        Assert.Null(ImageFormat.Detect([1, 2, 3]));
    }

    [Fact]
    public void Knows_which_formats_steam_can_render()
    {
        Assert.True(ImageFormat.IsSteamCompatible(".png"));
        Assert.True(ImageFormat.IsSteamCompatible(".JPG"));
        Assert.False(ImageFormat.IsSteamCompatible(".gif"));
        Assert.False(ImageFormat.IsSteamCompatible(".ico"));
    }
}

public class LocalImageDownloaderTests
{
    [Fact]
    public async Task Reads_and_sniffs_a_local_file_uri_without_http()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nama-local-art-{Guid.NewGuid():N}.png");
        var bytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0 };
        await File.WriteAllBytesAsync(path, bytes);

        try
        {
            using var client = new HttpClient(new StubHandler(_ =>
                throw new InvalidOperationException("Local artwork must not use HTTP.")));
            var result = await new Nama.Providers.HttpImageDownloader(client)
                .DownloadAsync(new Uri(path).AbsoluteUri);

            Assert.NotNull(result);
            Assert.Equal(".png", result.Extension);
            Assert.Equal(bytes, result.Bytes);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public class ArtworkApplierTests : IDisposable
{
    private readonly string _userData = Path.Combine(Path.GetTempPath(), $"nama-art-{Guid.NewGuid():N}");

    private readonly SteamAccount _account;

    private const uint AppId = 4066054353;

    public ArtworkApplierTests()
    {
        _account = new SteamAccount { AccountId = 1, UserDataPath = _userData };
        Directory.CreateDirectory(_account.GridPath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_userData)) Directory.Delete(_userData, recursive: true);
    }

    private static readonly byte[] PngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];

    private static readonly byte[] JpgBytes = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4, 5, 6, 7, 8];

    private sealed class FakeDownloader(byte[]? bytes, string extension = ".png") : IImageDownloader
    {
        public Task<DownloadedImage?> DownloadAsync(string url, CancellationToken ct = default) =>
            Task.FromResult(bytes is null ? null : new DownloadedImage(bytes, extension));
    }

    private static Artwork Art(ArtworkType type) => new()
    {
        Id = type.ToString(),
        Type = type,
        Url = $"https://example.test/{type}",
        Source = "test",
        Width = 600,
        Height = 900,
    };

    [Theory]
    [InlineData(ArtworkType.Grid, "4066054353")]
    [InlineData(ArtworkType.Cover, "4066054353p")]
    [InlineData(ArtworkType.Hero, "4066054353_hero")]
    [InlineData(ArtworkType.Logo, "4066054353_logo")]
    public void Grid_stems_follow_steams_naming(ArtworkType type, string expected)
    {
        Assert.Equal(expected, ArtworkApplier.GridStem(type, AppId));
    }

    [Fact]
    public void The_icon_is_not_a_grid_file()
    {
        // Steam stores a path to the icon in the shortcut and reads that file directly.
        Assert.Null(ArtworkApplier.GridStem(ArtworkType.Icon, AppId));
    }

    [Fact]
    public async Task Applies_artwork_to_the_grid_folder()
    {
        var applier = new ArtworkApplier(new FakeDownloader(PngBytes));
        var selections = new Dictionary<ArtworkType, Artwork> { [ArtworkType.Cover] = Art(ArtworkType.Cover) };

        var report = applier.Apply(_account, AppId, selections, await applier.FetchAsync(selections), dryRun: false);

        var written = Path.Combine(_account.GridPath, "4066054353p.png");
        Assert.True(File.Exists(written));
        Assert.Equal(written, report.Applied[ArtworkType.Cover]);
    }

    [Fact]
    public async Task Replacing_a_cover_removes_the_old_file_of_a_different_extension()
    {
        // Confirmed against a real library: extensions vary per slot, and Steam will keep
        // showing a stale leftover. Without this, replacing artwork appears to do nothing.
        var stale = Path.Combine(_account.GridPath, "4066054353p.jpg");
        File.WriteAllBytes(stale, JpgBytes);

        var applier = new ArtworkApplier(new FakeDownloader(PngBytes));
        var selections = new Dictionary<ArtworkType, Artwork> { [ArtworkType.Cover] = Art(ArtworkType.Cover) };

        applier.Apply(_account, AppId, selections, await applier.FetchAsync(selections), dryRun: false);

        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(Path.Combine(_account.GridPath, "4066054353p.png")));
    }

    [Fact]
    public async Task Steams_own_json_sidecars_are_left_alone()
    {
        // A real library has {appid}.json next to the artwork. It is Steam's, not ours.
        var sidecar = Path.Combine(_account.GridPath, "4066054353.json");
        File.WriteAllText(sidecar, "{}");

        var applier = new ArtworkApplier(new FakeDownloader(PngBytes));
        var selections = new Dictionary<ArtworkType, Artwork> { [ArtworkType.Grid] = Art(ArtworkType.Grid) };

        applier.Apply(_account, AppId, selections, await applier.FetchAsync(selections), dryRun: false);

        Assert.True(File.Exists(sidecar));
    }

    [Fact]
    public async Task Another_games_artwork_is_never_touched()
    {
        var other = Path.Combine(_account.GridPath, "1234567890p.png");
        File.WriteAllBytes(other, PngBytes);

        var applier = new ArtworkApplier(new FakeDownloader(PngBytes));
        var selections = new Dictionary<ArtworkType, Artwork> { [ArtworkType.Cover] = Art(ArtworkType.Cover) };

        applier.Apply(_account, AppId, selections, await applier.FetchAsync(selections), dryRun: false);

        Assert.True(File.Exists(other));
    }

    [Fact]
    public async Task A_dry_run_writes_nothing_but_still_reports_the_paths()
    {
        var applier = new ArtworkApplier(new FakeDownloader(PngBytes));
        var selections = new Dictionary<ArtworkType, Artwork> { [ArtworkType.Cover] = Art(ArtworkType.Cover) };

        var report = applier.Apply(_account, AppId, selections, await applier.FetchAsync(selections), dryRun: true);

        Assert.NotEmpty(report.Applied);
        Assert.Empty(Directory.GetFiles(_account.GridPath));
    }

    [Fact]
    public async Task A_failed_download_is_reported_rather_than_thrown()
    {
        var applier = new ArtworkApplier(new FakeDownloader(null));
        var selections = new Dictionary<ArtworkType, Artwork> { [ArtworkType.Cover] = Art(ArtworkType.Cover) };

        var report = applier.Apply(_account, AppId, selections, await applier.FetchAsync(selections), dryRun: false);

        Assert.Empty(report.Applied);
        Assert.Contains(ArtworkType.Cover, report.Failed.Keys);
    }

    [Fact]
    public async Task One_slot_failing_does_not_prevent_the_others()
    {
        var applier = new ArtworkApplier(new FakeDownloader(PngBytes));
        var selections = new Dictionary<ArtworkType, Artwork>
        {
            [ArtworkType.Cover] = Art(ArtworkType.Cover),
            [ArtworkType.Background] = Art(ArtworkType.Background), // no Steam slot exists
        };

        var report = applier.Apply(_account, AppId, selections, await applier.FetchAsync(selections), dryRun: false);

        Assert.Contains(ArtworkType.Cover, report.Applied.Keys);
        Assert.Contains(ArtworkType.Background, report.Failed.Keys);
    }

    [Fact]
    public async Task An_unsupported_format_is_written_as_png_rather_than_a_file_steam_ignores()
    {
        var gif = new byte[] { (byte)'G', (byte)'I', (byte)'F', (byte)'8', 1, 2, 3, 4, 5, 6, 7, 8 };
        var applier = new ArtworkApplier(new FakeDownloader(gif, ".gif"));
        var selections = new Dictionary<ArtworkType, Artwork> { [ArtworkType.Cover] = Art(ArtworkType.Cover) };

        var report = applier.Apply(_account, AppId, selections, await applier.FetchAsync(selections), dryRun: false);

        Assert.EndsWith(".png", report.Applied[ArtworkType.Cover]);
    }

    [Fact]
    public async Task The_icon_goes_to_namas_own_folder_not_the_grid_folder()
    {
        var applier = new ArtworkApplier(new FakeDownloader(PngBytes));
        var selections = new Dictionary<ArtworkType, Artwork> { [ArtworkType.Icon] = Art(ArtworkType.Icon) };

        var report = applier.Apply(_account, AppId, selections, await applier.FetchAsync(selections), dryRun: true);

        Assert.NotNull(report.IconPath);
        Assert.StartsWith(ArtworkApplier.IconDirectory, report.IconPath);
        Assert.DoesNotContain(_account.GridPath, report.IconPath);
    }
}
