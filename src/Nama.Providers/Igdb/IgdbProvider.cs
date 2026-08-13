using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nama.Core.Models;
using Nama.Core.Providers;
using Nama.Storage;

namespace Nama.Providers.Igdb;

/// <summary>
/// IGDB, reached through Twitch's OAuth client-credentials flow.
///
/// This provider is off unless the user supplies a client id and secret. It exists
/// mainly to prove the provider abstraction holds: adding it required no changes to
/// identification, artwork aggregation or the UI.
/// </summary>
public sealed class IgdbProvider(
    HttpClient httpClient,
    SearchCache cache,
    Func<(string? ClientId, string? ClientSecret)> credentialAccessor)
    : IGameProvider, IArtworkProvider
{
    private const string ApiBase = "https://api.igdb.com/v4";

    private readonly SemaphoreSlim _tokenGate = new(1, 1);
    private string? _accessToken;
    private DateTimeOffset _tokenExpiry = DateTimeOffset.MinValue;

    public string Id => "igdb";
    public string DisplayName => "IGDB";
    public int Priority => 40;

    public bool IsUserEnabled { get; set; } = true;

    public bool IsEnabled
    {
        get
        {
            var (clientId, clientSecret) = credentialAccessor();
            return IsUserEnabled && !string.IsNullOrWhiteSpace(clientId) && !string.IsNullOrWhiteSpace(clientSecret);
        }
    }

    public IReadOnlyCollection<ArtworkType> SupportedTypes { get; } =
    [
        ArtworkType.Cover, ArtworkType.Background,
    ];

    public async Task<IReadOnlyList<Game>> SearchAsync(string query, CancellationToken ct = default)
    {
        if (!IsEnabled || string.IsNullOrWhiteSpace(query)) return [];

        var body =
            $"search \"{Escape(query)}\"; " +
            "fields name, alternative_names.name, first_release_date, summary, " +
            "involved_companies.company.name, involved_companies.developer, involved_companies.publisher, " +
            "cover.image_id, platforms.name; " +
            "limit 12;";

        var results = await cache.GetOrAddAsync(
            Id,
            query,
            token => PostAsync<List<IgdbGame>>("games", body, token),
            ct).ConfigureAwait(false);

        if (results is not { Count: > 0 }) return [];

        return results
            .Where(g => !string.IsNullOrWhiteSpace(g.Name))
            .Select(ToGame)
            .ToList();
    }

    private Game ToGame(IgdbGame game) => new()
    {
        CanonicalName = game.Name!.Trim(),
        DisplayName = game.Name!.Trim(),
        Aliases = game.AlternativeNames?
            .Select(a => a.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n!)
            .ToList() ?? [],
        ReleaseDate = game.FirstReleaseDate is > 0
            ? DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(game.FirstReleaseDate.Value).UtcDateTime)
            : null,
        Developer = game.InvolvedCompanies?.FirstOrDefault(c => c.Developer)?.Company?.Name,
        Publisher = game.InvolvedCompanies?.FirstOrDefault(c => c.Publisher)?.Company?.Name,
        Platforms = game.Platforms?.Select(p => p.Name).Where(n => n is not null).Select(n => n!).ToList() ?? [],
        Summary = game.Summary,
        PreviewImageUrl = ImageUrl(game.Cover?.ImageId, "t_cover_big"),
        SourceIds = [new GameSourceId(Id, game.Id.ToString())],
    };

    public async Task<IReadOnlyList<Artwork>> GetArtworkAsync(
        Game game,
        IReadOnlyCollection<ArtworkType> types,
        CancellationToken ct = default)
    {
        if (!IsEnabled) return [];

        var igdbId = game.SourceFor(Id)?.Id;
        if (igdbId is null)
        {
            var matches = await SearchAsync(game.CanonicalName, ct).ConfigureAwait(false);
            igdbId = matches.FirstOrDefault()?.SourceFor(Id)?.Id;
            if (igdbId is null) return [];
        }

        var body =
            $"fields cover.image_id, artworks.image_id, screenshots.image_id; where id = {igdbId}; limit 1;";

        var results = await cache.GetOrAddAsync(
            $"{Id}-art",
            igdbId,
            token => PostAsync<List<IgdbGame>>("games", body, token),
            ct).ConfigureAwait(false);

        var result = results?.FirstOrDefault();
        if (result is null) return [];

        var artwork = new List<Artwork>();

        if (types.Contains(ArtworkType.Cover) && result.Cover?.ImageId is { } coverId)
        {
            artwork.Add(new Artwork
            {
                Id = $"igdb-cover-{coverId}",
                Type = ArtworkType.Cover,
                Url = ImageUrl(coverId, "t_cover_big_2x")!,
                ThumbnailUrl = ImageUrl(coverId, "t_cover_big"),
                Source = DisplayName,
                Width = 528,
                Height = 704,
                Score = 0.9,
                Author = "Official",
                Style = "official",
            });
        }

        if (types.Contains(ArtworkType.Background))
        {
            var backgrounds = (result.Artworks ?? []).Concat(result.Screenshots ?? []);
            var index = 0;

            foreach (var image in backgrounds.Take(24))
            {
                if (image.ImageId is null) continue;

                artwork.Add(new Artwork
                {
                    Id = $"igdb-bg-{image.ImageId}",
                    Type = ArtworkType.Background,
                    Url = ImageUrl(image.ImageId, "t_1080p")!,
                    ThumbnailUrl = ImageUrl(image.ImageId, "t_screenshot_med"),
                    Source = DisplayName,
                    Width = 1920,
                    Height = 1080,
                    Score = index++ < 4 ? 0.5 : 0.3,
                    Style = "artwork",
                });
            }
        }

        return artwork;
    }

    private static string? ImageUrl(string? imageId, string size) =>
        string.IsNullOrWhiteSpace(imageId) ? null : $"https://images.igdb.com/igdb/image/upload/{size}/{imageId}.jpg";

    /// <summary>IGDB queries are a bespoke text language, so quotes in the term must be escaped.</summary>
    private static string Escape(string value) => value.Replace("\\", string.Empty).Replace("\"", "\\\"");

    private async Task<T?> PostAsync<T>(string endpoint, string body, CancellationToken ct)
    {
        var token = await GetAccessTokenAsync(ct).ConfigureAwait(false);
        if (token is null) return default;

        var (clientId, _) = credentialAccessor();

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiBase}/{endpoint}")
        {
            Content = new StringContent(body, Encoding.UTF8, "text/plain"),
        };
        request.Headers.Add("Client-ID", clientId);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await httpClient.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return default;

        try
        {
            return await response.Content
                .ReadFromJsonAsync<T>(ProviderHttp.JsonOptions, ct)
                .ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    /// <summary>
    /// Fetches and caches a Twitch app access token. Tokens last ~60 days, so this
    /// normally runs once per session.
    /// </summary>
    private async Task<string?> GetAccessTokenAsync(CancellationToken ct)
    {
        if (_accessToken is not null && DateTimeOffset.UtcNow < _tokenExpiry) return _accessToken;

        await _tokenGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_accessToken is not null && DateTimeOffset.UtcNow < _tokenExpiry) return _accessToken;

            var (clientId, clientSecret) = credentialAccessor();
            if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret)) return null;

            var url = "https://id.twitch.tv/oauth2/token" +
                      $"?client_id={Uri.EscapeDataString(clientId)}" +
                      $"&client_secret={Uri.EscapeDataString(clientSecret)}" +
                      "&grant_type=client_credentials";

            using var response = await httpClient.PostAsync(url, content: null, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            var token = await response.Content
                .ReadFromJsonAsync<TwitchToken>(ProviderHttp.JsonOptions, ct)
                .ConfigureAwait(false);

            if (token?.AccessToken is null) return null;

            _accessToken = token.AccessToken;
            // Renew an hour early so a long session never fails on an expired token.
            _tokenExpiry = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, token.ExpiresIn - 3600));

            return _accessToken;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            return null;
        }
        finally
        {
            _tokenGate.Release();
        }
    }

    private sealed class TwitchToken
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; set; }

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; }
    }

    private sealed class IgdbGame
    {
        public long Id { get; set; }
        public string? Name { get; set; }
        public string? Summary { get; set; }

        [JsonPropertyName("first_release_date")]
        public long? FirstReleaseDate { get; set; }

        [JsonPropertyName("alternative_names")]
        public List<IgdbNamed>? AlternativeNames { get; set; }

        [JsonPropertyName("involved_companies")]
        public List<IgdbInvolvedCompany>? InvolvedCompanies { get; set; }

        public List<IgdbNamed>? Platforms { get; set; }
        public IgdbImage? Cover { get; set; }
        public List<IgdbImage>? Artworks { get; set; }
        public List<IgdbImage>? Screenshots { get; set; }
    }

    private sealed class IgdbNamed
    {
        public string? Name { get; set; }
    }

    private sealed class IgdbInvolvedCompany
    {
        public IgdbNamed? Company { get; set; }
        public bool Developer { get; set; }
        public bool Publisher { get; set; }
    }

    private sealed class IgdbImage
    {
        [JsonPropertyName("image_id")]
        public string? ImageId { get; set; }
    }
}
