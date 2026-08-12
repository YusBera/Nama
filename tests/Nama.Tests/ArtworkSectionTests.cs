using Nama.App.Services;
using Nama.App.ViewModels;
using Nama.App.WindowsIntegration;
using Nama.Core.Abstractions;
using Nama.Core.Models;

namespace Nama.Tests;

/// <summary>
/// The artwork picker's "five recommended, expand for the rest" behaviour. Covered here
/// because it cannot yet be exercised against a live provider — no SteamGridDB key exists
/// on this machine, and no other source returns more than five images for one slot.
/// </summary>
public class ArtworkSectionTests
{
    private sealed class NoDownloader : IImageDownloader
    {
        public Task<DownloadedImage?> DownloadAsync(string url, CancellationToken ct = default) =>
            Task.FromResult<DownloadedImage?>(null);
    }

    private static ThumbnailLoader Loader() =>
        new(new NoDownloader(), Path.Combine(Path.GetTempPath(), $"nama-thumbs-{Guid.NewGuid():N}"));

    private static List<Artwork> Artworks(int count, ArtworkType type = ArtworkType.Grid) =>
        Enumerable.Range(0, count).Select(i => new Artwork
        {
            Id = $"a{i}",
            Type = type,
            Url = $"https://example.test/{i}",
            Source = "test",
            Width = 920,
            Height = 430,
            // Descending score so ranking order is predictable.
            Score = 1.0 - (i * 0.01),
        }).ToList();

    [Fact]
    public void Shows_exactly_five_recommended_by_default()
    {
        var section = new ArtworkSectionViewModel(ArtworkType.Grid, Artworks(20), Loader());

        Assert.Equal(5, section.Tiles.Count);
        Assert.Equal(20, section.TotalCount);
        Assert.False(section.IsExpanded);
    }

    [Fact]
    public void Shows_everything_when_there_are_fewer_than_five()
    {
        var section = new ArtworkSectionViewModel(ArtworkType.Grid, Artworks(3), Loader());

        Assert.Equal(3, section.Tiles.Count);
        Assert.False(section.CanExpand);
    }

    [Fact]
    public void Expanding_reveals_the_rest_in_place()
    {
        var section = new ArtworkSectionViewModel(ArtworkType.Grid, Artworks(20), Loader());

        Assert.True(section.CanExpand);

        section.ToggleExpandCommand.Execute(null);

        Assert.True(section.IsExpanded);
        Assert.Equal(20, section.Tiles.Count);
    }

    [Fact]
    public void Collapsing_returns_to_the_five_recommended()
    {
        var section = new ArtworkSectionViewModel(ArtworkType.Grid, Artworks(20), Loader());

        section.ToggleExpandCommand.Execute(null);
        section.ToggleExpandCommand.Execute(null);

        Assert.Equal(5, section.Tiles.Count);
    }

    [Fact]
    public void The_expand_label_says_how_many_more_there_are()
    {
        var section = new ArtworkSectionViewModel(ArtworkType.Grid, Artworks(20), Loader());

        Assert.Contains("15 more", section.ExpandLabel);

        section.ToggleExpandCommand.Execute(null);

        Assert.Contains("Show less", section.ExpandLabel);
    }

    [Fact]
    public void The_first_five_are_the_highest_ranked()
    {
        var section = new ArtworkSectionViewModel(ArtworkType.Grid, Artworks(20), Loader());

        Assert.Equal(["a0", "a1", "a2", "a3", "a4"], section.Tiles.Select(t => t.Artwork.Id));
    }

    [Fact]
    public void Selecting_a_tile_deselects_the_previous_one()
    {
        var section = new ArtworkSectionViewModel(ArtworkType.Grid, Artworks(20), Loader());

        section.SelectCommand.Execute(section.Tiles[0]);
        section.SelectCommand.Execute(section.Tiles[1]);

        Assert.False(section.Tiles[0].IsSelected);
        Assert.True(section.Tiles[1].IsSelected);
        Assert.Same(section.Tiles[1], section.Selected);
    }

    [Fact]
    public void Clicking_the_selected_tile_again_clears_the_slot()
    {
        // Nothing is written for a slot with no selection, so this has to be reachable.
        var section = new ArtworkSectionViewModel(ArtworkType.Grid, Artworks(20), Loader());

        section.SelectCommand.Execute(section.Tiles[0]);
        section.SelectCommand.Execute(section.Tiles[0]);

        Assert.Null(section.Selected);
        Assert.False(section.Tiles[0].IsSelected);
    }

    [Fact]
    public void A_selection_survives_expanding_and_collapsing()
    {
        var section = new ArtworkSectionViewModel(ArtworkType.Grid, Artworks(20), Loader());

        section.SelectCommand.Execute(section.Tiles[2]);
        var chosen = section.Selected;

        section.ToggleExpandCommand.Execute(null);
        section.ToggleExpandCommand.Execute(null);

        Assert.Same(chosen, section.Selected);
        Assert.True(section.Selected!.IsSelected);
    }

    [Fact]
    public void Selecting_a_tile_only_visible_when_expanded_still_works()
    {
        var section = new ArtworkSectionViewModel(ArtworkType.Grid, Artworks(20), Loader());

        section.ToggleExpandCommand.Execute(null);
        section.SelectCommand.Execute(section.Tiles[12]);
        section.ToggleExpandCommand.Execute(null);

        // The tile is no longer shown, but it is still what will be written.
        Assert.NotNull(section.Selected);
        Assert.Equal("a12", section.Selected.Artwork.Id);
    }

    [Theory]
    [InlineData(ArtworkType.Grid, "BANNER")]
    [InlineData(ArtworkType.Cover, "COVER")]
    [InlineData(ArtworkType.Hero, "HERO")]
    [InlineData(ArtworkType.Logo, "LOGO")]
    [InlineData(ArtworkType.Icon, "ICON")]
    public void Headers_use_the_names_from_the_spec(ArtworkType type, string expected)
    {
        Assert.Equal(expected, new ArtworkSectionViewModel(type, Artworks(1, type), Loader()).Header);
    }

    [Fact]
    public void Tile_shape_follows_the_slot()
    {
        var cover = new ArtworkSectionViewModel(ArtworkType.Cover, Artworks(1, ArtworkType.Cover), Loader());
        var banner = new ArtworkSectionViewModel(ArtworkType.Grid, Artworks(1), Loader());
        var icon = new ArtworkSectionViewModel(ArtworkType.Icon, Artworks(1, ArtworkType.Icon), Loader());

        Assert.True(cover.TileHeight > cover.TileWidth, "covers are portrait");
        Assert.True(banner.TileWidth > banner.TileHeight, "banners are landscape");
        Assert.Equal(icon.TileWidth, icon.TileHeight);
    }

    [Fact]
    public void An_empty_section_still_exists_as_a_local_import_target()
    {
        var section = new ArtworkSectionViewModel(ArtworkType.Cover, [], Loader());

        Assert.Empty(section.Tiles);
        Assert.Equal(0, section.TotalCount);
        Assert.NotNull(section.ImportLocalCommand);
    }
}

public class ContextMenuInstallerTests
{
    [Fact]
    public void Resolves_a_real_executable_path()
    {
        // Must not hand Explorer a path to dotnet.exe or an unlaunchable dll.
        var path = ContextMenuInstaller.ExecutablePath;

        Assert.False(string.IsNullOrWhiteSpace(path));
        Assert.EndsWith(".exe", path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Reports_installation_state_without_throwing()
    {
        // Read-only: this must never modify the developer's registry as a side effect.
        _ = ContextMenuInstaller.IsInstalled();
    }
}
