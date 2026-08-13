using Nama.Core.Normalization;
using Xunit;

namespace Nama.Tests;

public class NameNormalizerTests
{
    private readonly NameNormalizer _normalizer = new();

    [Theory]
    // The three worked examples from the product spec.
    [InlineData("ELDEN-RING-v1.12.2-FITGIRL", "Elden Ring")]
    [InlineData("White_Album_2_Closing_Chapter", "White Album 2 Closing Chapter")]
    [InlineData("SubaHibiEN", "Subarashiki Hibi")]
    // Common real-world shapes.
    [InlineData("White_Album_2-v1.04-FINAL", "White Album 2")]
    [InlineData("Cyberpunk.2077.v2.1.REPACK-DODI", "Cyberpunk 2077")]
    [InlineData("Hades_v1.38290_[FitGirl]", "Hades")]
    [InlineData("EldenRing-Win64-Shipping", "Elden Ring")]
    [InlineData("DarkSouls3", "Dark Souls 3")]
    [InlineData("stardew valley", "Stardew Valley")]
    public void Normalize_produces_the_expected_title(string raw, string expected)
    {
        var result = _normalizer.Normalize(raw);
        Assert.Equal(expected, result.Normalized);
    }

    [Fact]
    public void Normalize_strips_release_groups_and_versions_but_records_them()
    {
        var result = _normalizer.Normalize("ELDEN-RING-v1.12.2-FITGIRL");

        Assert.Equal("Elden Ring", result.Normalized);
        Assert.Contains(result.RemovedTokens, t => t.Contains("1.12.2"));
        Assert.Contains(result.RemovedTokens, t => t.Equals("FITGIRL", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Normalize_keeps_the_raw_input_for_display()
    {
        const string raw = "ELDEN-RING-v1.12.2-FITGIRL";
        Assert.Equal(raw, _normalizer.Normalize(raw).Raw);
    }

    [Fact]
    public void Normalize_offers_the_base_title_as_a_fallback_candidate()
    {
        var result = _normalizer.Normalize("White Album 2 Closing Chapter");

        // Providers frequently index only the base title, so it must be searched too.
        Assert.Contains("White Album", result.Candidates);
    }

    [Fact]
    public void Normalize_expands_abbreviations_but_keeps_the_literal_reading()
    {
        var result = _normalizer.Normalize("SubaHibiEN");

        Assert.Equal("Subarashiki Hibi", result.Normalized);
        Assert.Contains("Suba Hibi", result.Candidates);
    }

    [Fact]
    public void Normalize_preserves_a_fully_Japanese_title_untouched()
    {
        var result = _normalizer.Normalize("素晴らしき日々");

        Assert.True(result.ContainsCjk);
        Assert.Equal("素晴らしき日々", result.Normalized);
    }

    [Theory]
    // Words like "Game", "HD" and "Complete" describe packaging *and* appear in real
    // titles. Stripping them outright turned "There Is No Game" into "There Is No".
    [InlineData("There Is No Game", "There Is No Game")]
    [InlineData("The Game", "The Game")]
    [InlineData("Devil May Cry HD Collection", "Devil May Cry HD Collection")]
    [InlineData("Horizon Forbidden West Complete Edition", "Horizon Forbidden West Complete Edition")]
    [InlineData("Game Dev Tycoon", "Game Dev Tycoon")]
    [InlineData("Final Fantasy VII Remake", "Final Fantasy VII Remake")]
    public void Normalize_keeps_noise_words_that_are_part_of_the_title(string raw, string expected)
    {
        Assert.Equal(expected, _normalizer.Normalize(raw).Normalized);
    }

    [Fact]
    public void Normalize_offers_the_trimmed_form_as_a_fallback_candidate()
    {
        // The padded title leads, but a database that indexes the bare name is still reachable.
        var result = _normalizer.Normalize("There Is No Game");

        Assert.Equal("There Is No Game", result.Candidates[0]);
        Assert.Contains("There Is No", result.Candidates);
    }

    [Theory]
    // A bare "V<n>" in the middle of a title is part of the name, not a build number.
    [InlineData("Danganronpa V3 Killing Harmony", "Danganronpa V3 Killing Harmony")]
    [InlineData("Danganronpa_V3_Killing_Harmony-CODEX", "Danganronpa V3 Killing Harmony")]
    public void Normalize_does_not_mistake_a_title_number_for_a_version(string raw, string expected)
    {
        Assert.Equal(expected, _normalizer.Normalize(raw).Normalized);
    }

    [Theory]
    // Genuine version strings must still go.
    [InlineData("Hades_v1.38290", "Hades")]
    [InlineData("Some Game v2", "Some Game")]
    [InlineData("Stardew Valley 1.6.9", "Stardew Valley")]
    public void Normalize_still_removes_real_version_strings(string raw, string expected)
    {
        Assert.Equal(expected, _normalizer.Normalize(raw).Normalized);
    }

    [Fact]
    public void Normalize_does_not_destroy_a_title_that_is_itself_a_noise_word()
    {
        // "Portal" and "Control" are real games whose names appear in the noise list.
        Assert.Equal("Portal", _normalizer.Normalize("Portal").Normalized);
        Assert.Equal("Control", _normalizer.Normalize("Control").Normalized);
    }

    [Fact]
    public void Normalize_falls_back_to_the_raw_name_when_everything_is_noise()
    {
        var result = _normalizer.Normalize("setup_installer_x64");

        Assert.True(result.IsFallback);
        Assert.False(string.IsNullOrWhiteSpace(result.Normalized));
    }

    [Fact]
    public void Normalize_preserves_stylized_and_acronym_casing_in_mixed_case_names()
    {
        Assert.Equal("NieR Automata", _normalizer.Normalize("NieR Automata").Normalized);
        Assert.Equal("Final Fantasy XIV", _normalizer.Normalize("Final Fantasy XIV").Normalized);
    }

    [Fact]
    public void Normalize_handles_empty_input()
    {
        Assert.Equal(string.Empty, _normalizer.Normalize(null).Normalized);
        Assert.Equal(string.Empty, _normalizer.Normalize("   ").Normalized);
    }

    [Theory]
    [InlineData("Elden Ring", "elden-ring")]
    [InlineData("Elden Ring", "ELDEN_RING")]
    [InlineData("Muv-Luv", "Muv Luv")]
    [InlineData("Pokémon", "Pokemon")]
    public void MatchKey_ignores_punctuation_spacing_case_and_accents(string a, string b)
    {
        Assert.Equal(NameNormalizer.BuildMatchKey(a), NameNormalizer.BuildMatchKey(b));
    }
}
