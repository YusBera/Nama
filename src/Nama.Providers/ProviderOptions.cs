namespace Nama.Providers;

/// <summary>Configuration shared by every provider.</summary>
public sealed class ProviderOptions
{
    /// <summary>
    /// SteamGridDB personal API key. Null or blank makes that provider report itself
    /// unavailable; Steam and VNDB need no key and keep working regardless.
    /// </summary>
    public string? SteamGridDbApiKey { get; set; }

    public bool EnableDlsite { get; set; } = true;

    public bool EnableVndb { get; set; } = true;

    /// <summary>
    /// Per-request timeout. Short on purpose — a slow provider must not hold up a flow
    /// that is supposed to take a couple of seconds.
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Identifies Nama to providers. VNDB in particular asks for a real user agent.</summary>
    public string UserAgent { get; set; } = "Nama/0.1 (local game to Steam library utility)";

    /// <summary>How long provider responses stay cached.</summary>
    public TimeSpan CacheTtl { get; set; } = TimeSpan.FromDays(7);

    /// <summary>Maximum results requested from each provider per search.</summary>
    public int MaxResults { get; set; } = 10;

    /// <summary>
    /// How many Steam results get a follow-up <c>appdetails</c> call for developer and
    /// release date. That endpoint is rate limited (roughly 200 requests per 5 minutes),
    /// so this stays small — enough to label the results the user is actually choosing
    /// between, not the whole list.
    /// </summary>
    public int SteamEnrichmentLimit { get; set; } = 5;
}
