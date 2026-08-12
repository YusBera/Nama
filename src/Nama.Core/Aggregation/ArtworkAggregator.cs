using Nama.Core.Abstractions;
using Nama.Core.Models;

namespace Nama.Core.Aggregation;

/// <summary>Artwork from every provider, grouped by slot.</summary>
public sealed class ArtworkCollection
{
    public required IReadOnlyList<Artwork> All { get; init; }

    /// <summary>Providers that failed outright. The UI can note them without blocking.</summary>
    public required IReadOnlyList<string> FailedProviders { get; init; }

    /// <summary>Providers that were skipped for want of a key or a usable id.</summary>
    public required IReadOnlyList<string> SkippedProviders { get; init; }

    /// <summary>Artwork for one slot, unranked.</summary>
    public IReadOnlyList<Artwork> OfType(ArtworkType type) =>
        All.Where(a => a.Type == type).ToList();

    /// <summary>Slots that actually have artwork, in Nama's display order. Empty slots are not shown.</summary>
    public IReadOnlyList<ArtworkType> AvailableTypes =>
        DisplayOrder.Where(type => All.Any(a => a.Type == type)).ToList();

    /// <summary>The order artwork sections appear in, most useful first.</summary>
    public static readonly IReadOnlyList<ArtworkType> DisplayOrder =
    [
        ArtworkType.Cover,
        ArtworkType.Grid,
        ArtworkType.Hero,
        ArtworkType.Logo,
        ArtworkType.Icon,
        ArtworkType.Background,
    ];

    public static ArtworkCollection Empty { get; } = new()
    {
        All = [],
        FailedProviders = [],
        SkippedProviders = [],
    };
}

/// <summary>
/// Fans out to every available artwork provider and merges the results into one list.
/// <para>
/// The user picks an image, never a source — so this exists to make several providers look
/// like one. Providers run concurrently, and one failing only costs its own results.
/// </para>
/// </summary>
public sealed class ArtworkAggregator(IEnumerable<IArtworkProvider> providers)
{
    private readonly IReadOnlyList<IArtworkProvider> _providers = providers.ToList();

    public async Task<ArtworkCollection> GetArtworkAsync(GameRef game, CancellationToken ct = default)
    {
        var failed = new List<string>();
        var skipped = new List<string>();
        var usable = new List<IArtworkProvider>();

        foreach (var provider in _providers)
        {
            if (!provider.IsAvailable || !provider.CanResolve(game)) skipped.Add(provider.DisplayName);
            else usable.Add(provider);
        }

        var results = await Task.WhenAll(usable.Select(async provider =>
        {
            try
            {
                return await provider.GetArtworkAsync(game, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // The interface says providers should not throw. This is the backstop for
                // when one does anyway — a bug in a single provider must not take down the
                // whole artwork step.
                lock (failed) failed.Add(provider.DisplayName);
                return [];
            }
        })).ConfigureAwait(false);

        return new ArtworkCollection
        {
            All = results.SelectMany(r => r).ToList(),
            FailedProviders = failed,
            SkippedProviders = skipped,
        };
    }
}
