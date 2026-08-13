using Nama.Core.Identification;
using Nama.Core.Models;
using Nama.Core.Providers;
using Nama.Storage;

namespace Nama.Providers.Local;

/// <summary>
/// Offers the game's own executable icon as Icon artwork.
///
/// Unlike every other provider this one is local, instant and always available, which
/// makes it the safety net for games no online database has ever heard of. It is
/// constructed per identification because it needs the executable path, which belongs to
/// the local target rather than to the <see cref="Game"/>.
/// </summary>
public sealed class ExecutableIconProvider(LocalGameTarget target, string? stagingDirectory = null)
    : IArtworkProvider
{
    private readonly string _stagingDirectory = stagingDirectory ?? NamaPaths.ArtworkStagingDirectory;

    public string Id => "gamefiles";
    public string DisplayName => "Game files";
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Runs first so its result is available immediately, without waiting on the network.
    /// </summary>
    public int Priority => 5;

    public IReadOnlyCollection<ArtworkType> SupportedTypes { get; } = [ArtworkType.Icon];

    public Task<IReadOnlyList<Artwork>> GetArtworkAsync(
        Game game,
        IReadOnlyCollection<ArtworkType> types,
        CancellationToken ct = default)
    {
        if (!IsEnabled || !types.Contains(ArtworkType.Icon))
            return Task.FromResult<IReadOnlyList<Artwork>>([]);

        if (!PeIconReader.TryExtract(target.ExecutablePath, out var extracted))
            return Task.FromResult<IReadOnlyList<Artwork>>([]);

        try
        {
            // Staged on disk so the rest of the pipeline can treat it like any other
            // artwork URL: the image loader and the Steam writer both resolve file URIs.
            Directory.CreateDirectory(_stagingDirectory);

            var name = $"icon-{Math.Abs(target.ExecutablePath.GetHashCode()):x8}{extracted.Extension}";
            var path = Path.Combine(_stagingDirectory, name);

            File.WriteAllBytes(path, extracted.Data);

            IReadOnlyList<Artwork> result =
            [
                new Artwork
                {
                    Id = $"gamefiles-icon-{name}",
                    Type = ArtworkType.Icon,
                    Url = new Uri(path).AbsoluteUri,
                    ThumbnailUrl = new Uri(path).AbsoluteUri,
                    Source = DisplayName,
                    Width = extracted.Width,
                    Height = extracted.Height,
                    // The game's real icon is the most likely thing the user wants for a
                    // title that online databases do not cover, so it leads its section.
                    Score = 1.0,
                    Author = "From the game",
                    Style = "original",
                },
            ];

            return Task.FromResult(result);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // A staging failure just means one fewer icon option.
            return Task.FromResult<IReadOnlyList<Artwork>>([]);
        }
    }
}
