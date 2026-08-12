using Nama.Core.Identification;
using Nama.Core.Models;

namespace Nama.Tests;

public class FuzzyMatcherTests
{
    [Theory]
    [InlineData("Elden Ring", "ELDEN RING")]
    [InlineData("Steins Gate", "Steins;Gate")]
    [InlineData("White Album 2", "WHITE ALBUM2")]
    [InlineData("Nier Automata", "NieR:Automata")]
    public void Punctuation_and_case_differences_are_exact_matches(string a, string b)
    {
        Assert.Equal(1.0, FuzzyMatcher.Similarity(a, b));
    }

    [Fact]
    public void A_run_together_name_still_matches_its_spaced_form()
    {
        // Jaro-Winkler carries this; token overlap gives nothing.
        Assert.True(FuzzyMatcher.Similarity("eldenring", "Elden Ring") > 0.85);
    }

    [Fact]
    public void Reordered_words_still_match()
    {
        // Token overlap carries this; Jaro-Winkler is confused by it.
        Assert.True(FuzzyMatcher.Similarity("Hibi Subarashiki", "Subarashiki Hibi") > 0.85);
    }

    [Fact]
    public void A_containing_title_scores_high_but_not_perfect()
    {
        var score = FuzzyMatcher.Similarity("Elden Ring", "ELDEN RING Shadow of the Erdtree");

        Assert.InRange(score, 0.70, 0.95);
    }

    [Fact]
    public void The_exact_title_beats_a_longer_one_containing_it()
    {
        var exact = FuzzyMatcher.Similarity("Elden Ring", "ELDEN RING");
        var extended = FuzzyMatcher.Similarity("Elden Ring", "ELDEN RING NIGHTREIGN");

        Assert.True(exact > extended);
    }

    [Fact]
    public void Unrelated_titles_score_low()
    {
        Assert.True(FuzzyMatcher.Similarity("Elden Ring", "Football Manager 2024") < 0.5);
        Assert.True(FuzzyMatcher.Similarity("Subarashiki Hibi", "Diablo IV") < 0.5);
    }

    [Fact]
    public void Best_similarity_picks_the_strongest_alias()
    {
        var score = FuzzyMatcher.BestSimilarity(
            "素晴らしき日々",
            ["Subarashiki Hibi", "Wonderful Everyday", "素晴らしき日々～不連続存在～"]);

        Assert.True(score > 0.8);
    }

    [Fact]
    public void Empty_input_scores_zero_and_does_not_throw()
    {
        Assert.Equal(0.0, FuzzyMatcher.Similarity("", "Elden Ring"));
        Assert.Equal(0.0, FuzzyMatcher.Similarity("Elden Ring", "   "));
    }

    [Fact]
    public void Scores_stay_in_range()
    {
        foreach (var (a, b) in new[] { ("a", "b"), ("", ""), ("Elden Ring", "Elden Ring"), ("x", "xxxxxxxxxxxx") })
        {
            Assert.InRange(FuzzyMatcher.Similarity(a, b), 0.0, 1.0);
        }
    }
}

public class CandidateExtractorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"nama-extract-{Guid.NewGuid():N}");

    private readonly CandidateExtractor _extractor = new();

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private string MakeGame(string folder, string executable, int sizeBytes = 4096)
    {
        var directory = Path.Combine(_root, folder);
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, executable);
        File.WriteAllBytes(path, new byte[sizeBytes]);

        return path;
    }

    [Fact]
    public void A_meaningful_executable_name_outranks_the_folder()
    {
        var exe = MakeGame("Some Repack Folder", "Hollow Knight.exe");

        var result = _extractor.Extract(exe);

        Assert.False(result.ExecutableNameWasGeneric);
        Assert.Equal(CandidateOrigin.ExecutableName, result.Candidates[0].Origin);
    }

    [Fact]
    public void A_dlsite_code_in_a_nearby_filename_becomes_the_strongest_candidate()
    {
        var exe = MakeGame("Translated Game Folder", "game.exe");
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(exe)!, "RJ01234567_readme.txt"), "");

        var result = _extractor.Extract(exe);

        Assert.Equal("RJ01234567", result.Candidates[0].Value);
        Assert.Equal(CandidateOrigin.SiblingFile, result.Candidates[0].Origin);
    }

    [Theory]
    [InlineData("game.exe")]
    [InlineData("start.exe")]
    [InlineData("launcher.exe")]
    [InlineData("onscripter-ru.exe")]
    [InlineData("siglusengine.exe")]
    public void A_generic_or_engine_executable_defers_to_the_folder(string executable)
    {
        var exe = MakeGame("Hollow Knight", executable);

        var result = _extractor.Extract(exe);

        Assert.True(result.ExecutableNameWasGeneric);
        Assert.Equal("Hollow Knight", result.Candidates[0].Value);
    }

    [Fact]
    public void A_japanese_folder_outranks_an_ascii_executable()
    {
        // Real shape from this library: らぶらぼ\bolero01sys.exe, where the binary is the
        // engine and the folder is the title.
        var exe = MakeGame("らぶらぼ", "bolero01sys.exe");

        var result = _extractor.Extract(exe);

        Assert.Equal("らぶらぼ", result.Candidates[0].Value);
    }

    [Theory]
    [InlineData("bolero01sys", true)]
    [InlineData("advdata02win", true)]
    [InlineData("Portal2", false)]
    [InlineData("re4", false)]
    [InlineData("l4d2", false)]
    [InlineData("eldenring", false)]
    public void Engine_binary_heuristic_does_not_catch_real_titles(string stem, bool expected)
    {
        Assert.Equal(expected, CandidateExtractor.LooksLikeEngineBinary(stem));
    }

    [Fact]
    public void A_build_directory_is_skipped_in_favour_of_the_real_folder()
    {
        var exe = MakeGame(Path.Combine("Hollow Knight", "Binaries", "Win64"), "game.exe");

        var result = _extractor.Extract(exe);

        Assert.Contains("Hollow Knight", result.Candidates.Select(c => c.Value));
        Assert.DoesNotContain(result.Candidates, c => c.Value is "Win64" or "Binaries");
    }

    [Fact]
    public void A_library_folder_never_becomes_a_candidate()
    {
        // Every game under "PC GAMES" would otherwise contribute the same useless guess.
        var exe = MakeGame(Path.Combine("PC GAMES", "Hollow Knight"), "game.exe");

        var result = _extractor.Extract(exe);

        Assert.DoesNotContain(result.Candidates, c => c.Value.Contains("GAMES", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Selecting_a_folder_finds_the_game_executable_inside_it()
    {
        var directory = Path.Combine(_root, "Elden Ring");
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "eldenring.exe"), new byte[4 * 1024 * 1024]);
        File.WriteAllBytes(Path.Combine(directory, "unins000.exe"), new byte[1024]);
        File.WriteAllBytes(Path.Combine(directory, "vcredist_x64.exe"), new byte[8 * 1024 * 1024]);

        var result = _extractor.Extract(directory);

        // Installers and uninstallers are excluded even when they are larger.
        Assert.Equal("eldenring.exe", Path.GetFileName(result.ExecutablePath));
    }

    [Fact]
    public void A_folder_with_no_executable_reports_a_warning_instead_of_failing()
    {
        var directory = Path.Combine(_root, "Empty Game");
        Directory.CreateDirectory(directory);

        var result = _extractor.Extract(directory);

        Assert.NotNull(result.Warning);
        Assert.NotEmpty(result.Candidates); // the folder name is still a usable guess
    }

    [Fact]
    public void A_missing_path_is_reported_rather_than_throwing()
    {
        var result = _extractor.Extract(Path.Combine(_root, "does-not-exist.exe"));

        Assert.NotNull(result.Warning);
    }

    [Fact]
    public void Repack_noise_is_stripped_from_the_folder_name()
    {
        var exe = MakeGame("ELDEN-RING-v1.12.2-FITGIRL", "game.exe");

        var result = _extractor.Extract(exe);

        Assert.Equal("Elden Ring", result.BestGuess);
    }

    [Fact]
    public void Candidates_are_ordered_by_weight_and_deduplicated()
    {
        var exe = MakeGame("Hollow Knight", "Hollow Knight.exe");

        var result = _extractor.Extract(exe);

        Assert.Equal(result.Candidates.OrderByDescending(c => c.Weight), result.Candidates);
        Assert.Equal(result.Candidates.Select(c => c.Value).Distinct().Count(), result.Candidates.Count);
    }

    [Fact]
    public void Start_directory_is_the_executables_folder()
    {
        var exe = MakeGame("Hollow Knight", "game.exe");

        var result = _extractor.Extract(exe);

        Assert.Equal(Path.GetDirectoryName(exe), result.StartDirectory);
    }
}

public class GameRankingTests
{
    private static GameCandidate Candidate(
        string name, string source = "steam", string id = "1",
        int? year = null, params string[] aliases) => new()
    {
        Source = source,
        SourceId = id,
        Name = name,
        Aliases = aliases,
        ReleaseDate = year is null ? null : new DateOnly(year.Value, 1, 1),
    };

    private static LocalNameCandidate Local(string value, double weight = 1.0) =>
        new(value, CandidateOrigin.FolderName, weight, value);

    [Fact]
    public void The_exact_title_ranks_first()
    {
        var ranked = GameIdentifier.Rank(
            [
                Candidate("ELDEN RING NIGHTREIGN", id: "2"),
                Candidate("ELDEN RING", id: "1"),
                Candidate("ELDEN RING Shadow of the Erdtree", id: "3"),
            ],
            [Local("Elden Ring")]);

        Assert.Equal("1", ranked[0].SourceId);
        Assert.Equal(1.0, ranked[0].Confidence);
    }

    [Fact]
    public void Matching_an_alias_counts_as_matching_the_game()
    {
        var ranked = GameIdentifier.Rank(
            [Candidate("Subarashiki Hibi", aliases: "素晴らしき日々～不連続存在～")],
            [Local("素晴らしき日々")]);

        Assert.True(ranked[0].Confidence > 0.8);
    }

    [Fact]
    public void A_weak_source_cannot_outrank_a_strong_one_on_a_lucky_hit()
    {
        var ranked = GameIdentifier.Rank(
            [Candidate("Bolero", id: "wrong"), Candidate("らぶらぼ", id: "right")],
            [
                Local("らぶらぼ", weight: 0.95),   // the folder
                Local("bolero01sys", weight: 0.24), // the engine binary
            ]);

        Assert.Equal("right", ranked[0].SourceId);
    }

    [Fact]
    public void Confidence_is_the_best_single_pairing_not_an_average()
    {
        // Most local guesses are noise by design; averaging would bury the one that hits.
        var ranked = GameIdentifier.Rank(
            [Candidate("Elden Ring")],
            [Local("Elden Ring"), Local("nonsense"), Local("more nonsense")]);

        Assert.Equal(1.0, ranked[0].Confidence);
    }

    [Fact]
    public void A_dated_release_breaks_a_tie_against_a_bare_entry()
    {
        var ranked = GameIdentifier.Rank(
            [Candidate("Elden Ring", id: "bare"), Candidate("Elden Ring", id: "dated", year: 2022)],
            [Local("Elden Ring")]);

        Assert.Equal("dated", ranked[0].SourceId);
    }

    [Fact]
    public void Ranking_an_empty_result_set_is_safe()
    {
        Assert.Empty(GameIdentifier.Rank([], [Local("Elden Ring")]));
    }
}
