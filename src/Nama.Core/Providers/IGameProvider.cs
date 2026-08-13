using Nama.Core.Models;

namespace Nama.Core.Providers;

/// <summary>
/// A source that can turn a search term into candidate <see cref="Game"/> records.
/// Implementations must be safe to call concurrently and must not throw for
/// ordinary network failures — return an empty result instead.
/// </summary>
public interface IGameProvider
{
    /// <summary>Stable machine id used in <see cref="GameSourceId"/>, e.g. <c>vndb</c>.</summary>
    string Id { get; }

    /// <summary>Name shown to the user.</summary>
    string DisplayName { get; }

    /// <summary>
    /// False when the provider cannot run — typically a missing API key. Disabled
    /// providers are skipped silently rather than surfacing errors.
    /// </summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Lower runs first and its results win ties during merging.
    /// Steam = 10, SteamGridDB = 20, VNDB = 30.
    /// </summary>
    int Priority { get; }

    Task<IReadOnlyList<Game>> SearchAsync(string query, CancellationToken ct = default);
}

/// <summary>A source of artwork for an already-identified game.</summary>
public interface IArtworkProvider
{
    string Id { get; }
    string DisplayName { get; }
    bool IsEnabled { get; }
    int Priority { get; }

    /// <summary>Artwork types this provider can supply, used to skip pointless requests.</summary>
    IReadOnlyCollection<ArtworkType> SupportedTypes { get; }

    /// <summary>
    /// Fetches artwork for <paramref name="game"/>. Providers that cannot resolve the
    /// game from its <see cref="Game.SourceIds"/> should return an empty list.
    /// </summary>
    Task<IReadOnlyList<Artwork>> GetArtworkAsync(
        Game game,
        IReadOnlyCollection<ArtworkType> types,
        CancellationToken ct = default);
}
