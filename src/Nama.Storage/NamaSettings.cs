using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nama.Storage;

/// <summary>
/// User-configurable state. Deliberately small — Nama is opinionated, so almost
/// everything that could be a setting is instead a sensible default.
/// </summary>
public sealed class NamaSettings
{
    /// <summary>NamaDB is intentionally opt-in because its catalog may contain adult artwork.</summary>
    public bool NamaDbEnabled { get; set; }

    /// <summary>UTC timestamp of the explicit adult-content confirmation.</summary>
    public DateTimeOffset? NamaDbAdultAcceptedAt { get; set; }

    /// <summary>Opaque first-party installation identifier issued by NamaDB.</summary>
    public string? NamaDbInstallationId { get; set; }

    /// <summary>
    /// Base URL of the NamaDB instance to talk to. Empty by default, which switches the provider
    /// off: there is no hosted NamaDB service, so shipping a default host would point every
    /// installation at a domain this project does not control. Whoever did control it could then
    /// choose the page Nama opens for Steam sign in. Set this only to an instance you trust.
    /// </summary>
    public string NamaDbApiBaseUrl { get; set; } = "";

    /// <summary>
    /// SteamGridDB API key. Without it the SteamGridDB provider disables itself and Nama
    /// falls back to Steam and VNDB artwork.
    /// </summary>
    public string? SteamGridDbApiKey { get; set; }

    /// <summary>IGDB (Twitch) client credentials. Optional; the provider stays off without them.</summary>
    public string? IgdbClientId { get; set; }
    public string? IgdbClientSecret { get; set; }

    /// <summary>Provider ids the user has explicitly switched off.</summary>
    public List<string> DisabledProviders { get; set; } = [];

    /// <summary>Steam install path, when auto-detection failed and the user picked it manually.</summary>
    public string? SteamPathOverride { get; set; }

    /// <summary>Steam user id (the 32-bit account id under <c>userdata</c>) to write shortcuts for.</summary>
    public string? PreferredSteamUserId { get; set; }

    /// <summary>How long cached search results stay valid.</summary>
    public int SearchCacheHours { get; set; } = 72;

    /// <summary>Debounce for the search box, in milliseconds.</summary>
    public int SearchDebounceMs { get; set; } = 300;

    /// <summary>Whether Nama writes a backup of shortcuts.vdf before modifying it.</summary>
    public bool BackupShortcutsFile { get; set; } = true;

    public bool IsProviderEnabled(string providerId) =>
        !DisabledProviders.Contains(providerId, StringComparer.OrdinalIgnoreCase);

    public void SetProviderEnabled(string providerId, bool enabled)
    {
        DisabledProviders.RemoveAll(p => string.Equals(p, providerId, StringComparison.OrdinalIgnoreCase));
        if (!enabled) DisabledProviders.Add(providerId);
    }
}

/// <summary>Loads and saves <see cref="NamaSettings"/>, tolerating a corrupt or missing file.</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _path;
    private readonly Lock _gate = new();

    public SettingsStore(string? path = null) => _path = path ?? NamaPaths.SettingsFile;

    /// <summary>Reads settings from disk. A missing or unreadable file yields defaults.</summary>
    public NamaSettings Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_path)) return new NamaSettings();
                var json = File.ReadAllText(_path);
                return JsonSerializer.Deserialize<NamaSettings>(json, Options) ?? new NamaSettings();
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // A damaged settings file must not stop the app from starting.
                return new NamaSettings();
            }
        }
    }

    /// <summary>Writes settings atomically so a crash mid-write cannot corrupt the file.</summary>
    public void Save(NamaSettings settings)
    {
        lock (_gate)
        {
            NamaPaths.Ensure(Path.GetDirectoryName(_path)!);

            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, Options));
            File.Move(temp, _path, overwrite: true);
        }
    }
}
