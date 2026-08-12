using Nama.Core.Models;
using Nama.Core.Normalization;

namespace Nama.Tests;

public class NameNormalizerTests
{
    // Isolated from %APPDATA% so a user override on the dev machine can never change
    // test outcomes.
    private static readonly NameNormalizer Normalizer =
        new(NormalizationData.Load(Path.Combine(Path.GetTempPath(), "nama-tests-no-override")));

    [Theory]
    // --- the three acceptance cases from the spec ---------------------------------
    [InlineData("ELDEN-RING-v1.12.2-FITGIRL", "Elden Ring")]
    [InlineData("White_Album_2_Closing_Chapter", "White Album 2 Closing Chapter")]
    [InlineData("SubaHibiEN.exe", "Subarashiki Hibi")]
    // --- version and release-group stripping ---------------------------------------
    [InlineData("White_Album_2-v1.04-FINAL", "White Album 2")]
    [InlineData("Cyberpunk.2077.v2.1.DODI.Repack", "Cyberpunk 2077")]
    [InlineData("Hades_II_v0.90.1_x64_Portable", "Hades II")]
    [InlineData("DOOM.Eternal.Update.6.CODEX", "DOOM Eternal")]
    [InlineData("Hollow Knight [FitGirl Repack]", "Hollow Knight")]
    [InlineData("Stardew Valley (v1.6.8) [GOG]", "Stardew Valley")]
    [InlineData("Baldurs-Gate-3-Build-4541000", "Baldurs Gate 3")]
    // --- separators and punctuation -------------------------------------------------
    [InlineData("Ori_and_the_Blind_Forest", "Ori and the Blind Forest")]
    [InlineData("Sekiro.Shadows.Die.Twice", "Sekiro Shadows Die Twice")]
    [InlineData("the~witness~", "The Witness")]
    // --- title information that must NOT be destroyed --------------------------------
    [InlineData("PC Building Simulator", "PC Building Simulator")]
    [InlineData("Final Fantasy X", "Final Fantasy X")]
    [InlineData("Football Manager 2024", "Football Manager 2024")]
    [InlineData("The Last of Us Part II Remastered", "The Last of Us Part II Remastered")]
    [InlineData("Portal 2", "Portal 2")]
    // --- abbreviation and punctuation restoration ------------------------------------
    [InlineData("SteinsGate.exe", "Steins;Gate")]
    [InlineData("chaoschild", "Chaos;Child")]
    [InlineData("Umineko", "Umineko no Naku Koro ni")]
    [InlineData("DDLC.exe", "Doki Doki Literature Club")]
    // --- real folder names from this machine ------------------------------------------
    [InlineData("Konosuba Love for these Clothes of Desire!", "Konosuba Love for these Clothes of Desire!")]
    [InlineData("The House in Fata Morgana", "The House in Fata Morgana")]
    public void Normalizes_to_expected_display_name(string raw, string expected)
    {
        var result = Normalizer.Normalize(raw);

        Assert.Equal(expected, result.DisplayName);
    }

    [Theory]
    [InlineData("ホワイトアルバム2")]
    [InlineData("素晴らしき日々")]
    public void Preserves_japanese_titles_verbatim(string raw)
    {
        var result = Normalizer.Normalize(raw);

        Assert.True(result.HasCjk);
        Assert.Contains(raw, result.CandidateValues);
        // Title-casing must never touch CJK.
        Assert.Equal(raw, result.DisplayName);
    }

    [Fact]
    public void Extracts_japanese_run_from_a_mixed_name()
    {
        var result = Normalizer.Normalize("ホワイトアルバム2 White Album 2");

        Assert.Contains("ホワイトアルバム2", result.CandidateValues);
        Assert.Contains(result.Candidates, c => c.Kind == NameCandidateKind.Cjk);
    }

    [Fact]
    public void Keeps_a_lower_weighted_candidate_without_edition_markers()
    {
        var result = Normalizer.Normalize("Elden Ring Deluxe Edition");

        // The primary keeps the edition, because dropping it could pick the wrong game...
        Assert.Equal("Elden Ring Deluxe Edition", result.DisplayName);
        // ...but the stripped form is still searchable.
        Assert.Contains("Elden Ring", result.CandidateValues);
    }

    [Fact]
    public void Never_normalizes_away_the_entire_name()
    {
        // Every token is noise. Stripping them all would leave nothing to search for.
        var result = Normalizer.Normalize("setup.exe");

        Assert.False(string.IsNullOrWhiteSpace(result.Normalized));
        Assert.NotEmpty(result.Candidates);
    }

    [Fact]
    public void Records_what_it_removed_so_a_bad_match_can_be_explained()
    {
        var result = Normalizer.Normalize("ELDEN-RING-v1.12.2-FITGIRL");

        Assert.Contains("v1.12.2", result.RemovedTokens);
        Assert.Contains("FITGIRL", result.RemovedTokens);
    }

    [Fact]
    public void Retains_the_raw_input_untouched()
    {
        const string raw = "  ELDEN-RING-v1.12.2-FITGIRL  ";

        Assert.Equal(raw, Normalizer.Normalize(raw).Raw);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Handles_empty_input_without_throwing(string? raw)
    {
        var result = Normalizer.Normalize(raw);

        Assert.NotNull(result);
        Assert.Equal(raw ?? string.Empty, result.Raw);
    }

    [Fact]
    public void Candidates_are_ordered_by_weight_and_deduplicated()
    {
        var result = Normalizer.Normalize("SteinsGate.exe");

        Assert.Equal(result.Candidates.OrderByDescending(c => c.Weight), result.Candidates);
        Assert.Equal(
            result.Candidates.Select(c => TextTools.Compact(c.Value)).Distinct().Count(),
            result.Candidates.Count);
    }
}
