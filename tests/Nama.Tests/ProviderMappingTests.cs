using System.Net;
using Nama.Core.Models;
using Nama.Providers;
using Nama.Providers.Dlsite;
using Nama.Providers.Steam;
using Nama.Providers.SteamGridDb;
using Nama.Providers.Vndb;

namespace Nama.Tests;

/// <summary>
/// Mapping from provider responses to Nama's models, driven by payloads captured from the
/// real APIs. These run offline; the live calls are in <see cref="ProviderNetworkTests"/>.
/// </summary>
public class ProviderMappingTests
{
    private const string DlsiteJson = """
        {"workno":"RJ01234567","work_name":"Test Japanese Game","work_name_kana":"テストゲーム",
         "product_name":"Test Japanese Game","maker_name":"Test Circle","regist_date":"2024-05-06 00:00:00",
         "platform":["pc"],
         "image_main":{"id":"1","url":"//img.dlsite.jp/main.jpg","width":"560","height":"420"},
         "image_samples":[{"id":"2","url":"//img.dlsite.jp/sample.jpg","width":"1280","height":"720"}]}
        """;

    [Fact]
    public async Task Dlsite_only_resolves_exact_product_codes_and_maps_artwork()
    {
        var handler = StubHandler.ForUrls(("product.json", DlsiteJson));
        var provider = new DlsiteProvider(Transport(handler));

        Assert.Empty(await provider.SearchAsync("Test Japanese Game"));
        var game = Assert.Single(await provider.SearchAsync("Test Japanese Game [RJ01234567]"));
        Assert.Equal("Test Japanese Game", game.Name);
        Assert.Equal("Test Circle", game.Developer);
        Assert.Equal(new DateOnly(2024, 5, 6), game.ReleaseDate);
        Assert.Equal("https://img.dlsite.jp/main.jpg", game.CoverUrl);

        var artwork = await provider.GetArtworkAsync(
            new GameRef([new KeyValuePair<string, string>("dlsite", "RJ01234567")], game.Name));
        Assert.Single(artwork, a => a.Type == ArtworkType.Cover);
        Assert.Single(artwork, a => a.Type == ArtworkType.Background);
    }

    [Fact]
    public async Task Experimental_providers_make_no_requests_when_disabled()
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException("HTTP should not be called"));
        var options = new ProviderOptions { EnableDlsite = false, EnableVndb = false };

        var dlsite = new DlsiteProvider(Transport(handler, options));
        var vndb = new VndbProvider(Transport(handler, options));

        Assert.False(dlsite.IsAvailable);
        Assert.False(vndb.IsAvailable);
        Assert.Empty(await dlsite.SearchAsync("RJ01234567"));
        Assert.Empty(await vndb.SearchAsync("Umineko"));
        Assert.Equal(0, handler.CallCount);
    }

    private static ProviderHttp Transport(HttpMessageHandler handler, ProviderOptions? options = null) =>
        new(StubHandler.Client(handler), options ?? new ProviderOptions());

    // Captured from store.steampowered.com/api/storesearch/?term=elden%20ring
    private const string StoreSearchJson = """
        {"total":3,"items":[
          {"type":"app","name":"ELDEN RING","id":1245620,
           "tiny_image":"https://shared.akamai.steamstatic.com/x/capsule_231x87.jpg",
           "metascore":"94","platforms":{"windows":true,"mac":false,"linux":false}},
          {"type":"app","name":"ELDEN RING NIGHTREIGN","id":2622380,
           "tiny_image":"https://shared.akamai.steamstatic.com/y/capsule_231x87.jpg",
           "metascore":"","platforms":{"windows":true,"mac":true,"linux":false}},
          {"type":"bundle","name":"ELDEN RING Bundle","id":99999,"platforms":{"windows":true}}
        ]}
        """;

    // Captured from store.steampowered.com/api/appdetails?appids=1245620
    private const string AppDetailsJson = """
        {"1245620":{"success":true,"data":{
          "type":"game","name":"ELDEN RING",
          "developers":["FromSoftware, Inc."],
          "publishers":["FromSoftware, Inc.","Bandai Namco Entertainment"],
          "release_date":{"coming_soon":false,"date":"Feb 24, 2022"}}}}
        """;

    [Fact]
    public async Task Steam_maps_search_results()
    {
        var handler = StubHandler.ForUrls(("storesearch", StoreSearchJson), ("appdetails", AppDetailsJson));
        var provider = new SteamProvider(Transport(handler));

        var results = await provider.SearchAsync("elden ring");

        // The bundle is dropped: it cannot be a shortcut target.
        Assert.Equal(2, results.Count);
        Assert.Equal("ELDEN RING", results[0].Name);
        Assert.Equal("1245620", results[0].SourceId);
        Assert.Equal("steam", results[0].Source);
        Assert.Equal("https://shared.akamai.steamstatic.com/x/capsule_231x87.jpg", results[0].CoverUrl);
        Assert.Equal(["Windows"], results[0].Platforms);
        Assert.Equal(["Windows", "macOS"], results[1].Platforms);
    }

    [Fact]
    public async Task Steam_enriches_results_with_developer_and_release_date()
    {
        var handler = StubHandler.ForUrls(("storesearch", StoreSearchJson), ("appdetails", AppDetailsJson));

        var results = await new SteamProvider(Transport(handler)).SearchAsync("elden ring");

        Assert.Equal("FromSoftware, Inc.", results[0].Developer);
        Assert.Equal("FromSoftware, Inc.", results[0].Publisher);
        Assert.Equal(new DateOnly(2022, 2, 24), results[0].ReleaseDate);
    }

    [Fact]
    public async Task Steam_respects_the_enrichment_limit()
    {
        var handler = StubHandler.ForUrls(("storesearch", StoreSearchJson), ("appdetails", AppDetailsJson));
        var options = new ProviderOptions { SteamEnrichmentLimit = 1 };

        await new SteamProvider(Transport(handler, options)).SearchAsync("elden ring");

        // One search plus exactly one detail call — the endpoint is rate limited.
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task Steam_still_returns_results_when_enrichment_fails()
    {
        // appdetails 429s (rate limited) but the search itself succeeded.
        var handler = new StubHandler(request =>
            request.RequestUri!.ToString().Contains("storesearch")
                ? StubHandler.Json(StoreSearchJson)
                : StubHandler.Status(HttpStatusCode.TooManyRequests));

        var results = await new SteamProvider(Transport(handler)).SearchAsync("elden ring");

        Assert.Equal(2, results.Count);
        Assert.Null(results[0].Developer); // unlabelled, but present and usable
    }

    [Fact]
    public async Task Steam_returns_empty_rather_than_throwing_when_the_store_is_down()
    {
        var handler = new StubHandler(_ => StubHandler.Status(HttpStatusCode.ServiceUnavailable));

        Assert.Empty(await new SteamProvider(Transport(handler)).SearchAsync("elden ring"));
    }

    [Fact]
    public async Task Steam_uses_published_hashed_artwork_when_legacy_paths_are_missing()
    {
        // Captured from Machine Party (appid 4108000). Every predictable CDN path is a
        // 404; newer Steam apps publish artwork beneath an asset-specific hash instead.
        const string details = """
            {"4108000":{"success":true,"data":{
              "name":"Machine Party",
              "header_image":"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/4108000/00ae7d0e0fd0ad72635bddb1dd80ee68cc94eed5/header.jpg?t=1786561627",
              "capsule_image":"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/4108000/5986ba4057fa210d0f96bd470e41a0dd439ed6fb/capsule_231x87.jpg?t=1786561627"
            }}}
            """;
        const string storeBrowse = """
            {"response":{"store_items":[{"appid":4108000,"assets":{
              "asset_url_format":"steam/apps/4108000/${FILENAME}?t=1786561627",
              "main_capsule":"2e5f2365/capsule_616x353.jpg",
              "library_capsule":"d7697088/library_capsule.jpg",
              "library_hero":"bdbc1744/library_hero.jpg"
            }}]}}
            """;

        var handler = new StubHandler(request =>
            request.Method == HttpMethod.Head
                ? StubHandler.Status(HttpStatusCode.NotFound)
                : request.RequestUri!.ToString().Contains("IStoreBrowseService")
                    ? StubHandler.Json(storeBrowse)
                    : StubHandler.Json(details));

        var artwork = await new SteamArtworkProvider(Transport(handler)).GetArtworkAsync(
            new GameRef([new KeyValuePair<string, string>("steam", "4108000")], "Machine Party"));

        Assert.Contains(artwork, a => a.Type == ArtworkType.Grid && a.Url.Contains("2e5f2365"));
        Assert.Contains(artwork, a => a.Type == ArtworkType.Cover && a.Url.Contains("d7697088"));
        Assert.Contains(artwork, a => a.Type == ArtworkType.Hero && a.Url.Contains("bdbc1744"));
        Assert.DoesNotContain(artwork, a => a.Type == ArtworkType.Logo);
        Assert.DoesNotContain(handler.RequestedUrls, u => u.Contains("api/appdetails"));
    }

    // Captured from api.vndb.org/kana/vn, filters ["search","=","white album 2"]
    private const string VndbJson = """
        {"results":[{
          "id":"v7771","title":"WHITE ALBUM2","alttitle":null,
          "released":"2010-03-26",
          "image":{"url":"https://t.vndb.org/cv/62/88962.jpg","dims":[411,560],"sexual":0},
          "developers":[{"id":"p21","name":"Leaf"},{"id":"p87","name":"AQUAPLUS"}],
          "titles":[{"lang":"ja","title":"WHITE ALBUM2","official":true},
                    {"lang":"zh-Hans","title":"白色相簿2","official":false}],
          "screenshots":[{"url":"https://t.vndb.org/sf/75/26875.jpg","dims":[800,600],"sexual":1}]
        }],"more":false}
        """;

    [Fact]
    public async Task Vndb_maps_titles_aliases_and_developer()
    {
        var handler = StubHandler.ForUrls(("vndb.org", VndbJson));

        var results = await new VndbProvider(Transport(handler)).SearchAsync("white album 2");

        var vn = Assert.Single(results);
        Assert.Equal("v7771", vn.SourceId);
        Assert.Equal("WHITE ALBUM2", vn.Name);
        Assert.Equal("Leaf", vn.Developer);
        Assert.Equal(new DateOnly(2010, 3, 26), vn.ReleaseDate);
        Assert.Contains("白色相簿2", vn.Aliases);
    }

    [Fact]
    public async Task Vndb_falls_back_to_the_titles_list_when_alttitle_is_null()
    {
        // alttitle is null whenever the main title is already the original — the Japanese
        // name then has to come from titles[lang=ja].
        var handler = StubHandler.ForUrls(("vndb.org", VndbJson));

        var results = await new VndbProvider(Transport(handler)).SearchAsync("white album 2");

        Assert.Equal("WHITE ALBUM2", results[0].JapaneseName);
    }

    [Fact]
    public async Task Vndb_maps_cover_and_screenshots_to_the_right_slots()
    {
        var handler = StubHandler.ForUrls(("vndb.org", VndbJson));
        var provider = new VndbProvider(Transport(handler));

        var artwork = await provider.GetArtworkAsync(
            new GameRef([new KeyValuePair<string, string>("vndb", "v7771")], "WHITE ALBUM2"));

        var cover = Assert.Single(artwork, a => a.Type == ArtworkType.Cover);
        Assert.Equal(411, cover.Width);
        Assert.Equal(560, cover.Height);
        Assert.False(cover.IsNsfw);

        var screenshot = Assert.Single(artwork, a => a.Type == ArtworkType.Background);
        Assert.True(screenshot.IsNsfw); // flagged, not filtered
        Assert.True(cover.Score > screenshot.Score);
    }

    [Fact]
    public async Task Vndb_adds_official_release_package_and_digital_covers()
    {
        const string releases = """
            {"results":[{"id":"r1","images":[
              {"id":"cv1","url":"https://t.vndb.org/cv/pkg.jpg","dims":[600,900],"sexual":0,"type":"pkgfront"},
              {"id":"cv2","url":"https://t.vndb.org/cv/dig.jpg","dims":[800,800],"sexual":1,"type":"dig"},
              {"id":"cv3","url":"https://t.vndb.org/cv/back.jpg","dims":[600,900],"sexual":0,"type":"pkgback"}
            ]}],"more":false}
            """;
        var handler = new StubHandler(request => request.RequestUri!.AbsolutePath.EndsWith("/release")
            ? StubHandler.Json(releases) : StubHandler.Json(VndbJson));

        var artwork = await new VndbProvider(Transport(handler)).GetArtworkAsync(
            new GameRef([new KeyValuePair<string, string>("vndb", "v7771")], "WHITE ALBUM2"));

        Assert.Contains(artwork, a => a.Url.EndsWith("pkg.jpg") && a.Type == ArtworkType.Cover);
        Assert.Contains(artwork, a => a.Url.EndsWith("dig.jpg") && a.IsNsfw);
        Assert.DoesNotContain(artwork, a => a.Url.EndsWith("back.jpg"));
    }

    // Shape per SteamGridDB's documented v2 API.
    private const string SgdbGameJson = """{"success":true,"data":{"id":5247,"name":"ELDEN RING"}}""";

    private const string SgdbGridsJson = """
        {"success":true,"data":[
          {"id":1,"score":0,"style":"alternate","width":920,"height":430,"nsfw":false,
           "mime":"image/png","url":"https://cdn2.steamgriddb.com/grid/wide.png",
           "thumb":"https://cdn2.steamgriddb.com/thumb/wide.png","upvotes":90,"downvotes":10,
           "author":{"name":"someone"}},
          {"id":2,"score":0,"style":"alternate","width":600,"height":900,"nsfw":false,
           "mime":"image/gif","url":"https://cdn2.steamgriddb.com/grid/tall.gif",
           "thumb":"https://cdn2.steamgriddb.com/thumb/tall.png","upvotes":0,"downvotes":0,
           "author":{"name":"other"}}
        ]}
        """;

    private static ProviderOptions WithKey => new() { SteamGridDbApiKey = "test-key" };

    [Fact]
    public async Task SteamGridDb_is_unavailable_without_a_key()
    {
        var provider = new SteamGridDbProvider(Transport(new StubHandler(_ => StubHandler.Json("{}"))));

        Assert.False(provider.IsAvailable);
        Assert.Empty(await provider.GetArtworkAsync(
            new GameRef([new KeyValuePair<string, string>("steam", "1245620")], "ELDEN RING")));
    }

    [Fact]
    public async Task SteamGridDb_splits_grids_into_covers_and_banners_by_shape()
    {
        // The API returns both the wide capsule and the portrait cover as "grids". Steam
        // stores them in different files, so they must be separated by aspect ratio.
        var handler = StubHandler.ForUrls(
            ("games/steam", SgdbGameJson),
            ("grids/game", SgdbGridsJson),
            ("heroes/game", """{"success":true,"data":[]}"""),
            ("logos/game", """{"success":true,"data":[]}"""),
            ("icons/game", """{"success":true,"data":[]}"""));

        var artwork = await new SteamGridDbProvider(Transport(handler, WithKey)).GetArtworkAsync(
            new GameRef([new KeyValuePair<string, string>("steam", "1245620")], "ELDEN RING"));

        Assert.Equal(ArtworkType.Grid, Assert.Single(artwork, a => a.Width == 920).Type);
        Assert.Equal(ArtworkType.Cover, Assert.Single(artwork, a => a.Width == 600).Type);
    }

    [Fact]
    public async Task SteamGridDb_normalizes_votes_into_a_zero_to_one_score()
    {
        var handler = StubHandler.ForUrls(
            ("games/steam", SgdbGameJson), ("grids/game", SgdbGridsJson),
            ("heroes/game", """{"success":true,"data":[]}"""),
            ("logos/game", """{"success":true,"data":[]}"""),
            ("icons/game", """{"success":true,"data":[]}"""));

        var artwork = await new SteamGridDbProvider(Transport(handler, WithKey)).GetArtworkAsync(
            new GameRef([new KeyValuePair<string, string>("steam", "1245620")], "ELDEN RING"));

        // 90 up / 10 down.
        Assert.Equal(0.9, Assert.Single(artwork, a => a.Width == 920).Score);
        // Unvoted sits mid-range rather than at the bottom.
        Assert.Equal(0.5, Assert.Single(artwork, a => a.Width == 600).Score);
    }

    [Fact]
    public async Task SteamGridDb_flags_animated_artwork()
    {
        var handler = StubHandler.ForUrls(
            ("games/steam", SgdbGameJson), ("grids/game", SgdbGridsJson),
            ("heroes/game", """{"success":true,"data":[]}"""),
            ("logos/game", """{"success":true,"data":[]}"""),
            ("icons/game", """{"success":true,"data":[]}"""));

        var artwork = await new SteamGridDbProvider(Transport(handler, WithKey)).GetArtworkAsync(
            new GameRef([new KeyValuePair<string, string>("steam", "1245620")], "ELDEN RING"));

        Assert.True(Assert.Single(artwork, a => a.Width == 600).IsAnimated);
        Assert.False(Assert.Single(artwork, a => a.Width == 920).IsAnimated);
    }

    [Fact]
    public async Task SteamGridDb_falls_back_to_name_search_without_a_steam_id()
    {
        var handler = StubHandler.ForUrls(
            ("search/autocomplete", """{"success":true,"data":[{"id":5247,"name":"Subarashiki Hibi"}]}"""),
            ("grids/game", SgdbGridsJson),
            ("heroes/game", """{"success":true,"data":[]}"""),
            ("logos/game", """{"success":true,"data":[]}"""),
            ("icons/game", """{"success":true,"data":[]}"""));

        var artwork = await new SteamGridDbProvider(Transport(handler, WithKey)).GetArtworkAsync(
            new GameRef([new KeyValuePair<string, string>("vndb", "v3144")], "Subarashiki Hibi"));

        Assert.NotEmpty(artwork);
        Assert.Contains(handler.RequestedUrls, u => u.Contains("search/autocomplete"));
    }

    [Fact]
    public async Task SteamGridDb_sends_the_key_as_a_bearer_token()
    {
        string? authorization = null;
        var handler = new StubHandler(request =>
        {
            authorization = request.Headers.Authorization?.ToString();
            return StubHandler.Json("""{"success":true,"data":[]}""");
        });

        await new SteamGridDbProvider(Transport(handler, WithKey)).GetArtworkAsync(
            new GameRef([new KeyValuePair<string, string>("steam", "1")], "x"));

        Assert.Equal("Bearer test-key", authorization);
    }
}
