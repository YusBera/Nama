using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Nama.Storage;

/// <summary>Stores provider refresh tokens with Windows DPAPI, separate from settings.json.</summary>
public sealed class ProtectedTokenStore(string? path = null)
{
    private readonly string _path = path ?? NamaPaths.TokenFile;

    public IReadOnlyDictionary<string, string> Load()
    {
        try
        {
            if (!OperatingSystem.IsWindows() || !File.Exists(_path)) return new Dictionary<string, string>();
            var clear = ProtectedData.Unprotect(File.ReadAllBytes(_path), null, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(clear) ?? new Dictionary<string, string>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or JsonException)
        {
            return new Dictionary<string, string>();
        }
    }

    public void Set(string key, string? value) => SetMany([new KeyValuePair<string, string?>(key, value)]);

    /// <summary>
    /// Writes several tokens in one pass. Access and refresh tokens must land together —
    /// a half-written pair would leave the app holding a token it cannot renew.
    /// </summary>
    public void SetMany(IEnumerable<KeyValuePair<string, string?>> values)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Token storage requires Windows DPAPI.");
        var tokens = new Dictionary<string, string>(Load(), StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in values)
        {
            if (string.IsNullOrWhiteSpace(value)) tokens.Remove(key); else tokens[key] = value;
        }
        NamaPaths.Ensure(Path.GetDirectoryName(_path)!);
        var clear = JsonSerializer.SerializeToUtf8Bytes(tokens);
        var encrypted = ProtectedData.Protect(clear, null, DataProtectionScope.CurrentUser);
        var temp = _path + ".tmp";
        File.WriteAllBytes(temp, encrypted);
        File.Move(temp, _path, true);
    }
}
