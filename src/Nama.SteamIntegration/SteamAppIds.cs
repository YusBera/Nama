using System.Text;

namespace Nama.SteamIntegration;

/// <summary>
/// Derives the identifiers Steam uses for non-Steam shortcuts.
///
/// Steam does not assign these — it computes them from the shortcut's target and name,
/// which is why artwork files can be written before Steam has ever seen the shortcut.
/// The algorithm must match Steam's exactly or the artwork lands under a filename Steam
/// never looks for.
/// </summary>
public static class SteamAppIds
{
    /// <summary>
    /// The 32-bit id used for artwork file names in <c>userdata\&lt;user&gt;\config\grid</c>
    /// and stored (as a signed int) in the shortcut's <c>appid</c> field.
    /// </summary>
    /// <param name="exePath">The executable path exactly as written to the shortcut's <c>Exe</c> field, including quotes.</param>
    /// <param name="appName">The shortcut's display name.</param>
    public static uint ComputeShortcutAppId(string exePath, string appName)
    {
        var input = Encoding.UTF8.GetBytes(exePath + appName);
        return Crc32(input) | 0x8000_0000u;
    }

    /// <summary>
    /// The same value as <see cref="ComputeShortcutAppId"/>, reinterpreted as the signed
    /// int32 that the binary VDF <c>appid</c> field stores.
    /// </summary>
    public static int ComputeShortcutAppIdSigned(string exePath, string appName) =>
        unchecked((int)ComputeShortcutAppId(exePath, appName));

    /// <summary>
    /// The 64-bit id older Steam builds use in shortcut URLs
    /// (<c>steam://rungameid/&lt;id&gt;</c>).
    /// </summary>
    public static ulong ComputeLegacyGameId(string exePath, string appName) =>
        ((ulong)ComputeShortcutAppId(exePath, appName) << 32) | 0x0200_0000ul;

    /// <summary>Converts a signed <c>appid</c> field value back to the unsigned artwork id.</summary>
    public static uint ToUnsigned(int signedAppId) => unchecked((uint)signedAppId);

    /// <summary>
    /// Steam stores the shortcut target quoted. Nama matches that so the computed id
    /// agrees with what Steam derives from the same file.
    /// </summary>
    public static string QuotePath(string path)
    {
        var trimmed = path.Trim();
        if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"') return trimmed;
        return $"\"{trimmed}\"";
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <summary>Standard CRC-32 (IEEE 802.3, polynomial 0xEDB88320) — the variant zlib and Steam use.</summary>
    public static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFF_FFFFu;

        foreach (var b in data)
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);

        return crc ^ 0xFFFF_FFFFu;
    }

    private static uint[] BuildCrcTable()
    {
        const uint polynomial = 0xEDB8_8320u;
        var table = new uint[256];

        for (var i = 0u; i < 256u; i++)
        {
            var entry = i;
            for (var bit = 0; bit < 8; bit++)
                entry = (entry & 1) != 0 ? (entry >> 1) ^ polynomial : entry >> 1;
            table[i] = entry;
        }

        return table;
    }
}
