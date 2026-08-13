using Nama.Core.Aggregation;
using Nama.Core.Identification;
using Nama.Core.Models;
using Nama.Core.Providers;
using Xunit;

namespace Nama.Tests;

public class FuzzyMatcherTests
{
    [Fact]
    public void Identical_titles_score_one()
    {
        Assert.Equal(1.0, FuzzyMatcher.Similarity("Elden Ring", "Elden Ring"), 3);
    }

    [Fact]
    public void Formatting_differences_do_not_reduce_the_score()
    {
        Assert.Equal(1.0, FuzzyMatcher.Similarity("elden-ring", "ELDEN RING"), 3);
    }

    [Fact]
    public void A_sequel_stays_distinguishable_from_the_base_game()
    {
        var exact = FuzzyMatcher.Similarity("Elden Ring", "Elden Ring");
        var sequel = FuzzyMatcher.Similarity("Elden Ring", "Elden Ring Nightreign");

        // The sequel must still look related, but never as good as the exact match.
        Assert.True(sequel < exact);
        Assert.True(sequel > 0.5, $"expected some similarity, got {sequel}");
    }

    [Fact]
    public void Unrelated_titles_score_low()
    {
        Assert.True(FuzzyMatcher.Similarity("Elden Ring", "Stardew Valley") < 0.4);
    }

    [Fact]
    public void Word_order_does_not_matter_much()
    {
        Assert.True(FuzzyMatcher.TokenSetSimilarity("Zero Dawn Horizon", "Horizon Zero Dawn") > 0.9);
    }

    [Fact]
    public void Best_similarity_picks_the_strongest_alias()
    {
        var score = FuzzyMatcher.BestSimilarity("Subarashiki Hibi",
            ["Wonderful Everyday", "素晴らしき日々", "Subarashiki Hibi"]);

        Assert.Equal(1.0, score, 3);
    }

    [Fact]
    public void Empty_input_scores_zero()
    {
        Assert.Equal(0, FuzzyMatcher.Similarity("", "Elden Ring"));
        Assert.Equal(0, FuzzyMatcher.Similarity(null, null));
    }

    [Fact]
    public void Jaro_winkler_rewards_a_shared_prefix()
    {
        Assert.True(FuzzyMatcher.JaroWinkler("martha", "marhta") > 0.95);
    }
}

public class GameIdentifierTests
{
    [Fact]
    public async Task Ranks_the_correct_game_first_for_a_repack_name()
    {
        var provider = new StubGameProvider("stub",
        [
            Game("Elden Ring Nightreign"),
            Game("Elden Ring"),
            Game("Stardew Valley"),
        ]);

        var identifier = new GameIdentifier([provider]);
        var result = await identifier.IdentifyAsync(FakeLocal("ELDEN-RING-v1.12.2-FITGIRL"));

        Assert.Equal("Elden Ring", result.Candidates[0].CanonicalName);
        Assert.NotNull(result.BestMatch);
    }

    [Fact]
    public async Task Merges_the_same_game_reported_by_two_providers()
    {
        var steam = new StubGameProvider("steam", [Game("Elden Ring")]);
        var vndb = new StubGameProvider("vndb", [Game("elden ring")]);

        var identifier = new GameIdentifier([steam, vndb]);
        var result = await identifier.IdentifyAsync(FakeLocal("Elden Ring"));

        var top = result.Candidates[0];
        Assert.Equal(2, top.SourceIds.Count);
        Assert.NotNull(top.SourceFor("steam"));
        Assert.NotNull(top.SourceFor("vndb"));
    }

    [Fact]
    public async Task A_failing_provider_does_not_break_the_search()
    {
        var good = new StubGameProvider("good", [Game("Elden Ring")]);
        var bad = new ThrowingGameProvider();

        var identifier = new GameIdentifier([good, bad]);
        var result = await identifier.IdentifyAsync(FakeLocal("Elden Ring"));

        Assert.NotEmpty(result.Candidates);
        Assert.Single(result.Failures);
        Assert.Equal("Broken", result.Failures[0].Provider);
    }

    [Fact]
    public async Task Disabled_providers_are_skipped()
    {
        var disabled = new StubGameProvider("stub", [Game("Elden Ring")]) { IsEnabled = false };

        var identifier = new GameIdentifier([disabled]);
        var result = await identifier.IdentifyAsync(FakeLocal("Elden Ring"));

        Assert.Empty(result.Candidates);
    }

    [Fact]
    public async Task An_exact_match_is_confident_even_when_a_sequel_scores_close()
    {
        // Regression: "Steins Gate 0" matched STEINS;GATE 0 exactly, but STEINS;GATE
        // trailed by only three points, so the margin rule refused to preselect the
        // perfect match and cost the user an extra click.
        var provider = new StubGameProvider("stub",
        [
            Game("STEINS;GATE 0"),
            Game("STEINS;GATE"),
        ]);

        var identifier = new GameIdentifier([provider]);
        var result = await identifier.IdentifyAsync(FakeLocal("Steins_Gate_0-v1.0-CODEX"));

        Assert.Equal("STEINS;GATE 0", result.Candidates[0].CanonicalName);
        Assert.True(result.IsConfident,
            $"expected confidence, top={result.Candidates[0].Confidence:F3} next={result.Candidates[1].Confidence:F3}");
    }

    [Fact]
    public async Task An_ambiguous_result_is_not_reported_as_confident()
    {
        // The folder matches neither title outright, and both candidates are equally
        // plausible completions of it, so Nama must make the user choose.
        var provider = new StubGameProvider("stub",
        [
            Game("Hollow Knight"),
            Game("Hollow Knight Silksong"),
        ]);

        var identifier = new GameIdentifier([provider]);
        var result = await identifier.IdentifyAsync(FakeLocal("Hollow"));

        Assert.False(result.IsConfident);
    }

    [Fact]
    public async Task An_exact_match_still_wins_over_a_longer_similar_title()
    {
        // The counterpart to the ambiguous case: here the folder names one of them
        // exactly, so preselecting is the right call rather than a coin flip.
        var provider = new StubGameProvider("stub",
        [
            Game("Hollow Knight Silksong"),
            Game("Hollow Knight"),
        ]);

        var identifier = new GameIdentifier([provider]);
        var result = await identifier.IdentifyAsync(FakeLocal("Hollow Knight"));

        Assert.Equal("Hollow Knight", result.Candidates[0].CanonicalName);
        Assert.True(result.IsConfident);
    }

    private static Game Game(string name) => new()
    {
        CanonicalName = name,
        DisplayName = name,
    };

    private static LocalMetadata FakeLocal(string rawName) => new()
    {
        Target = new LocalGameTarget
        {
            ExecutablePath = $@"D:\Games\{rawName}\{rawName}.exe",
            StartDirectory = $@"D:\Games\{rawName}",
            InstallRoot = $@"D:\Games\{rawName}",
        },
        Hints = [new NameHint(rawName, NameHintOrigin.InstallRootFolder, 0.9)],
    };
}

public class ArtworkAggregatorTests
{
    [Fact]
    public async Task Groups_results_into_sections_by_type()
    {
        var provider = new StubArtworkProvider("A",
        [
            Art(ArtworkType.Cover, "c1", 600, 900, 10),
            Art(ArtworkType.Cover, "c2", 600, 900, 5),
            Art(ArtworkType.Grid, "g1", 460, 215, 8),
        ]);

        var collection = await new ArtworkAggregator([provider]).CollectAsync(TestGame);

        Assert.Equal(2, collection.Sections.Count);
        Assert.Equal(2, collection[ArtworkType.Cover]!.Items.Count);
        Assert.Single(collection[ArtworkType.Grid]!.Items);
    }

    [Fact]
    public async Task Shows_exactly_five_recommendations_and_flags_the_rest()
    {
        var items = Enumerable.Range(0, 12)
            .Select(i => Art(ArtworkType.Cover, $"c{i}", 600, 900, i))
            .ToArray();

        var collection = await new ArtworkAggregator([new StubArtworkProvider("A", items)]).CollectAsync(TestGame);

        var section = collection[ArtworkType.Cover]!;
        Assert.Equal(5, section.Recommended.Count);
        Assert.True(section.HasMore);
        Assert.Equal(12, section.Items.Count);
    }

    [Fact]
    public async Task Does_not_flag_more_when_five_or_fewer_results_exist()
    {
        var items = Enumerable.Range(0, 4)
            .Select(i => Art(ArtworkType.Cover, $"c{i}", 600, 900, i))
            .ToArray();

        var collection = await new ArtworkAggregator([new StubArtworkProvider("A", items)]).CollectAsync(TestGame);

        Assert.False(collection[ArtworkType.Cover]!.HasMore);
    }

    [Fact]
    public async Task Ranks_a_correctly_shaped_cover_above_a_badly_shaped_one()
    {
        var provider = new StubArtworkProvider("A",
        [
            Art(ArtworkType.Cover, "wrong-shape", 1920, 200, 100),
            Art(ArtworkType.Cover, "right-shape", 600, 900, 100),
        ]);

        var collection = await new ArtworkAggregator([provider]).CollectAsync(TestGame);

        Assert.Equal("right-shape", collection[ArtworkType.Cover]!.Items[0].Id);
    }

    [Fact]
    public async Task Ranks_higher_resolution_first_when_everything_else_matches()
    {
        var provider = new StubArtworkProvider("A",
        [
            Art(ArtworkType.Cover, "small", 600, 900, 50),
            Art(ArtworkType.Cover, "large", 1200, 1800, 50),
        ]);

        var collection = await new ArtworkAggregator([provider]).CollectAsync(TestGame);

        Assert.Equal("large", collection[ArtworkType.Cover]!.Items[0].Id);
    }

    [Fact]
    public async Task Normalizes_scores_so_one_providers_scale_cannot_dominate()
    {
        // SteamGridDB reports upvote counts in the hundreds while Steam reports a 0-1
        // confidence. Without per-provider normalization the raw numbers would decide
        // the ordering on their own, regardless of fit.
        var upvotes = new StubArtworkProvider("Votes",
        [
            Art(ArtworkType.Cover, "votes-bad-shape", 1920, 200, 500),
            Art(ArtworkType.Cover, "votes-good-shape", 600, 900, 480),
        ]);
        var fraction = new StubArtworkProvider("Fraction", [Art(ArtworkType.Cover, "fraction", 600, 900, 0.7)]);

        var collection = await new ArtworkAggregator([upvotes, fraction]).CollectAsync(TestGame);
        var items = collection[ArtworkType.Cover]!.Items;

        // The badly-shaped image loses despite having the single highest raw score.
        Assert.NotEqual("votes-bad-shape", items[0].Id);
    }

    [Fact]
    public async Task Ranks_animated_artwork_below_stills()
    {
        var provider = new StubArtworkProvider("A",
        [
            Art(ArtworkType.Grid, "animated", 460, 215, 100, animated: true),
            Art(ArtworkType.Grid, "still", 460, 215, 90),
        ]);

        var collection = await new ArtworkAggregator([provider]).CollectAsync(TestGame);

        Assert.Equal("still", collection[ArtworkType.Grid]!.Items[0].Id);
    }

    [Fact]
    public async Task Combines_artwork_from_several_providers_into_one_section()
    {
        var a = new StubArtworkProvider("A", [Art(ArtworkType.Cover, "a1", 600, 900, 5)]);
        var b = new StubArtworkProvider("B", [Art(ArtworkType.Cover, "b1", 600, 900, 5)]);

        var collection = await new ArtworkAggregator([a, b]).CollectAsync(TestGame);

        Assert.Equal(2, collection[ArtworkType.Cover]!.Items.Count);
    }

    [Fact]
    public async Task Removes_the_same_image_arriving_from_two_providers()
    {
        var a = new StubArtworkProvider("A", [Art(ArtworkType.Cover, "a1", 600, 900, 5, "https://same/x.png")]);
        var b = new StubArtworkProvider("B", [Art(ArtworkType.Cover, "b1", 600, 900, 5, "https://same/x.png")]);

        var collection = await new ArtworkAggregator([a, b]).CollectAsync(TestGame);

        Assert.Single(collection[ArtworkType.Cover]!.Items);
    }

    [Fact]
    public async Task A_failing_provider_is_reported_but_others_still_contribute()
    {
        var good = new StubArtworkProvider("Good", [Art(ArtworkType.Cover, "c1", 600, 900, 5)]);
        var bad = new ThrowingArtworkProvider();

        var collection = await new ArtworkAggregator([good, bad]).CollectAsync(TestGame);

        Assert.Single(collection[ArtworkType.Cover]!.Items);
        Assert.Single(collection.Failures);
    }

    [Fact]
    public async Task No_artwork_yields_an_empty_collection_rather_than_an_error()
    {
        var collection = await new ArtworkAggregator([new StubArtworkProvider("A", [])]).CollectAsync(TestGame);
        Assert.True(collection.IsEmpty);
    }

    private static readonly Game TestGame = new()
    {
        CanonicalName = "Elden Ring",
        SourceIds = [new GameSourceId("stub", "1")],
    };

    private static Artwork Art(
        ArtworkType type, string id, int w, int h, double score, string? url = null, bool animated = false) => new()
    {
        Id = id,
        Type = type,
        Url = url ?? $"https://example.test/{id}.png",
        Source = "Test",
        Width = w,
        Height = h,
        Score = score,
        IsAnimated = animated,
    };
}

// --- Stubs -------------------------------------------------------------------

file sealed class StubGameProvider(string id, Game[] games) : IGameProvider
{
    public string Id { get; } = id;
    public string DisplayName => Id;
    public bool IsEnabled { get; set; } = true;
    public int Priority => 10;

    public Task<IReadOnlyList<Game>> SearchAsync(string query, CancellationToken ct = default)
    {
        // Re-stamp each result with this provider's source id, as a real provider would.
        IReadOnlyList<Game> results = games.Select(g => new Game
        {
            CanonicalName = g.CanonicalName,
            DisplayName = g.DisplayName,
            SourceIds = [new GameSourceId(Id, g.CanonicalName)],
        }).ToList();

        return Task.FromResult(results);
    }
}

file sealed class ThrowingGameProvider : IGameProvider
{
    public string Id => "broken";
    public string DisplayName => "Broken";
    public bool IsEnabled => true;
    public int Priority => 99;

    public Task<IReadOnlyList<Game>> SearchAsync(string query, CancellationToken ct = default) =>
        throw new HttpRequestException("boom");
}

file sealed class StubArtworkProvider(string name, Artwork[] artwork) : IArtworkProvider
{
    public string Id { get; } = name;
    public string DisplayName { get; } = name;
    public bool IsEnabled => true;
    public int Priority => 10;

    public IReadOnlyCollection<ArtworkType> SupportedTypes { get; } =
        [ArtworkType.Cover, ArtworkType.Grid, ArtworkType.Hero, ArtworkType.Logo, ArtworkType.Icon];

    public Task<IReadOnlyList<Artwork>> GetArtworkAsync(
        Game game,
        IReadOnlyCollection<ArtworkType> types,
        CancellationToken ct = default)
    {
        IReadOnlyList<Artwork> results = artwork
            .Where(a => types.Contains(a.Type))
            .Select(a => new Artwork
            {
                Id = a.Id,
                Type = a.Type,
                Url = a.Url,
                Source = DisplayName,
                Width = a.Width,
                Height = a.Height,
                Score = a.Score,
                IsAnimated = a.IsAnimated,
            })
            .ToList();

        return Task.FromResult(results);
    }
}

file sealed class ThrowingArtworkProvider : IArtworkProvider
{
    public string Id => "broken";
    public string DisplayName => "Broken";
    public bool IsEnabled => true;
    public int Priority => 99;

    public IReadOnlyCollection<ArtworkType> SupportedTypes { get; } = [ArtworkType.Cover];

    public Task<IReadOnlyList<Artwork>> GetArtworkAsync(
        Game game,
        IReadOnlyCollection<ArtworkType> types,
        CancellationToken ct = default) =>
        throw new HttpRequestException("boom");
}
