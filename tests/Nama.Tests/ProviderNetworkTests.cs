using Nama.Core.Aggregation;
using Nama.Core.Models;
using Nama.Providers;
using Nama.Providers.Steam;
using Nama.Providers.Vndb;

namespace Nama.Tests;

/// <summary>
/// Live calls against the real APIs. These verify assumptions that offline mapping tests
/// cannot — that the endpoints still exist, still return the shapes Nama expects, and that
/// the constructed Steam CDN paths actually resolve.
/// <para>
/// Excluded from the default run because they are slow and depend on someone else's uptime:
/// </para>
/// <code>dotnet test --filter "Category!=Network"</code>
/// <para>Run them deliberately with:</para>
/// <code>dotnet test --filter "Category=Network"</code>
/// </summary>
[Trait("Category", "Network")]
public class ProviderNetworkTests
{
    private static ProviderHttp Transport() =>
        new(new HttpClient { Timeout = TimeSpan.FromSeconds(30) }, new ProviderOptions());

    [Fact]
    public async Task Steam_search_returns_elden_ring()
    {
        var results = await new SteamProvider(Transport()).SearchAsync("elden ring");

        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.SourceId == "1245620");
    }

    [Fact]
    public async Task Steam_enrichment_supplies_developer_and_year()
    {
        var results = await new SteamProvider(Transport()).SearchAsync("elden ring");
        var eldenRing = results.First(r => r.SourceId == "1245620");

        Assert.Contains("FromSoftware", eldenRing.Developer);
        Assert.Equal(2022, eldenRing.ReleaseDate?.Year);
    }

    [Fact]
    public async Task Steam_cdn_paths_still_resolve()
    {
        // The constructed CDN URLs are an assumption about someone else's infrastructure.
        // If Valve moves them, this is what catches it.
        var artwork = await new SteamArtworkProvider(Transport()).GetArtworkAsync(
            new GameRef([new KeyValuePair<string, string>("steam", "1245620")], "ELDEN RING"));

        Assert.Contains(artwork, a => a.Type == ArtworkType.Cover);
        Assert.Contains(artwork, a => a.Type == ArtworkType.Grid);
        Assert.Contains(artwork, a => a.Type == ArtworkType.Hero);
        Assert.Contains(artwork, a => a.Type == ArtworkType.Logo);
    }

    [Fact]
    public async Task Steam_hashed_artwork_fallback_resolves_machine_party()
    {
        // Regression: app 4108000 has no legacy apps/{id}/{file} assets. Steam's
        // appdetails response contains the only usable, hashed artwork URL.
        var artwork = await new SteamArtworkProvider(Transport()).GetArtworkAsync(
            new GameRef([new KeyValuePair<string, string>("steam", "4108000")], "Machine Party"));

        Assert.Contains(artwork, a =>
            a.Type == ArtworkType.Grid &&
            a.Url.Contains("store_item_assets/steam/apps/4108000/"));
    }

    [Fact]
    public async Task Steam_artwork_probing_rejects_a_nonexistent_app()
    {
        var artwork = await new SteamArtworkProvider(Transport()).GetArtworkAsync(
            new GameRef([new KeyValuePair<string, string>("steam", "999999999")], "Nothing"));

        Assert.Empty(artwork);
    }

    [Fact]
    public async Task Vndb_finds_subarashiki_hibi_with_its_japanese_title()
    {
        var results = await new VndbProvider(Transport()).SearchAsync("Subarashiki Hibi");

        var vn = results.First(r => r.SourceId == "v3144");
        Assert.Contains("素晴らしき日々", vn.JapaneseName);
        Assert.Equal("KeroQ", vn.Developer);
    }

    [Fact]
    public async Task Vndb_returns_cover_artwork()
    {
        var artwork = await new VndbProvider(Transport()).GetArtworkAsync(
            new GameRef([new KeyValuePair<string, string>("vndb", "v3144")], "Subarashiki Hibi"));

        Assert.Contains(artwork, a => a.Type == ArtworkType.Cover && a.Width > 0);
    }

    [Fact]
    public async Task A_japanese_query_reaches_the_right_visual_novel()
    {
        // The path a folder named in Japanese takes — no romanization involved.
        var results = await new VndbProvider(Transport()).SearchAsync("素晴らしき日々");

        Assert.Contains(results, r => r.SourceId == "v3144");
    }

    [Fact]
    public async Task The_full_search_path_survives_a_repack_folder_name()
    {
        using var providers = ProviderFactory.Create(new ProviderOptions());
        var analysis = new Core.Normalization.NameNormalizer().Normalize("ELDEN-RING-v1.12.2-FITGIRL");

        var results = await new GameSearchAggregator(providers.GameProviders)
            .SearchAsync(analysis.Normalized);

        Assert.Contains(results.Candidates, c => c.SourceId == "1245620");
    }
}
