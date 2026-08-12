using Nama.Core.Aggregation;
using Nama.Core.Models;

namespace Nama.Tests;

public class ArtworkRankerTests
{
    private static Artwork Art(
        int width, int height, double? score = null, int? votes = null,
        bool animated = false, string id = "x", ArtworkType type = ArtworkType.Grid) => new()
    {
        Id = id,
        Type = type,
        Url = $"https://example.test/{id}",
        Source = "test",
        Width = width,
        Height = height,
        Score = score,
        Votes = votes,
        IsAnimated = animated,
    };

    [Fact]
    public void Prefers_artwork_shaped_for_the_target_slot()
    {
        // A portrait cover in the wide Grid slot gets letterboxed by Steam and looks
        // broken, so shape has to beat a better rating.
        var wide = Art(920, 430, score: 0.5, id: "wide");
        var portrait = Art(600, 900, score: 1.0, id: "portrait");

        var ranked = ArtworkRanker.Rank([portrait, wide], ArtworkType.Grid);

        Assert.Equal("wide", ranked[0].Id);
    }

    [Fact]
    public void Prefers_portrait_for_the_cover_slot()
    {
        var wide = Art(920, 430, score: 1.0, id: "wide");
        var portrait = Art(600, 900, score: 0.5, id: "portrait");

        var ranked = ArtworkRanker.Rank([wide, portrait], ArtworkType.Cover);

        Assert.Equal("portrait", ranked[0].Id);
    }

    [Fact]
    public void Prefers_higher_resolution_when_shape_and_rating_match()
    {
        var small = Art(460, 215, score: 0.8, id: "small");
        var large = Art(920, 430, score: 0.8, id: "large");

        Assert.Equal("large", ArtworkRanker.Rank([small, large], ArtworkType.Grid)[0].Id);
    }

    [Fact]
    public void Prefers_better_rated_artwork_when_everything_else_matches()
    {
        var poor = Art(920, 430, score: 0.2, id: "poor");
        var good = Art(920, 430, score: 0.9, id: "good");

        Assert.Equal("good", ArtworkRanker.Rank([poor, good], ArtworkType.Grid)[0].Id);
    }

    [Fact]
    public void Popularity_breaks_ties_but_does_not_dominate()
    {
        var unpopular = Art(920, 430, score: 0.8, votes: 0, id: "unpopular");
        var popular = Art(920, 430, score: 0.8, votes: 500, id: "popular");

        Assert.Equal("popular", ArtworkRanker.Rank([unpopular, popular], ArtworkType.Grid)[0].Id);
    }

    [Fact]
    public void Popularity_is_compressed_so_huge_vote_counts_do_not_run_away()
    {
        var many = ArtworkRanker.Score(Art(920, 430, score: 0.5, votes: 10_000), ArtworkType.Grid);
        var some = ArtworkRanker.Score(Art(920, 430, score: 0.5, votes: 100), ArtworkType.Grid);
        var few = ArtworkRanker.Score(Art(920, 430, score: 0.5, votes: 1), ArtworkType.Grid);

        // 1 -> 100 should matter more than 100 -> 10000.
        Assert.True(some - few > many - some);
    }

    [Fact]
    public void Animated_artwork_ranks_below_an_equivalent_still()
    {
        var still = Art(920, 430, score: 0.8, id: "still");
        var animated = Art(920, 430, score: 0.8, animated: true, id: "animated");

        Assert.Equal("still", ArtworkRanker.Rank([animated, still], ArtworkType.Grid)[0].Id);
    }

    [Fact]
    public void Unknown_dimensions_are_not_treated_as_a_fault()
    {
        var unknown = ArtworkRanker.Score(Art(0, 0, score: 0.8), ArtworkType.Grid);
        var terrible = ArtworkRanker.Score(Art(100, 2000, score: 0.8), ArtworkType.Grid);

        Assert.True(unknown > terrible);
    }

    [Fact]
    public void Recommended_returns_at_most_five_by_default()
    {
        var artwork = Enumerable.Range(0, 20).Select(i => Art(920, 430, score: i / 20.0, id: $"a{i}")).ToList();

        Assert.Equal(5, ArtworkRanker.Recommended(artwork, ArtworkType.Grid).Count);
    }

    [Fact]
    public void Recommended_returns_everything_when_there_are_fewer_than_five()
    {
        var artwork = new[] { Art(920, 430, id: "a"), Art(920, 430, id: "b") };

        Assert.Equal(2, ArtworkRanker.Recommended(artwork, ArtworkType.Grid).Count);
    }

    [Fact]
    public void Scores_stay_within_range()
    {
        foreach (var type in ArtworkCollection.DisplayOrder)
        {
            foreach (var art in new[] { Art(1, 1), Art(4000, 10, score: 1.0, votes: 99999), Art(0, 0) })
            {
                var score = ArtworkRanker.Score(art, type);
                Assert.InRange(score, 0.0, 1.0);
            }
        }
    }
}

public class ArtworkCollectionTests
{
    private static Artwork Art(ArtworkType type) => new()
    {
        Id = type.ToString(),
        Type = type,
        Url = "https://example.test/x",
        Source = "test",
        Width = 100,
        Height = 100,
    };

    [Fact]
    public void Available_types_only_lists_slots_that_have_artwork()
    {
        var collection = new ArtworkCollection
        {
            All = [Art(ArtworkType.Cover), Art(ArtworkType.Logo)],
            FailedProviders = [],
            SkippedProviders = [],
        };

        Assert.Equal([ArtworkType.Cover, ArtworkType.Logo], collection.AvailableTypes);
    }

    [Fact]
    public void Available_types_follow_the_fixed_display_order()
    {
        var collection = new ArtworkCollection
        {
            All = [Art(ArtworkType.Icon), Art(ArtworkType.Cover), Art(ArtworkType.Hero)],
            FailedProviders = [],
            SkippedProviders = [],
        };

        Assert.Equal([ArtworkType.Cover, ArtworkType.Hero, ArtworkType.Icon], collection.AvailableTypes);
    }
}
