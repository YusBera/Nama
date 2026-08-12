using System.Text;

namespace Nama.Steam;

/// <summary>
/// Generates the synthetic app id for a non-Steam shortcut Nama creates.
///
/// <para><b>What the app id actually is.</b> Artwork in <c>config\grid</c> is named after
/// the <c>appid</c> field stored in <c>shortcuts.vdf</c> — that relationship holds and is
/// the one that matters. What is <em>not</em> true is that the id is derivable: measured
/// against a real library, none of the CRC32 formulations (quoted/unquoted exe, either
/// concatenation order, with or without the high bit) reproduces the ids Steam assigned
/// to its own shortcuts. Modern Steam picks them by some other means.</para>
///
/// <para><b>The rule that follows.</b> For an entry that already exists, always read the
/// stored <c>appid</c> and never recompute it — a recomputed id names artwork files Steam
/// will never look at, so the images land on disk and simply do nothing.
/// <see cref="Compute"/> is for <em>new</em> entries only, where Nama chooses the id and
/// writes it into both the field and the artwork filenames, making the two agree by
/// construction.</para>
///
/// <para><b>Why still CRC32.</b> Any value with the high bit set would work, but this
/// formulation is what Steam ROM Manager, Playnite and steamgrid use, so artwork stays
/// interoperable with them. It is also deterministic: re-adding the same game reproduces
/// the same id and finds its existing artwork.</para>
///
/// <para>The id depends on the display name, so it can only be computed once the final
/// Steam name is settled. Renaming a shortcut changes its id and orphans its artwork.</para>
/// </summary>
public static class SteamAppId
{
    /// <summary>
    /// Computes an id for a <em>new</em> shortcut. <paramref name="exeAsStored"/> must be
    /// the Exe field exactly as it will be written, quotes included — a real library
    /// contains both quoted and unquoted entries, and the two produce different ids.
    /// </summary>
    public static uint Compute(string exeAsStored, string appName)
    {
        var payload = Encoding.UTF8.GetBytes(exeAsStored + appName);
        return Crc32.Compute(payload) | 0x80000000u;
    }

    /// <summary>Reinterprets the unsigned id as the signed int32 stored in the <c>appid</c> field.</summary>
    public static int ToShortcutField(uint appId) => unchecked((int)appId);

    /// <summary>Reinterprets a stored <c>appid</c> field as the unsigned id used for filenames.</summary>
    public static uint FromShortcutField(int field) => unchecked((uint)field);

    /// <summary>Wraps a bare path in quotes the way Steam stores it.</summary>
    public static string Quote(string path) =>
        path.StartsWith('"') && path.EndsWith('"') ? path : $"\"{path}\"";
}

/// <summary>
/// Standard CRC-32 (IEEE 802.3, reflected, polynomial 0xEDB88320). Implemented here rather
/// than taken as a dependency — it is fifteen lines and this is the only place it is needed.
/// </summary>
public static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFFu;
    }

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var entry = i;
            for (var bit = 0; bit < 8; bit++)
            {
                entry = (entry & 1) != 0 ? 0xEDB88320u ^ (entry >> 1) : entry >> 1;
            }

            table[i] = entry;
        }

        return table;
    }
}
