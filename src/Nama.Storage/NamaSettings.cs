using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nama.Storage;

/// <summary>
/// User settings, persisted as JSON under <c>%APPDATA%\Nama</c>.
/// <para>
/// The SteamGridDB key is encrypted with DPAPI scoped to the current user, so the file is
/// useless if copied to another machine or account. It is deliberately never exposed as
/// plaintext on this type — <see cref="SteamGridDbApiKey"/> encrypts on set and decrypts
/// on get, and only the ciphertext is serialized.
/// </para>
/// </summary>
public sealed class NamaSettings
{
    /// <summary>Extra entropy, so a DPAPI blob from another app cannot be swapped in.</summary>
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Nama.Settings.v1");

    /// <summary>DPAPI ciphertext, base64. The only form written to disk.</summary>
    [JsonPropertyName("steamGridDbApiKeyProtected")]
    public string? SteamGridDbApiKeyProtected { get; set; }

    /// <summary>Which Steam account to operate on. Null means "most recently used".</summary>
    [JsonPropertyName("preferredSteamAccountId")]
    public uint? PreferredSteamAccountId { get; set; }

    /// <summary>Whether the Explorer context-menu entry is installed.</summary>
    [JsonPropertyName("contextMenuInstalled")]
    public bool ContextMenuInstalled { get; set; }

    /// <summary>Offer to close Steam automatically rather than asking every time.</summary>
    [JsonPropertyName("alwaysCloseSteam")]
    public bool AlwaysCloseSteam { get; set; }

    [JsonPropertyName("experimentalDlsiteEnabled")]
    public bool ExperimentalDlsiteEnabled { get; set; } = true;

    [JsonPropertyName("experimentalVndbEnabled")]
    public bool ExperimentalVndbEnabled { get; set; } = true;

    /// <summary>The plaintext key. Encrypts on assignment; never serialized.</summary>
    [JsonIgnore]
    public string? SteamGridDbApiKey
    {
        get => Unprotect(SteamGridDbApiKeyProtected);
        set => SteamGridDbApiKeyProtected = Protect(value);
    }

    public static string DirectoryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Nama");

    public static string FilePath => Path.Combine(DirectoryPath, "settings.json");

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Loads settings, returning defaults when the file is absent or unreadable. A corrupt
    /// settings file must never stop the app starting — the user can just re-enter a key.
    /// </summary>
    public static NamaSettings Load(string? path = null)
    {
        path ??= FilePath;

        try
        {
            if (!File.Exists(path)) return new NamaSettings();

            return JsonSerializer.Deserialize<NamaSettings>(File.ReadAllText(path), SerializerOptions)
                   ?? new NamaSettings();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return new NamaSettings();
        }
    }

    public void Save(string? path = null)
    {
        path = path ?? FilePath;
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        // Write-then-replace, so an interrupted save cannot leave a truncated file.
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, SerializerOptions));

        if (File.Exists(path)) File.Replace(temporary, path, destinationBackupFileName: null);
        else File.Move(temporary, path);
    }

    private static string? Protect(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        try
        {
            return Convert.ToBase64String(
                ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser));
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private static string? Unprotect(string? protectedValue)
    {
        if (string.IsNullOrWhiteSpace(protectedValue)) return null;

        try
        {
            return Encoding.UTF8.GetString(
                ProtectedData.Unprotect(Convert.FromBase64String(protectedValue), Entropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception e) when (e is CryptographicException or FormatException)
        {
            // Copied from another machine or account, or corrupt. Treat as "no key set".
            return null;
        }
    }
}
