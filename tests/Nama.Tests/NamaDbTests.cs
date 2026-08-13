using System.Net;
using System.Text;
using Nama.Core.Models;
using Nama.Core.Providers;
using Nama.Providers.NamaDb;
using Nama.Storage;
using Xunit;

namespace Nama.Tests;

/// <summary>Replays canned responses and records what the provider actually sent.</summary>
internal sealed class FakeHandler(Func<HttpRequestMessage, int, HttpResponseMessage> respond) : HttpMessageHandler
{
    private int _count;

    public List<HttpRequestMessage> Requests { get; } = [];

    /// <summary>Authorization headers seen, with null recorded for an anonymous request.</summary>
    public List<string?> AuthorizationHeaders { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(request);
        AuthorizationHeaders.Add(request.Headers.Authorization?.Parameter);
        return Task.FromResult(respond(request, _count++));
    }
}

/// <summary>Test fixtures for the NamaDB provider and its device-link service.</summary>
public sealed class NamaDbTests : IDisposable
{
    private readonly string _tokenPath = Path.Combine(Path.GetTempPath(), $"nama-tokens-{Guid.NewGuid():N}.dat");

    private ProtectedTokenStore Tokens => new(_tokenPath);

    public void Dispose()
    {
        if (File.Exists(_tokenPath)) File.Delete(_tokenPath);
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static NamaSettings Enabled() => new()
    {
        NamaDbEnabled = true,
        NamaDbAdultAcceptedAt = DateTimeOffset.UtcNow,
        NamaDbApiBaseUrl = "https://api.test",
    };

    private static Game Game() => new()
    {
        CanonicalName = "Test Game",
        SourceIds = [new GameSourceId("steam", "440")],
    };

    private const string ResolveBody = """{"id":"game-1"}""";

    private static string ArtworkBody(bool canVote) => $$"""
        {"items":[{"id":"art-1","imageUrl":"https://m/i.png","thumbnailUrl":"https://m/t.webp",
        "width":920,"height":430,"upvotes":7,"downvotes":2,"rankScore":0.7,
        "currentUserVote":0,"canVote":{{(canVote ? "true" : "false")}},"author":null,"detailsUrl":"https://w/a/1"}]}
        """;

    private (NamaDbProvider Provider, NamaDbAuthService Auth, FakeHandler Handler) Build(
        NamaSettings settings, Func<HttpRequestMessage, int, HttpResponseMessage> respond)
    {
        var handler = new FakeHandler(respond);
        var client = new HttpClient(handler);
        var auth = new NamaDbAuthService(client, () => settings, Tokens);
        return (new NamaDbProvider(client, () => settings, Tokens, auth), auth, handler);
    }

    [Fact]
    public async Task Provider_is_disabled_when_the_user_has_not_switched_NamaDb_on()
    {
        var settings = Enabled();
        settings.NamaDbEnabled = false;
        var (provider, _, handler) = Build(settings, (_, _) => Json(ResolveBody));

        Assert.False(provider.IsEnabled);
        Assert.Empty(await provider.GetArtworkAsync(Game(), [ArtworkType.Grid]));
        // A disabled provider must not reach the network at all.
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Provider_is_disabled_when_the_adult_confirmation_was_declined()
    {
        var settings = Enabled();
        settings.NamaDbAdultAcceptedAt = null;
        var (provider, _, handler) = Build(settings, (_, _) => Json(ResolveBody));

        Assert.False(provider.IsEnabled);
        Assert.Empty(await provider.GetArtworkAsync(Game(), [ArtworkType.Grid]));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Anonymous_browsing_sends_no_credentials_and_still_returns_artwork()
    {
        var (provider, _, handler) = Build(Enabled(), (request, _) =>
            Json(request.RequestUri!.AbsolutePath.Contains("resolve") ? ResolveBody : ArtworkBody(canVote: false)));

        var results = await provider.GetArtworkAsync(Game(), [ArtworkType.Grid]);

        Assert.Single(results);
        Assert.False(results[0].CanVote);
        Assert.All(handler.AuthorizationHeaders, header => Assert.Null(header));
    }

    [Fact]
    public async Task Authenticated_browsing_presents_the_stored_access_token()
    {
        Tokens.Set(NamaDbAuthService.AccessTokenKey, "access-1");
        var (provider, _, handler) = Build(Enabled(), (request, _) =>
            Json(request.RequestUri!.AbsolutePath.Contains("resolve") ? ResolveBody : ArtworkBody(canVote: true)));

        var results = await provider.GetArtworkAsync(Game(), [ArtworkType.Grid]);

        Assert.True(results[0].CanVote);
        Assert.Contains("access-1", handler.AuthorizationHeaders);
    }

    [Fact]
    public async Task An_expired_access_token_is_renewed_and_the_request_retried()
    {
        Tokens.SetMany([
            new KeyValuePair<string, string?>(NamaDbAuthService.AccessTokenKey, "stale"),
            new KeyValuePair<string, string?>(NamaDbAuthService.RefreshTokenKey, "refresh-1"),
        ]);

        var (provider, _, handler) = Build(Enabled(), (request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.Contains("resolve")) return Json(ResolveBody);
            if (path.Contains("refresh")) return Json("""{"accessToken":"fresh","refreshToken":"refresh-2","expiresIn":900}""");
            // The artwork endpoint rejects the stale token, then accepts the renewed one.
            return request.Headers.Authorization?.Parameter == "fresh"
                ? Json(ArtworkBody(canVote: true))
                : Json("", HttpStatusCode.Unauthorized);
        });

        var results = await provider.GetArtworkAsync(Game(), [ArtworkType.Grid]);

        Assert.Single(results);
        Assert.Equal("fresh", Tokens.Load()[NamaDbAuthService.AccessTokenKey]);
        // The rotated refresh token must replace the spent one, or the next renewal burns the family.
        Assert.Equal("refresh-2", Tokens.Load()[NamaDbAuthService.RefreshTokenKey]);
        Assert.Contains(handler.AuthorizationHeaders, header => header == "fresh");
    }

    [Fact]
    public async Task Voting_without_a_link_reports_that_it_needs_one()
    {
        var (provider, _, _) = Build(Enabled(), (_, _) => Json("", HttpStatusCode.Unauthorized));

        await Assert.ThrowsAsync<NamaDbNotLinkedException>(
            () => provider.VoteAsync("art-1", ArtworkVoteValue.Up));
    }

    [Fact]
    public async Task Voting_returns_the_counts_the_server_reports()
    {
        Tokens.Set(NamaDbAuthService.AccessTokenKey, "access-1");
        var (provider, _, _) = Build(Enabled(), (_, _) =>
            Json("""{"upvotes":8,"downvotes":2,"rankScore":0.8,"currentUserVote":1}"""));

        var result = await provider.VoteAsync("art-1", ArtworkVoteValue.Up);

        Assert.Equal(8, result.Upvotes);
        Assert.Equal(ArtworkVoteValue.Up, result.CurrentVote);
    }

    [Fact]
    public async Task A_pending_device_code_keeps_the_handshake_waiting()
    {
        var (_, auth, _) = Build(Enabled(), (_, _) => Json("""{"status":"pending"}""", HttpStatusCode.Accepted));

        Assert.Equal(DeviceLinkStatus.Pending, await auth.PollOnceAsync("device-1"));
        Assert.False(auth.IsLinked);
    }

    [Fact]
    public async Task An_approved_device_code_stores_both_tokens()
    {
        var (_, auth, _) = Build(Enabled(), (_, _) =>
            Json("""{"accessToken":"access-1","refreshToken":"refresh-1","expiresIn":900}"""));

        Assert.Equal(DeviceLinkStatus.Linked, await auth.PollOnceAsync("device-1"));
        Assert.True(auth.IsLinked);

        var stored = Tokens.Load();
        Assert.Equal("access-1", stored[NamaDbAuthService.AccessTokenKey]);
        Assert.Equal("refresh-1", stored[NamaDbAuthService.RefreshTokenKey]);
    }

    [Fact]
    public async Task An_expired_device_code_stops_the_handshake_rather_than_polling_forever()
    {
        var (_, auth, _) = Build(Enabled(), (_, _) => Json("", HttpStatusCode.NotFound));

        Assert.Equal(DeviceLinkStatus.Expired, await auth.PollOnceAsync("device-1"));
    }

    [Fact]
    public async Task A_rejected_refresh_token_clears_the_stored_identity()
    {
        Tokens.SetMany([
            new KeyValuePair<string, string?>(NamaDbAuthService.AccessTokenKey, "access-1"),
            new KeyValuePair<string, string?>(NamaDbAuthService.RefreshTokenKey, "revoked"),
        ]);
        var (_, auth, _) = Build(Enabled(), (_, _) => Json("", HttpStatusCode.Unauthorized));

        Assert.False(await auth.RefreshAsync());
        // Keeping a dead token would make every later call spend a pointless round trip.
        Assert.False(auth.IsLinked);
        Assert.Empty(Tokens.Load());
    }

    [Fact]
    public async Task Refreshing_without_a_stored_token_fails_without_calling_the_server()
    {
        var (_, auth, handler) = Build(Enabled(), (_, _) => Json(""));

        Assert.False(await auth.RefreshAsync());
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Signing_out_forgets_the_tokens_even_when_NamaDb_is_unreachable()
    {
        Tokens.SetMany([
            new KeyValuePair<string, string?>(NamaDbAuthService.AccessTokenKey, "access-1"),
            new KeyValuePair<string, string?>(NamaDbAuthService.RefreshTokenKey, "refresh-1"),
        ]);
        var (_, auth, _) = Build(Enabled(), (_, _) => throw new HttpRequestException("offline"));

        await auth.SignOutAsync();

        Assert.False(auth.IsLinked);
        Assert.Empty(Tokens.Load());
    }
}
