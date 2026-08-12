using System.Buffers.Binary;
using System.Text;

namespace Nama.Steam.Vdf;

/// <summary>Thrown when a VDF file cannot be parsed. Never thrown for a well-formed file.</summary>
public sealed class VdfFormatException(string message) : Exception(message);

/// <summary>
/// Codec for Valve's binary VDF (as used by <c>shortcuts.vdf</c>).
/// <para>
/// Format: each entry is a type byte, a null-terminated UTF-8 key, then the value.
/// <c>0x00</c> opens a nested map (terminated by <c>0x08</c>), <c>0x01</c> is a
/// null-terminated UTF-8 string, <c>0x02</c> a little-endian int32. The file's root is an
/// implicit map, so a shortcuts file ends with one <c>0x08</c> per open map plus one for
/// the root.
/// </para>
/// <para>
/// Round-tripping is a correctness requirement, not a nicety: Nama rewrites a file that
/// holds the user's existing shortcuts, so anything it does not understand must survive
/// untouched. <see cref="RoundTrips"/> is the runtime guard built on that property.
/// </para>
/// </summary>
public static class BinaryVdf
{
    // Never throw on malformed input: we hand back U+FFFD and rely on the retained
    // original bytes to reproduce the file exactly.
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    /// <summary>Parses a binary VDF file into its implicit root map.</summary>
    public static VdfMap Read(ReadOnlySpan<byte> data)
    {
        var offset = 0;
        var root = ReadMap(data, ref offset, depth: 0);

        // A trailing 0x08 closes the implicit root. Anything else after it is unexpected.
        if (offset < data.Length && data[offset] == VdfType.End) offset++;

        return root;
    }

    public static VdfMap ReadFile(string path) => Read(File.ReadAllBytes(path));

    /// <summary>Serializes a map back to bytes, including the implicit root terminator.</summary>
    public static byte[] Write(VdfMap root)
    {
        var buffer = new MemoryStream(4096);
        WriteMapBody(buffer, root);
        buffer.WriteByte(VdfType.End); // closes the implicit root
        return buffer.ToArray();
    }

    /// <summary>
    /// True when <paramref name="original"/> parses and re-serializes to exactly the same
    /// bytes.
    /// <para>
    /// This is the gate on the entire write path. If it returns false, Nama has met a
    /// file it does not fully understand and must refuse to write rather than risk
    /// discarding part of it. Corrupting a file would require the codec to be wrong
    /// <em>and</em> wrong in a way that re-serializes byte-identically.
    /// </para>
    /// </summary>
    public static bool RoundTrips(ReadOnlySpan<byte> original, out VdfMap? parsed)
    {
        try
        {
            parsed = Read(original);
        }
        catch (VdfFormatException)
        {
            parsed = null;
            return false;
        }

        return Write(parsed).AsSpan().SequenceEqual(original);
    }

    // --- reading ---------------------------------------------------------------------

    private static VdfMap ReadMap(ReadOnlySpan<byte> data, ref int offset, int depth)
    {
        // Guards against a malformed file causing unbounded recursion.
        if (depth > 32) throw new VdfFormatException($"VDF nesting deeper than 32 levels at offset {offset}.");

        var map = new VdfMap();

        while (offset < data.Length)
        {
            var type = data[offset];
            if (type == VdfType.End)
            {
                offset++;
                return map;
            }

            offset++;
            var keyBytes = ReadNullTerminated(data, ref offset);
            var key = Utf8.GetString(keyBytes);

            VdfNode value = type switch
            {
                VdfType.Map => ReadMap(data, ref offset, depth + 1),
                VdfType.String or VdfType.WideString => ReadString(data, ref offset),
                VdfType.Int32 => new VdfInt32(ReadInt32(data, ref offset)),
                VdfType.UInt64 => new VdfUInt64(ReadUInt64(data, ref offset)),
                VdfType.Float32 => new VdfFloat32(BitConverter.Int32BitsToSingle(ReadInt32(data, ref offset))),
                _ => throw new VdfFormatException(
                    $"Unknown VDF type byte 0x{type:x2} for key '{key}' at offset {offset}."),
            };

            map.AddRaw(new VdfEntry(key, value, keyBytes));
        }

        // Running out of data mid-map is tolerated at the root (the file simply ended)
        // but is corruption anywhere deeper.
        if (depth > 0) throw new VdfFormatException("VDF data ended inside a nested map.");

        return map;
    }

    private static VdfString ReadString(ReadOnlySpan<byte> data, ref int offset)
    {
        var bytes = ReadNullTerminated(data, ref offset);
        return new VdfString(Utf8.GetString(bytes), bytes);
    }

    private static byte[] ReadNullTerminated(ReadOnlySpan<byte> data, ref int offset)
    {
        var start = offset;
        while (offset < data.Length && data[offset] != 0x00) offset++;

        if (offset >= data.Length) throw new VdfFormatException($"Unterminated string starting at offset {start}.");

        var bytes = data[start..offset].ToArray();
        offset++; // consume the terminator
        return bytes;
    }

    private static int ReadInt32(ReadOnlySpan<byte> data, ref int offset)
    {
        if (offset + 4 > data.Length) throw new VdfFormatException($"Truncated int32 at offset {offset}.");

        var value = BinaryPrimitives.ReadInt32LittleEndian(data[offset..]);
        offset += 4;
        return value;
    }

    private static ulong ReadUInt64(ReadOnlySpan<byte> data, ref int offset)
    {
        if (offset + 8 > data.Length) throw new VdfFormatException($"Truncated uint64 at offset {offset}.");

        var value = BinaryPrimitives.ReadUInt64LittleEndian(data[offset..]);
        offset += 8;
        return value;
    }

    // --- writing ---------------------------------------------------------------------

    private static void WriteMapBody(Stream output, VdfMap map)
    {
        // One buffer for the whole map rather than one per entry.
        Span<byte> scratch = stackalloc byte[8];

        foreach (var entry in map.Entries)
        {
            switch (entry.Value)
            {
                case VdfMap child:
                    output.WriteByte(VdfType.Map);
                    WriteKey(output, entry);
                    WriteMapBody(output, child);
                    output.WriteByte(VdfType.End);
                    break;

                case VdfString text:
                    output.WriteByte(VdfType.String);
                    WriteKey(output, entry);
                    WriteNullTerminated(output, text.OriginalBytes ?? Utf8.GetBytes(text.Value));
                    break;

                case VdfInt32 number:
                    output.WriteByte(VdfType.Int32);
                    WriteKey(output, entry);
                    BinaryPrimitives.WriteInt32LittleEndian(scratch, number.Value);
                    output.Write(scratch[..4]);
                    break;

                case VdfUInt64 large:
                    output.WriteByte(VdfType.UInt64);
                    WriteKey(output, entry);
                    BinaryPrimitives.WriteUInt64LittleEndian(scratch, large.Value);
                    output.Write(scratch[..8]);
                    break;

                case VdfFloat32 real:
                    output.WriteByte(VdfType.Float32);
                    WriteKey(output, entry);
                    BinaryPrimitives.WriteInt32LittleEndian(scratch, BitConverter.SingleToInt32Bits(real.Value));
                    output.Write(scratch[..4]);
                    break;

                default:
                    throw new VdfFormatException($"Cannot serialize node type {entry.Value.GetType().Name}.");
            }
        }
    }

    private static void WriteKey(Stream output, VdfEntry entry) =>
        WriteNullTerminated(output, entry.OriginalKeyBytes ?? Utf8.GetBytes(entry.Key));

    private static void WriteNullTerminated(Stream output, byte[] bytes)
    {
        output.Write(bytes);
        output.WriteByte(0x00);
    }
}
