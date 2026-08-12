using Nama.Core.Abstractions;
using Nama.Core.Models;

namespace Nama.Providers.Igdb;

/// <summary>
/// Placeholder for IGDB, deliberately not implemented in v1.
/// <para>
/// It exists to keep the extension point honest: adding a real provider must be a matter
/// of filling in these two methods and adding one registration line, with no change to the
/// identification pipeline, the artwork aggregator or the UI. If that ever stops being
/// true, this class stops compiling.
/// </para>
/// <para>
/// IGDB also needs Twitch OAuth credentials rather than a simple key, which is why it is
/// not part of the first release.
/// </para>
/// </summary>
public sealed class IgdbProvider : IGameProvider, IArtworkProvider
{
    public const string Id = "igdb";

    public string SourceId => Id;

    public string DisplayName => "IGDB";

    /// <summary>Always false: unavailable providers are skipped without comment.</summary>
    public bool IsAvailable => false;

    public int Priority => 50;

    public IReadOnlyCollection<ArtworkType> SupportedTypes { get; } =
        [ArtworkType.Cover, ArtworkType.Background];

    public bool CanResolve(GameRef game) => false;

    public Task<IReadOnlyList<GameCandidate>> SearchAsync(string query, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<GameCandidate>>([]);

    public Task<IReadOnlyList<Artwork>> GetArtworkAsync(GameRef game, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Artwork>>([]);
}
