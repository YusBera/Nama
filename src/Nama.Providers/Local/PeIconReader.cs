using System.Buffers.Binary;

namespace Nama.Providers.Local;

/// <summary>The best icon found inside an executable.</summary>
/// <param name="Data">Ready-to-write image bytes: either a PNG or a single-image ICO container.</param>
/// <param name="Extension">File extension matching <paramref name="Data"/>, including the dot.</param>
/// <param name="Width">Pixel width. 256 for the modern large icon.</param>
/// <param name="Height">Pixel height.</param>
/// <param name="BitCount">Colour depth, used to prefer 32-bit icons over legacy palettes.</param>
public readonly record struct ExtractedIcon(byte[] Data, string Extension, int Width, int Height, int BitCount);

/// <summary>
/// Pulls the largest icon out of a Windows executable by walking the PE resource tree.
///
/// This exists because a game's own icon is the single most reliable Icon-type artwork
/// available: it is already on disk, needs no provider, no API key and no network, and
/// it is correct by definition. It matters most for Japanese visual novels, which
/// frequently have no SteamGridDB entry at all.
///
/// Implemented as a pure managed reader rather than via <c>PrivateExtractIcons</c> so it
/// stays in a platform-neutral project and can be tested without any Win32 interop.
/// </summary>
public static class PeIconReader
{
    private const int RtIcon = 3;
    private const int RtGroupIcon = 14;

    /// <summary>Refuse absurd resource sizes rather than trusting a malformed header.</summary>
    private const int MaxImageBytes = 8 * 1024 * 1024;

    /// <summary>
    /// Overflow-safe bounds check. Every offset here comes from the file being parsed, so
    /// the naive <c>offset + needed &gt; length</c> form can wrap to a negative number and
    /// wave a hostile header straight through into an out-of-range read.
    /// </summary>
    private static bool Fits(int offset, int needed, int length) =>
        offset >= 0 && needed >= 0 && offset <= length - needed;

    /// <summary>
    /// Extracts the highest-quality icon from <paramref name="path"/>.
    /// Returns false for non-PE files, resource-less executables, and anything malformed —
    /// a missing icon is an ordinary outcome, never an error.
    /// </summary>
    public static bool TryExtract(string path, out ExtractedIcon icon)
    {
        icon = default;

        try
        {
            var file = File.ReadAllBytes(path);
            return TryExtract(file, out icon);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Extracts from an in-memory image. Exposed for testing.</summary>
    public static bool TryExtract(ReadOnlySpan<byte> file, out ExtractedIcon icon)
    {
        icon = default;

        if (!TryFindResourceSection(file, out var resourceBase, out var resourceRva, out var sections))
            return false;

        // Level 1 of the resource tree is keyed by type. Group icons describe which
        // individual RT_ICON images belong together, so that is the entry point.
        if (!TryFindTypeDirectory(file, resourceBase, RtGroupIcon, out var groupDirectory))
            return false;

        if (!TryReadFirstLeaf(file, resourceBase, groupDirectory, out var groupRva, out var groupSize))
            return false;

        if (!TryResolve(sections, groupRva, groupSize, file.Length, out var groupOffset))
            return false;

        var group = file.Slice(groupOffset, groupSize);
        if (!TrySelectBestEntry(group, out var bestId, out var width, out var height, out var bitCount))
            return false;

        // Now fetch the actual image bytes for the chosen entry.
        if (!TryFindTypeDirectory(file, resourceBase, RtIcon, out var iconDirectory))
            return false;

        if (!TryFindLeafById(file, resourceBase, iconDirectory, bestId, out var imageRva, out var imageSize))
            return false;

        if (imageSize <= 0 || imageSize > MaxImageBytes) return false;
        if (!TryResolve(sections, imageRva, imageSize, file.Length, out var imageOffset))
            return false;

        var image = file.Slice(imageOffset, imageSize);

        // Vista-era 256x256 icons are stored as embedded PNGs and can be written straight
        // out. Everything else is a headerless DIB that needs an ICO container to be
        // readable by an image decoder.
        var isPng = image.Length > 8 &&
                    image[0] == 0x89 && image[1] == 0x50 && image[2] == 0x4E && image[3] == 0x47;

        icon = isPng
            ? new ExtractedIcon(image.ToArray(), ".png", width, height, bitCount)
            : new ExtractedIcon(WrapInIcoContainer(image, width, height, bitCount), ".ico", width, height, bitCount);

        _ = resourceRva;
        return true;
    }

    /// <summary>
    /// Locates the resource data directory and the section table needed to translate
    /// resource RVAs into file offsets.
    /// </summary>
    private static bool TryFindResourceSection(
        ReadOnlySpan<byte> file,
        out int resourceBase,
        out uint resourceRva,
        out List<Section> sections)
    {
        resourceBase = 0;
        resourceRva = 0;
        sections = [];

        if (file.Length < 0x40) return false;
        if (file[0] != (byte)'M' || file[1] != (byte)'Z') return false;

        var peOffset = BinaryPrimitives.ReadInt32LittleEndian(file[0x3C..]);
        if (peOffset <= 0 || !Fits(peOffset, 24, file.Length)) return false;

        if (file[peOffset] != (byte)'P' || file[peOffset + 1] != (byte)'E' ||
            file[peOffset + 2] != 0 || file[peOffset + 3] != 0)
            return false;

        var coff = peOffset + 4;
        var sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(file[(coff + 2)..]);
        var optionalSize = BinaryPrimitives.ReadUInt16LittleEndian(file[(coff + 16)..]);
        var optional = coff + 20;

        if (!Fits(optional, optionalSize, file.Length) || optionalSize < 2) return false;

        // PE32 and PE32+ place the data directory array at different offsets.
        var magic = BinaryPrimitives.ReadUInt16LittleEndian(file[optional..]);
        var directoryOffset = magic switch
        {
            0x10B => 96,   // PE32
            0x20B => 112,  // PE32+
            _ => -1,
        };
        if (directoryOffset < 0) return false;

        // Data directory index 2 is the resource table.
        var resourceEntry = optional + directoryOffset + (2 * 8);
        if (!Fits(resourceEntry, 8, file.Length)) return false;

        resourceRva = BinaryPrimitives.ReadUInt32LittleEndian(file[resourceEntry..]);
        var resourceSize = BinaryPrimitives.ReadUInt32LittleEndian(file[(resourceEntry + 4)..]);
        if (resourceRva == 0 || resourceSize == 0) return false;

        var sectionTable = optional + optionalSize;

        for (var i = 0; i < sectionCount; i++)
        {
            var entry = sectionTable + (i * 40);
            if (!Fits(entry, 40, file.Length)) return false;

            sections.Add(new Section(
                VirtualAddress: BinaryPrimitives.ReadUInt32LittleEndian(file[(entry + 12)..]),
                VirtualSize: BinaryPrimitives.ReadUInt32LittleEndian(file[(entry + 8)..]),
                RawAddress: BinaryPrimitives.ReadUInt32LittleEndian(file[(entry + 20)..]),
                RawSize: BinaryPrimitives.ReadUInt32LittleEndian(file[(entry + 16)..])));
        }

        // Offsets inside the resource tree are relative to the start of the resource data.
        if (!TryResolve(sections, resourceRva, 16, file.Length, out resourceBase)) return false;

        return true;
    }

    /// <summary>Finds the level-1 directory for a resource type, returning its offset from the resource base.</summary>
    private static bool TryFindTypeDirectory(ReadOnlySpan<byte> file, int resourceBase, int type, out int directoryOffset)
    {
        directoryOffset = 0;
        if (!TryReadDirectory(file, resourceBase, 0, out var named, out var ids, out var entries)) return false;

        // Named entries come first and are irrelevant here; icon types are always numeric.
        for (var i = named; i < named + ids; i++)
        {
            var entry = entries + (i * 8);
            if (!Fits(entry, 8, file.Length)) return false;

            var id = BinaryPrimitives.ReadUInt32LittleEndian(file[entry..]);
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(file[(entry + 4)..]);

            if (id != (uint)type) continue;

            // A type entry must point at a subdirectory (high bit set).
            if ((offset & 0x8000_0000) == 0) return false;

            directoryOffset = (int)(offset & 0x7FFF_FFFF);
            return true;
        }

        return false;
    }

    /// <summary>Walks down to the first data leaf under a directory, skipping the language level.</summary>
    private static bool TryReadFirstLeaf(
        ReadOnlySpan<byte> file, int resourceBase, int directoryOffset, out uint rva, out int size)
    {
        rva = 0;
        size = 0;

        if (!TryReadDirectory(file, resourceBase, directoryOffset, out var named, out var ids, out var entries))
            return false;

        if (named + ids == 0) return false;

        var offset = BinaryPrimitives.ReadUInt32LittleEndian(file[(entries + 4)..]);
        return TryReadLeaf(file, resourceBase, offset, out rva, out size);
    }

    /// <summary>Finds the leaf whose level-2 id matches, which is how group entries reference images.</summary>
    private static bool TryFindLeafById(
        ReadOnlySpan<byte> file, int resourceBase, int directoryOffset, int wantedId, out uint rva, out int size)
    {
        rva = 0;
        size = 0;

        if (!TryReadDirectory(file, resourceBase, directoryOffset, out var named, out var ids, out var entries))
            return false;

        for (var i = named; i < named + ids; i++)
        {
            var entry = entries + (i * 8);
            if (!Fits(entry, 8, file.Length)) return false;

            var id = BinaryPrimitives.ReadUInt32LittleEndian(file[entry..]);
            if (id != (uint)wantedId) continue;

            var offset = BinaryPrimitives.ReadUInt32LittleEndian(file[(entry + 4)..]);
            return TryReadLeaf(file, resourceBase, offset, out rva, out size);
        }

        return false;
    }

    /// <summary>Resolves an entry that may be a subdirectory (descend once) or a data leaf.</summary>
    private static bool TryReadLeaf(ReadOnlySpan<byte> file, int resourceBase, uint offset, out uint rva, out int size)
    {
        rva = 0;
        size = 0;

        if ((offset & 0x8000_0000) != 0)
        {
            // Subdirectory: descend into the language level and take its first entry.
            var child = (int)(offset & 0x7FFF_FFFF);
            if (!TryReadDirectory(file, resourceBase, child, out var named, out var ids, out var entries))
                return false;
            if (named + ids == 0) return false;

            offset = BinaryPrimitives.ReadUInt32LittleEndian(file[(entries + 4)..]);

            // Guard against a tree that nests deeper than the format allows.
            if ((offset & 0x8000_0000) != 0) return false;
        }

        var leaf = resourceBase + (int)offset;
        if (!Fits(leaf, 16, file.Length)) return false;

        rva = BinaryPrimitives.ReadUInt32LittleEndian(file[leaf..]);
        var rawSize = BinaryPrimitives.ReadUInt32LittleEndian(file[(leaf + 4)..]);

        if (rawSize > int.MaxValue) return false;
        size = (int)rawSize;
        return true;
    }

    private static bool TryReadDirectory(
        ReadOnlySpan<byte> file, int resourceBase, int directoryOffset, out int named, out int ids, out int entries)
    {
        named = 0;
        ids = 0;
        entries = 0;

        var start = resourceBase + directoryOffset;
        if (!Fits(start, 16, file.Length)) return false;

        named = BinaryPrimitives.ReadUInt16LittleEndian(file[(start + 12)..]);
        ids = BinaryPrimitives.ReadUInt16LittleEndian(file[(start + 14)..]);
        entries = start + 16;

        if (named + ids > 8192) return false;
        if (!Fits(entries, (named + ids) * 8, file.Length)) return false;

        return true;
    }

    /// <summary>
    /// Picks the best entry from a GRPICONDIR: largest pixel area first, then colour depth.
    /// </summary>
    private static bool TrySelectBestEntry(
        ReadOnlySpan<byte> group, out int id, out int width, out int height, out int bitCount)
    {
        id = 0;
        width = 0;
        height = 0;
        bitCount = 0;

        if (group.Length < 6) return false;

        var count = BinaryPrimitives.ReadUInt16LittleEndian(group[4..]);
        if (count == 0 || 6 + (count * 14) > group.Length) return false;

        var bestArea = -1;

        for (var i = 0; i < count; i++)
        {
            var entry = 6 + (i * 14);

            // A stored dimension of 0 means 256 — the format only has one byte per axis.
            int entryWidth = group[entry] == 0 ? 256 : group[entry];
            int entryHeight = group[entry + 1] == 0 ? 256 : group[entry + 1];
            int entryBits = BinaryPrimitives.ReadUInt16LittleEndian(group[(entry + 6)..]);
            var entryId = BinaryPrimitives.ReadUInt16LittleEndian(group[(entry + 12)..]);

            var area = entryWidth * entryHeight;

            if (area > bestArea || (area == bestArea && entryBits > bitCount))
            {
                bestArea = area;
                width = entryWidth;
                height = entryHeight;
                bitCount = entryBits;
                id = entryId;
            }
        }

        return bestArea > 0;
    }

    /// <summary>
    /// Wraps a raw DIB icon image in a one-entry ICO container so ordinary image decoders
    /// can read it. The resource stores only the pixel data; the directory lives separately.
    /// </summary>
    private static byte[] WrapInIcoContainer(ReadOnlySpan<byte> image, int width, int height, int bitCount)
    {
        const int headerSize = 6;
        const int entrySize = 16;

        var result = new byte[headerSize + entrySize + image.Length];
        var span = result.AsSpan();

        // ICONDIR
        BinaryPrimitives.WriteUInt16LittleEndian(span[0..], 0);  // reserved
        BinaryPrimitives.WriteUInt16LittleEndian(span[2..], 1);  // type: icon
        BinaryPrimitives.WriteUInt16LittleEndian(span[4..], 1);  // one image

        // ICONDIRENTRY
        span[6] = (byte)(width >= 256 ? 0 : width);
        span[7] = (byte)(height >= 256 ? 0 : height);
        span[8] = 0; // colour count, 0 for >8bpp
        span[9] = 0; // reserved
        BinaryPrimitives.WriteUInt16LittleEndian(span[10..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(span[12..], (ushort)bitCount);
        BinaryPrimitives.WriteUInt32LittleEndian(span[14..], (uint)image.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(span[18..], headerSize + entrySize);

        image.CopyTo(span[(headerSize + entrySize)..]);
        return result;
    }

    /// <summary>Translates a virtual address into a file offset using the section table.</summary>
    private static bool TryResolve(List<Section> sections, uint rva, int needed, int fileLength, out int offset)
    {
        offset = 0;

        foreach (var section in sections)
        {
            var size = Math.Max(section.VirtualSize, section.RawSize);
            if (rva < section.VirtualAddress || rva >= section.VirtualAddress + size) continue;

            var candidate = (long)section.RawAddress + (rva - section.VirtualAddress);
            if (candidate < 0 || candidate + needed > fileLength) return false;

            offset = (int)candidate;
            return true;
        }

        return false;
    }

    private readonly record struct Section(uint VirtualAddress, uint VirtualSize, uint RawAddress, uint RawSize);
}
