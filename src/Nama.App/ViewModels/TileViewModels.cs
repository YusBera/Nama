using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Nama.App.Services;
using Nama.Core.Models;

namespace Nama.App.ViewModels;

/// <summary>One row in the "what game is this?" result list.</summary>
public sealed partial class GameMatchViewModel : ObservableObject
{
    private readonly ThumbnailLoader _thumbnails;

    public GameMatchViewModel(GameCandidate candidate, ThumbnailLoader thumbnails)
    {
        Candidate = candidate;
        _thumbnails = thumbnails;

        _ = LoadThumbnailAsync();
    }

    public GameCandidate Candidate { get; }

    public string Name => Candidate.Name;

    public string? JapaneseName =>
        string.Equals(Candidate.JapaneseName, Candidate.Name, StringComparison.Ordinal)
            ? null
            : Candidate.JapaneseName;

    public bool HasJapaneseName => !string.IsNullOrWhiteSpace(JapaneseName);

    /// <summary>"FromSoftware · 2022", with either part omitted when unknown.</summary>
    public string Subtitle
    {
        get
        {
            var developer = Candidate.Developer;
            var year = Candidate.ReleaseDate?.Year.ToString();

            return (developer, year) switch
            {
                (not null, not null) => $"{developer} · {year}",
                (not null, null) => developer,
                (null, not null) => year!,
                _ => "Unknown",
            };
        }
    }

    public string SourceLabel => Candidate.Source.ToUpperInvariant();

    public int ConfidencePercent => (int)Math.Round(Candidate.Confidence * 100);

    [ObservableProperty]
    private BitmapImage? thumbnail;

    [ObservableProperty]
    private bool isSelected;

    private async Task LoadThumbnailAsync()
    {
        if (Candidate.CoverUrl is null) return;

        Thumbnail = await _thumbnails.LoadAsync(Candidate.CoverUrl, decodeWidth: 96).ConfigureAwait(true);
    }
}

/// <summary>One artwork image the user can pick.</summary>
public sealed partial class ArtworkTileViewModel : ObservableObject
{
    private readonly ThumbnailLoader _thumbnails;

    public ArtworkTileViewModel(Artwork artwork, ThumbnailLoader thumbnails, int decodeWidth)
    {
        Artwork = artwork;
        _thumbnails = thumbnails;
        DecodeWidth = decodeWidth;

        _ = LoadThumbnailAsync();
    }

    public Artwork Artwork { get; }

    public int DecodeWidth { get; }

    public string SourceLabel => Artwork.Source switch
    {
        "steamgriddb" => "SteamGridDB",
        "steam" => "Steam",
        "vndb" => "VNDB",
        "dlsite" => "DLsite",
        "local" => "Local",
        "igdb" => "IGDB",
        var other => other,
    };

    public string Dimensions => Artwork.Width > 0 ? $"{Artwork.Width}×{Artwork.Height}" : string.Empty;

    public bool IsNsfw => Artwork.IsNsfw;

    [ObservableProperty]
    private BitmapImage? thumbnail;

    [ObservableProperty]
    private bool isSelected;

    [ObservableProperty]
    private bool isLoading = true;

    private async Task LoadThumbnailAsync()
    {
        Thumbnail = await _thumbnails.LoadAsync(Artwork.PreviewUrl, DecodeWidth).ConfigureAwait(true);
        IsLoading = false;
    }
}
