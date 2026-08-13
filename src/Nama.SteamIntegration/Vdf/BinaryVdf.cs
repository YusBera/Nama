using System.Buffers.Binary;
using System.Text;

namespace Nama.SteamIntegration.Vdf;

/// <summary>
/// Reader/writer for Valve's binary KeyValues format, which is what
/// <c>shortcuts.vdf</c> uses.
///
/// Layout: each entry starts with a one-byte type tag, followed by a NUL-terminated
/// UTF-8 key, followed by the payload. Objects (tag 0x00) contain further entries and
/// are closed by a lone 0x08. The document itself is a sequence of entries closed by a
/// trailing 0x08.
/// </summary>
public static class BinaryVdf
{
    private const byte TagObject = 0x00;
    private const byte TagString = 0x01;
    private const byte TagInt32 = 0x02;
    private const byte TagFloat32 = 0x03;
    private const byte TagPointer = 0x04;
    private const byte TagWideString = 0x05;
    private const byte TagColor = 0x06;
    private const byte TagUInt64 = 0x07;
    private const byte TagEnd = 0x08;
    private const byte TagInt64 = 0x0A;

    /// <summary>Parses a binary KeyValues document into a synthetic root object.</summary>
    /// <exception cref="InvalidDataException">The file is not valid binary KeyValues.</exception>
    public static VdfNode Parse(ReadOnlySpan<byte> data)
    {
        var offset = 0;
        var root = VdfNode.NewObject();
        ReadEntries(data, ref offset, root, depth: 0);
        return root;
    }

    public static VdfNode ParseFile(string path) => Parse(File.ReadAllBytes(path));

    /// <summary>Serializes a document produced by <see cref="Parse"/> back to bytes.</summary>
    public static byte[] Serialize(VdfNode root)
    {
        using var buffer = new MemoryStream();
        foreach (var (key, node) in root.Children)
            WriteEntry(buffer, key, node);

        // The document's own terminator, matching how Steam writes the file.
        buffer.WriteByte(TagEnd);
        return buffer.ToArray();
    }

    private static void ReadEntries(ReadOnlySpan<byte> data, ref int offset, VdfNode parent, int depth)
    {
        if (depth > 64)
            throw new InvalidDataException("VDF nesting is implausibly deep; the file is probably corrupt.");

        while (true)
        {
            if (offset >= data.Length) return;

            var tag = data[offset++];
            if (tag == TagEnd) return;

            var key = ReadString(data, ref offset);

            switch (tag)
            {
                case TagObject:
                {
                    var child = VdfNode.NewObject();
                    ReadEntries(data, ref offset, child, depth + 1);
                    parent.Add(key, child);
                    break;
                }

                case TagString:
                    parent.Add(key, VdfNode.FromString(ReadString(data, ref offset)));
                    break;

                case TagWideString:
                    parent.Add(key, VdfNode.FromString(ReadWideString(data, ref offset)));
                    break;

                case TagInt32:
                case TagPointer:
                case TagColor:
                    parent.Add(key, VdfNode.FromInt(ReadInt32(data, ref offset)));
                    break;

                case TagFloat32:
                    parent.Add(key, VdfNode.FromFloat(BitConverter.Int32BitsToSingle(ReadInt32(data, ref offset))));
                    break;

                case TagUInt64:
                case TagInt64:
                    parent.Add(key, VdfNode.FromUInt64(ReadUInt64(data, ref offset)));
                    break;

                default:
                    throw new InvalidDataException(
                        $"Unknown VDF type tag 0x{tag:X2} at offset {offset - 1}.");
            }
        }
    }

    private static void WriteEntry(Stream output, string key, VdfNode node)
    {
        switch (node.Kind)
        {
            case VdfKind.Object:
                output.WriteByte(TagObject);
                WriteString(output, key);
                foreach (var (childKey, child) in node.Children)
                    WriteEntry(output, childKey, child);
                output.WriteByte(TagEnd);
                break;

            case VdfKind.String:
            case VdfKind.WideString:
                output.WriteByte(TagString);
                WriteString(output, key);
                WriteString(output, node.StringValue ?? string.Empty);
                break;

            case VdfKind.Int32:
            case VdfKind.Pointer:
            case VdfKind.Color:
                output.WriteByte(TagInt32);
                WriteString(output, key);
                WriteInt32(output, node.IntValue);
                break;

            case VdfKind.Float32:
                output.WriteByte(TagFloat32);
                WriteString(output, key);
                WriteInt32(output, BitConverter.SingleToInt32Bits(node.FloatValue));
                break;

            case VdfKind.UInt64:
            case VdfKind.Int64:
                output.WriteByte(TagUInt64);
                WriteString(output, key);
                WriteUInt64(output, node.UInt64Value);
                break;

            default:
                throw new InvalidDataException($"Cannot serialize VDF node of kind {node.Kind}.");
        }
    }

    private static string ReadString(ReadOnlySpan<byte> data, ref int offset)
    {
        var start = offset;

        while (offset < data.Length && data[offset] != 0) offset++;

        if (offset >= data.Length)
            throw new InvalidDataException("VDF string is not NUL-terminated; the file is truncated.");

        var value = Encoding.UTF8.GetString(data[start..offset]);
        offset++; // consume the NUL
        return value;
    }

    /// <summary>UTF-16LE, NUL-terminated. Rare in practice but part of the format.</summary>
    private static string ReadWideString(ReadOnlySpan<byte> data, ref int offset)
    {
        var start = offset;

        while (offset + 1 < data.Length && !(data[offset] == 0 && data[offset + 1] == 0))
            offset += 2;

        if (offset + 1 >= data.Length)
            throw new InvalidDataException("VDF wide string is not NUL-terminated; the file is truncated.");

        var value = Encoding.Unicode.GetString(data[start..offset]);
        offset += 2;
        return value;
    }

    private static int ReadInt32(ReadOnlySpan<byte> data, ref int offset)
    {
        if (offset + 4 > data.Length)
            throw new InvalidDataException("VDF int32 runs past the end of the file.");

        var value = BinaryPrimitives.ReadInt32LittleEndian(data[offset..]);
        offset += 4;
        return value;
    }

    private static ulong ReadUInt64(ReadOnlySpan<byte> data, ref int offset)
    {
        if (offset + 8 > data.Length)
            throw new InvalidDataException("VDF uint64 runs past the end of the file.");

        var value = BinaryPrimitives.ReadUInt64LittleEndian(data[offset..]);
        offset += 8;
        return value;
    }

    private static void WriteString(Stream output, string value)
    {
        // Steam cannot represent an embedded NUL, so strip any that slipped in.
        if (value.Contains('\0')) value = value.Replace("\0", string.Empty);

        var bytes = Encoding.UTF8.GetBytes(value);
        output.Write(bytes, 0, bytes.Length);
        output.WriteByte(0);
    }

    private static void WriteInt32(Stream output, int value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
        output.Write(buffer);
    }

    private static void WriteUInt64(Stream output, ulong value)
    {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
        output.Write(buffer);
    }
}
