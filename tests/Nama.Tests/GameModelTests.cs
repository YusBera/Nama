using Nama.Core.Models;
using Xunit;

namespace Nama.Tests;

public class GameModelTests
{
    [Fact]
    public void SourceFor_returns_null_when_the_provider_is_unknown()
    {
        // Regression: GameSourceId is a struct, so a FirstOrDefault-based lookup returns
        // a default value that is never null. That made every "does this provider know
        // the game?" check succeed and broke artwork resolution silently.
        var game = new Game
        {
            CanonicalName = "Elden Ring",
            SourceIds = [new GameSourceId("steam", "1245620")],
        };

        Assert.Null(game.SourceFor("vndb"));
        Assert.Null(game.SourceFor("steamgriddb"));
    }

    [Fact]
    public void SourceFor_finds_a_known_provider_regardless_of_casing()
    {
        var game = new Game
        {
            CanonicalName = "Elden Ring",
            SourceIds = [new GameSourceId("steam", "1245620")],
        };

        Assert.Equal("1245620", game.SourceFor("steam")!.Value.Id);
        Assert.Equal("1245620", game.SourceFor("STEAM")!.Value.Id);
    }

    [Fact]
    public void MergeWith_keeps_source_ids_from_both_providers()
    {
        var steam = new Game { CanonicalName = "Elden Ring", SourceIds = [new GameSourceId("steam", "1")] };
        var vndb = new Game { CanonicalName = "elden ring", SourceIds = [new GameSourceId("vndb", "2")] };

        var merged = steam.MergeWith(vndb);

        Assert.Equal(2, merged.SourceIds.Count);
        Assert.Equal("1", merged.SourceFor("steam")!.Value.Id);
        Assert.Equal("2", merged.SourceFor("vndb")!.Value.Id);
    }

    [Fact]
    public void MergeWith_does_not_duplicate_a_provider_already_present()
    {
        var first = new Game { CanonicalName = "Elden Ring", SourceIds = [new GameSourceId("steam", "1")] };
        var second = new Game { CanonicalName = "Elden Ring", SourceIds = [new GameSourceId("steam", "999")] };

        var merged = first.MergeWith(second);

        Assert.Single(merged.SourceIds);
        Assert.Equal("1", merged.SourceFor("steam")!.Value.Id);
    }

    [Fact]
    public void MergeWith_fills_gaps_and_collects_aliases()
    {
        var sparse = new Game { CanonicalName = "Elden Ring" };
        var rich = new Game
        {
            CanonicalName = "ELDEN RING",
            JapaneseName = "エルデンリング",
            Developer = "FromSoftware",
            ReleaseDate = new DateOnly(2022, 2, 25),
        };

        var merged = sparse.MergeWith(rich);

        Assert.Equal("Elden Ring", merged.CanonicalName);
        Assert.Equal("FromSoftware", merged.Developer);
        Assert.Equal("エルデンリング", merged.JapaneseName);
        Assert.Equal(2022, merged.ReleaseDate!.Value.Year);
        Assert.Contains("エルデンリング", merged.Aliases);
    }

    [Fact]
    public void AllTitles_includes_aliases_and_the_japanese_title()
    {
        var game = new Game
        {
            CanonicalName = "Subarashiki Hibi",
            JapaneseName = "素晴らしき日々",
            Aliases = ["Wonderful Everyday", "SubaHibi"],
        };

        var titles = game.AllTitles().ToList();

        Assert.Contains("Subarashiki Hibi", titles);
        Assert.Contains("素晴らしき日々", titles);
        Assert.Contains("Wonderful Everyday", titles);
    }

    [Fact]
    public void EffectiveDisplayName_falls_back_to_the_canonical_name()
    {
        Assert.Equal("Elden Ring", new Game { CanonicalName = "Elden Ring" }.EffectiveDisplayName);
    }

    [Theory]
    [InlineData(ArtworkType.Grid, 0)]
    [InlineData(ArtworkType.Cover, 0)]
    public void Artwork_aspect_ratio_is_zero_when_dimensions_are_unknown(ArtworkType type, int expected)
    {
        var artwork = new Artwork { Id = "x", Type = type, Url = "u", Source = "s" };
        Assert.Equal(expected, artwork.AspectRatio);
    }

    [Fact]
    public void Artwork_falls_back_to_the_full_image_when_no_thumbnail_exists()
    {
        var artwork = new Artwork
        {
            Id = "x",
            Type = ArtworkType.Cover,
            Url = "https://example.test/full.png",
            Source = "s",
        };

        Assert.Equal("https://example.test/full.png", artwork.PreviewUrl);
    }
}
