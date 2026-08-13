using System.Globalization;

namespace Nama.SteamIntegration.Vdf;

/// <summary>
/// A node in a Valve KeyValues document. A node is either a leaf holding a scalar or a
/// map holding ordered children; Steam's binary format preserves child order and so
/// does this type.
/// </summary>
public sealed class VdfNode
{
    private readonly List<KeyValuePair<string, VdfNode>> _children = [];

    public VdfKind Kind { get; private set; }

    public string? StringValue { get; private set; }
    public int IntValue { get; private set; }
    public ulong UInt64Value { get; private set; }
    public float FloatValue { get; private set; }

    public IReadOnlyList<KeyValuePair<string, VdfNode>> Children => _children;

    public static VdfNode NewObject() => new() { Kind = VdfKind.Object };

    public static VdfNode FromString(string? value) =>
        new() { Kind = VdfKind.String, StringValue = value ?? string.Empty };

    public static VdfNode FromInt(int value) => new() { Kind = VdfKind.Int32, IntValue = value };

    public static VdfNode FromUInt64(ulong value) => new() { Kind = VdfKind.UInt64, UInt64Value = value };

    public static VdfNode FromFloat(float value) => new() { Kind = VdfKind.Float32, FloatValue = value };

    /// <summary>Case-insensitive child lookup — Steam is inconsistent about key casing.</summary>
    public VdfNode? this[string key]
    {
        get
        {
            foreach (var (name, node) in _children)
                if (string.Equals(name, key, StringComparison.OrdinalIgnoreCase))
                    return node;
            return null;
        }
    }

    /// <summary>Adds or replaces a child, preserving the original position when replacing.</summary>
    public void Set(string key, VdfNode value)
    {
        for (var i = 0; i < _children.Count; i++)
        {
            if (string.Equals(_children[i].Key, key, StringComparison.OrdinalIgnoreCase))
            {
                _children[i] = new KeyValuePair<string, VdfNode>(_children[i].Key, value);
                return;
            }
        }

        _children.Add(new KeyValuePair<string, VdfNode>(key, value));
    }

    public void Set(string key, string value) => Set(key, FromString(value));
    public void Set(string key, int value) => Set(key, FromInt(value));
    public void Set(string key, bool value) => Set(key, FromInt(value ? 1 : 0));

    public bool Remove(string key)
    {
        for (var i = 0; i < _children.Count; i++)
        {
            if (string.Equals(_children[i].Key, key, StringComparison.OrdinalIgnoreCase))
            {
                _children.RemoveAt(i);
                return true;
            }
        }
        return false;
    }

    public void Clear() => _children.Clear();

    /// <summary>Appends a child without checking for an existing key. Used when rebuilding lists.</summary>
    public void Add(string key, VdfNode value) =>
        _children.Add(new KeyValuePair<string, VdfNode>(key, value));

    /// <summary>Reads a child as a string, coercing numeric leaves. Returns <paramref name="fallback"/> when absent.</summary>
    public string GetString(string key, string fallback = "")
    {
        var node = this[key];
        if (node is null) return fallback;

        return node.Kind switch
        {
            VdfKind.String => node.StringValue ?? fallback,
            VdfKind.Int32 => node.IntValue.ToString(CultureInfo.InvariantCulture),
            VdfKind.UInt64 => node.UInt64Value.ToString(CultureInfo.InvariantCulture),
            VdfKind.Float32 => node.FloatValue.ToString(CultureInfo.InvariantCulture),
            _ => fallback,
        };
    }

    /// <summary>Reads a child as an int, parsing string leaves. Returns <paramref name="fallback"/> when absent or unparsable.</summary>
    public int GetInt(string key, int fallback = 0)
    {
        var node = this[key];
        if (node is null) return fallback;

        return node.Kind switch
        {
            VdfKind.Int32 => node.IntValue,
            VdfKind.UInt64 => unchecked((int)node.UInt64Value),
            VdfKind.String when int.TryParse(node.StringValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => fallback,
        };
    }

    public bool GetBool(string key, bool fallback = false) =>
        this[key] is null ? fallback : GetInt(key, fallback ? 1 : 0) != 0;

    public VdfNode GetOrCreateObject(string key)
    {
        var existing = this[key];
        if (existing is { Kind: VdfKind.Object }) return existing;

        var created = NewObject();
        Set(key, created);
        return created;
    }
}

public enum VdfKind
{
    Object = 0,
    String = 1,
    Int32 = 2,
    Float32 = 3,
    Pointer = 4,
    WideString = 5,
    Color = 6,
    UInt64 = 7,
    Int64 = 10,
}
