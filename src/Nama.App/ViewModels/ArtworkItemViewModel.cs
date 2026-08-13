using System.Windows.Media.Imaging;
using Nama.App.Infrastructure;
using Nama.App.Services;
using Nama.Core.Models;

namespace Nama.App.ViewModels;

/// <summary>A single selectable artwork tile.</summary>
public sealed class ArtworkItemViewModel(Artwork artwork, ImageLoader imageLoader, int decodeWidth) : ObservableObject
{
    private BitmapSource? _image;
    private bool _isSelected;
    private bool _isLoading;
    private bool _failedToLoad;
    private bool _requested;

    public Artwork Artwork { get; } = artwork;

    /// <summary>Provider badge shown in the tile corner.</summary>
    public string Source => Artwork.Source;

    public string? Author => string.IsNullOrWhiteSpace(Artwork.Author) ? null : $"by {Artwork.Author}";

    public string Dimensions => Artwork.Dimensions;

    public BitmapSource? Image
    {
        get => _image;
        private set => SetProperty(ref _image, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    /// <summary>True when the preview could not be fetched, so the tile shows a placeholder.</summary>
    public bool FailedToLoad
    {
        get => _failedToLoad;
        private set => SetProperty(ref _failedToLoad, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    /// <summary>
    /// Downloads and decodes the preview once. Called when the tile first becomes
    /// visible, so collapsed sections cost nothing.
    /// </summary>
    public async Task EnsureImageAsync(CancellationToken ct = default)
    {
        if (_requested) return;
        _requested = true;

        IsLoading = true;
        try
        {
            var image = await imageLoader.LoadAsync(Artwork.PreviewUrl, decodeWidth, ct).ConfigureAwait(true);

            if (image is null)
                FailedToLoad = true;
            else
                Image = image;
        }
        finally
        {
            IsLoading = false;
        }
    }
}
