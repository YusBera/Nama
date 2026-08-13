using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Nama.Core.Models;
using Nama.Core.Providers;
using Nama.Storage;

namespace Nama.Providers.NamaDb;

/// <summary>Thrown when an action needs a linked NamaDB identity and there is none.</summary>
public sealed class NamaDbNotLinkedException(string message) : Exception(message);

/// <summary>First-party NamaDB artwork provider. Uploading remains on the website.</summary>
public sealed class NamaDbProvider(HttpClient httpClient, Func<NamaSettings> settings, ProtectedTokenStore tokens, NamaDbAuthService auth)
    : IArtworkProvider, IArtworkVotingProvider
{
    public string Id => "namadb";
    public string DisplayName => "NamaDB";
    public bool IsEnabled => settings().NamaDbEnabled && settings().NamaDbAdultAcceptedAt is not null;
    public int Priority => 5;
    public IReadOnlyCollection<ArtworkType> SupportedTypes { get; } = ArtworkTypeInfo.SteamApplicable;

    public async Task<IReadOnlyList<Artwork>> GetArtworkAsync(Game game, IReadOnlyCollection<ArtworkType> types, CancellationToken ct = default)
    {
        if (!IsEnabled) return [];
        var gameId = await ResolveGameAsync(game, ct).ConfigureAwait(false);
        if (gameId is null) return [];
        var results = new List<Artwork>();
        foreach (var type in types)
        {
            var apiType = ToApiType(type);
            if (apiType is null) continue;
            var url = $"{BaseUrl}/v1/games/{Uri.EscapeDataString(gameId)}/artwork?type={apiType}";
            using var response = await SendAuthorizedAsync(() => new HttpRequestMessage(HttpMethod.Get, url), ct).ConfigureAwait(false);
            // Browsing works signed out, so an expired identity degrades to anonymous results
            // rather than surfacing an error the user cannot act on mid-search.
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized) continue;
            response.EnsureSuccessStatusCode();
            var payload = await response.Content.ReadFromJsonAsync<ArtworkListDto>(ProviderHttp.JsonOptions, ct).ConfigureAwait(false);
            if (payload?.Items is null) continue;
            results.AddRange(payload.Items.Select(item => Map(item, type)));
        }
        return results;
    }

    public async Task<ArtworkVoteResult> VoteAsync(string artworkId, ArtworkVoteValue value, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/v1/artwork/{Uri.EscapeDataString(artworkId)}/vote";
        using var response = await SendAuthorizedAsync(
            () => new HttpRequestMessage(HttpMethod.Put, url) { Content = JsonContent.Create(new { value = (int)value }) }, ct).ConfigureAwait(false);
        // Unlike browsing, a vote cannot fall back to anonymous: say so instead of silently dropping it.
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new NamaDbNotLinkedException("This Nama installation is no longer linked to a NamaDB account.");
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<VoteDto>(ProviderHttp.JsonOptions, ct).ConfigureAwait(false)
            ?? throw new HttpRequestException("NamaDB returned an empty vote response.");
        return new ArtworkVoteResult(result.Upvotes, result.Downvotes, result.RankScore, (ArtworkVoteValue)result.CurrentUserVote);
    }

    private async Task<string?> ResolveGameAsync(Game game, CancellationToken ct)
    {
        foreach (var source in game.SourceIds)
        {
            using var response = await httpClient.GetAsync($"{BaseUrl}/v1/games/resolve?provider={Uri.EscapeDataString(source.Provider)}&externalId={Uri.EscapeDataString(source.Id)}", ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound) continue;
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<GameDto>(ProviderHttp.JsonOptions, ct).ConfigureAwait(false))?.Id;
        }
        return null;
    }

    private string BaseUrl => settings().NamaDbApiBaseUrl.TrimEnd('/');

    /// <summary>
    /// Sends an authorized request, renewing the access token once if the server rejects it.
    /// The factory builds a fresh <see cref="HttpRequestMessage"/> per attempt because a sent
    /// request cannot be reused.
    /// </summary>
    private async Task<HttpResponseMessage> SendAuthorizedAsync(Func<HttpRequestMessage> factory, CancellationToken ct)
    {
        var response = await SendOnceAsync(factory(), ct).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Unauthorized) return response;

        response.Dispose();
        if (!await auth.RefreshAsync(ct).ConfigureAwait(false))
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);

        return await SendOnceAsync(factory(), ct).ConfigureAwait(false);
    }

    private Task<HttpResponseMessage> SendOnceAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (tokens.Load().TryGetValue(NamaDbAuthService.AccessTokenKey, out var token))
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return httpClient.SendAsync(request, ct);
    }

    private static Artwork Map(ArtworkDto item, ArtworkType type) => new()
    {
        Id = item.Id, Type = type, Url = item.ImageUrl, ThumbnailUrl = item.ThumbnailUrl,
        Source = "NamaDB", Width = item.Width, Height = item.Height,
        Score = item.RankScore, ProviderRankScore = item.RankScore,
        Upvotes = item.Upvotes, Downvotes = item.Downvotes,
        CurrentUserVote = item.CurrentUserVote, CanVote = item.CanVote,
        Author = item.Author, DetailsUrl = item.DetailsUrl,
    };

    private static string? ToApiType(ArtworkType type) => type switch
    {
        ArtworkType.Cover => "libraryCapsule", ArtworkType.Grid => "libraryHeader",
        ArtworkType.Hero => "libraryHero", ArtworkType.Logo => "libraryLogo",
        ArtworkType.Icon => "icon", _ => null,
    };

    private sealed record GameDto([property: JsonPropertyName("id")] string Id);
    private sealed record ArtworkListDto([property: JsonPropertyName("items")] List<ArtworkDto> Items);
    private sealed record ArtworkDto(string Id, string ImageUrl, string ThumbnailUrl, int Width, int Height, int Upvotes, int Downvotes, double RankScore, int CurrentUserVote, bool CanVote, string? Author, string DetailsUrl);
    private sealed record VoteDto(int Upvotes, int Downvotes, double RankScore, int CurrentUserVote);
}
