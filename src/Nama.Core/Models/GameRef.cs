namespace Nama.Core.Models;

/// <summary>
/// Identity of a confirmed game across every provider that knows about it. Artwork
/// providers use this to resolve back to their own API without the caller needing to
/// know which provider originally matched the game.
/// </summary>
public sealed record GameRef
{
    private readonly Dictionary<string, string> _sourceIds;

    public GameRef(IEnumerable<KeyValuePair<string, string>> sourceIds, string name, string? japaneseName = null)
    {
        _sourceIds = new Dictionary<string, string>(sourceIds, StringComparer.OrdinalIgnoreCase);
        Name = name;
        JapaneseName = japaneseName;
    }

    /// <summary>Canonical name, used by providers that can only search by title.</summary>
    public string Name { get; }

    public string? JapaneseName { get; }

    public IReadOnlyDictionary<string, string> SourceIds => _sourceIds;

    public bool Has(string sourceId) => _sourceIds.ContainsKey(sourceId);

    public string? GetId(string sourceId) => _sourceIds.GetValueOrDefault(sourceId);
}
