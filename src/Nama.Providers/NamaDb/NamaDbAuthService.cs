using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Nama.Storage;

namespace Nama.Providers.NamaDb;

/// <summary>The code pair a user carries from Nama to the browser to link this installation.</summary>
/// <param name="DeviceCode">Opaque handle Nama polls with. Never shown to the user.</param>
/// <param name="UserCode">Short code the user checks against the page.</param>
/// <param name="VerificationUri">Page to open in the browser.</param>
/// <param name="ExpiresIn">Seconds before the pair stops being accepted.</param>
public readonly record struct DeviceLink(string DeviceCode, string UserCode, string VerificationUri, int ExpiresIn);

public enum DeviceLinkStatus
{
    /// <summary>The browser half has not completed yet. Keep polling.</summary>
    Pending,
    Linked,
    /// <summary>The code pair timed out or was already redeemed. Start over.</summary>
    Expired,
}

/// <summary>
/// Owns the NamaDB device-link handshake and the token lifecycle behind it.
///
/// Uploading stays on the website, so the desktop only ever needs a browsing and voting
/// identity. That is obtained without ever handling the user's Steam credentials: Nama asks
/// for a code pair, the user approves it in their browser, and Nama polls for the result.
/// </summary>
public sealed class NamaDbAuthService(HttpClient httpClient, Func<NamaSettings> settings, ProtectedTokenStore tokens)
{
    public const string AccessTokenKey = "namadb.access";
    public const string RefreshTokenKey = "namadb.refresh";

    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    /// <summary>True once a refresh token is on disk, meaning this installation has an identity.</summary>
    public bool IsLinked => tokens.Load().ContainsKey(RefreshTokenKey);

    /// <summary>Raised after the stored tokens change, so the UI can re-read <see cref="IsLinked"/>.</summary>
    public event EventHandler? LinkChanged;

    private string BaseUrl => settings().NamaDbApiBaseUrl.TrimEnd('/');

    /// <summary>Requests a code pair. The caller shows the user code and opens the verification page.</summary>
    public async Task<DeviceLink> StartAsync(CancellationToken ct = default)
    {
        using var response = await httpClient.PostAsync($"{BaseUrl}/v1/auth/device", content: null, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<DeviceDto>(ProviderHttp.JsonOptions, ct).ConfigureAwait(false)
            ?? throw new HttpRequestException("NamaDB returned an empty device-link response.");
        return new DeviceLink(payload.DeviceCode, payload.UserCode, payload.VerificationUri, payload.ExpiresIn);
    }

    /// <summary>
    /// Asks once whether the browser half has completed. The server hands the token pair over
    /// exactly once, so a successful poll must persist it before anything else can fail.
    /// </summary>
    public async Task<DeviceLinkStatus> PollOnceAsync(string deviceCode, CancellationToken ct = default)
    {
        using var response = await httpClient.PostAsJsonAsync($"{BaseUrl}/v1/auth/device/token", new { deviceCode }, ProviderHttp.JsonOptions, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Accepted) return DeviceLinkStatus.Pending;
        // A code that has expired or was already redeemed reads as pending forever otherwise.
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone) return DeviceLinkStatus.Expired;
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<TokenDto>(ProviderHttp.JsonOptions, ct).ConfigureAwait(false);
        if (payload?.AccessToken is null || payload.RefreshToken is null) return DeviceLinkStatus.Pending;

        Store(payload.AccessToken, payload.RefreshToken);
        return DeviceLinkStatus.Linked;
    }

    /// <summary>
    /// Polls until the link completes, the codes expire, or the caller cancels.
    /// </summary>
    public async Task<DeviceLinkStatus> WaitForLinkAsync(DeviceLink link, CancellationToken ct = default)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(Math.Max(30, link.ExpiresIn));
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var status = await PollOnceAsync(link.DeviceCode, ct).ConfigureAwait(false);
            if (status != DeviceLinkStatus.Pending) return status;
            await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
        }
        return DeviceLinkStatus.Expired;
    }

    /// <summary>
    /// Exchanges the stored refresh token for a new pair. Returns false when the installation
    /// has to be linked again.
    ///
    /// The server revokes an entire token family if a rotated token is ever presented twice, so
    /// concurrent refreshes would log the user out. The gate serialises them, and callers that
    /// queued behind a successful refresh reuse its result rather than spending the new token.
    /// </summary>
    public async Task<bool> RefreshAsync(CancellationToken ct = default)
    {
        var before = tokens.Load().GetValueOrDefault(AccessTokenKey);
        await _refreshGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var current = tokens.Load();
            if (!current.TryGetValue(RefreshTokenKey, out var refreshToken)) return false;
            // Someone else refreshed while this call waited; their tokens are already stored.
            if (before is not null && current.GetValueOrDefault(AccessTokenKey) != before) return true;

            using var response = await httpClient.PostAsJsonAsync($"{BaseUrl}/v1/auth/refresh", new { refreshToken }, ProviderHttp.JsonOptions, ct).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                // The family is gone: revoked, expired, or the account was sanctioned.
                Clear();
                return false;
            }
            response.EnsureSuccessStatusCode();

            var payload = await response.Content.ReadFromJsonAsync<TokenDto>(ProviderHttp.JsonOptions, ct).ConfigureAwait(false);
            if (payload?.AccessToken is null || payload.RefreshToken is null) return false;

            Store(payload.AccessToken, payload.RefreshToken);
            return true;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    /// <summary>Revokes the token family server-side, then forgets it locally either way.</summary>
    public async Task SignOutAsync(CancellationToken ct = default)
    {
        var refreshToken = tokens.Load().GetValueOrDefault(RefreshTokenKey);
        if (refreshToken is not null)
        {
            try
            {
                using var response = await httpClient.PostAsJsonAsync($"{BaseUrl}/v1/auth/logout", new { refreshToken }, ProviderHttp.JsonOptions, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // Signing out locally must succeed even when NamaDB is unreachable.
            }
        }
        Clear();
    }

    private void Store(string accessToken, string refreshToken)
    {
        tokens.SetMany([
            new KeyValuePair<string, string?>(AccessTokenKey, accessToken),
            new KeyValuePair<string, string?>(RefreshTokenKey, refreshToken),
        ]);
        LinkChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Clear()
    {
        tokens.SetMany([
            new KeyValuePair<string, string?>(AccessTokenKey, null),
            new KeyValuePair<string, string?>(RefreshTokenKey, null),
        ]);
        LinkChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed record DeviceDto(string DeviceCode, string UserCode, string VerificationUri, int ExpiresIn);
    private sealed record TokenDto(
        [property: JsonPropertyName("accessToken")] string? AccessToken,
        [property: JsonPropertyName("refreshToken")] string? RefreshToken,
        [property: JsonPropertyName("expiresIn")] int ExpiresIn);
}
