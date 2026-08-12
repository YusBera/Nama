using System.Diagnostics.CodeAnalysis;

namespace Nama.Steam.Vdf;

/// <summary>Wire type tags used by Valve's binary VDF format.</summary>
public static class VdfType
{
    public const byte Map = 0x00;
    public const byte String = 0x01;
    public const byte Int32 = 0x02;
    public const byte Float32 = 0x03;
    public const byte WideString = 0x05;
    public const byte UInt64 = 0x07;
    public const byte End = 0x08;
}

/// <summary>Base for every value in a parsed VDF tree.</summary>
public abstract class VdfNode;

/// <summary>
/// A UTF-8 string value.
/// <para>
/// The bytes it was decoded from are retained and re-emitted verbatim unless
/// <see cref="Value"/> is assigned. Decode/encode is not guaranteed to be lossless for
/// malformed UTF-8, and this file belongs to the user — an untouched entry must come
/// back out exactly as it went in.
/// </para>
/// </summary>
public sealed class VdfString : VdfNode
{
    private string _value;

    public VdfString(string value)
    {
        _value = value;
        OriginalBytes = null;
    }

    internal VdfString(string value, byte[] originalBytes)
    {
        _value = value;
        OriginalBytes = originalBytes;
    }

    public string Value
    {
        get => _value;
        set
        {
            _value = value;
            OriginalBytes = null; // Edited: the original encoding no longer applies.
        }
    }

    internal byte[]? OriginalBytes { get; private set; }

    public override string ToString() => _value;
}

/// <summary>A 32-bit little-endian integer value.</summary>
public sealed class VdfInt32(int value) : VdfNode
{
    public int Value { get; set; } = value;

    public override string ToString() => Value.ToString();
}

/// <summary>A 64-bit little-endian integer value. Not used by shortcuts.vdf, supported for completeness.</summary>
public sealed class VdfUInt64(ulong value) : VdfNode
{
    public ulong Value { get; set; } = value;

    public override string ToString() => Value.ToString();
}

/// <summary>A 32-bit float value. Not used by shortcuts.vdf, supported for completeness.</summary>
public sealed class VdfFloat32(float value) : VdfNode
{
    public float Value { get; set; } = value;

    public override string ToString() => Value.ToString();
}

/// <summary>One key/value pair inside a <see cref="VdfMap"/>.</summary>
public sealed class VdfEntry
{
    public VdfEntry(string key, VdfNode value)
    {
        Key = key;
        Value = value;
    }

    internal VdfEntry(string key, VdfNode value, byte[] originalKeyBytes)
    {
        Key = key;
        Value = value;
        OriginalKeyBytes = originalKeyBytes;
    }

    public string Key { get; }

    public VdfNode Value { get; set; }

    internal byte[]? OriginalKeyBytes { get; }
}

/// <summary>
/// An ordered property bag, not a fixed structure.
/// <para>
/// This is deliberate and load-bearing. Nama must be able to read a shortcuts file
/// containing keys it has never heard of — added by a future Steam version, by Steam
/// Deck tooling, by another third-party utility — modify one entry, and write everything
/// else back untouched. A typed struct would silently drop what it did not model.
/// </para>
/// </summary>
public sealed class VdfMap : VdfNode
{
    private readonly List<VdfEntry> _entries = [];

    public IReadOnlyList<VdfEntry> Entries => _entries;

    public int Count => _entries.Count;

    public VdfNode? this[string key] => Find(key)?.Value;

    public void Add(string key, VdfNode value) => _entries.Add(new VdfEntry(key, value));

    internal void AddRaw(VdfEntry entry) => _entries.Add(entry);

    public VdfEntry? Find(string key)
    {
        foreach (var entry in _entries)
        {
            // Steam writes these keys with inconsistent casing across versions
            // ("AppName" vs "appname"), so lookup ignores case while writing preserves it.
            if (string.Equals(entry.Key, key, StringComparison.OrdinalIgnoreCase)) return entry;
        }

        return null;
    }

    public bool Contains(string key) => Find(key) is not null;

    /// <summary>Replaces the value at an existing key, keeping its position, or appends it.</summary>
    public void Set(string key, VdfNode value)
    {
        var existing = Find(key);
        if (existing is not null) existing.Value = value;
        else Add(key, value);
    }

    public void SetString(string key, string value)
    {
        // Assigning through Value keeps the entry in place and drops the stale byte cache.
        if (Find(key)?.Value is VdfString existing) existing.Value = value;
        else Set(key, new VdfString(value));
    }

    public void SetInt32(string key, int value)
    {
        if (Find(key)?.Value is VdfInt32 existing) existing.Value = value;
        else Set(key, new VdfInt32(value));
    }

    public bool Remove(string key)
    {
        var entry = Find(key);
        return entry is not null && _entries.Remove(entry);
    }

    public string? GetString(string key) => (Find(key)?.Value as VdfString)?.Value;

    public int? GetInt32(string key) => (Find(key)?.Value as VdfInt32)?.Value;

    public VdfMap? GetMap(string key) => Find(key)?.Value as VdfMap;

    public bool TryGetMap(string key, [NotNullWhen(true)] out VdfMap? map)
    {
        map = GetMap(key);
        return map is not null;
    }

    /// <summary>Child maps in order, as (key, map) pairs. Shortcut entries are keyed "0", "1", …</summary>
    public IEnumerable<(string Key, VdfMap Map)> ChildMaps()
    {
        foreach (var entry in _entries)
        {
            if (entry.Value is VdfMap map) yield return (entry.Key, map);
        }
    }
}
