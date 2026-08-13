using System.Windows.Media.Imaging;
using Nama.App.Infrastructure;
using Nama.App.Services;
using Nama.Core.Models;

namespace Nama.App.ViewModels;

/// <summary>One row in the "possible matches" list.</summary>
public sealed class GameCandidateViewModel(Game game, ImageLoader imageLoader) : ObservableObject
{
    private BitmapSource? _thumbnail;
    private bool _isSelected;
    private bool _thumbnailRequested;

    public Game Game { get; } = game;

    public string Title => Game.CanonicalName;

    /// <summary>The original-language title, shown under the main one when it differs.</summary>
    public string? Subtitle =>
        !string.IsNullOrWhiteSpace(Game.JapaneseName) &&
        !string.Equals(Game.JapaneseName, Game.CanonicalName, StringComparison.OrdinalIgnoreCase)
            ? Game.JapaneseName
            : null;

    /// <summary>"FromSoftware · 2022" — whichever parts are known.</summary>
    public string Details
    {
        get
        {
            var parts = new List<string>(2);
            if (!string.IsNullOrWhiteSpace(Game.Developer)) parts.Add(Game.Developer!);
            if (Game.Year is not null) parts.Add(Game.Year!);
            return string.Join(" · ", parts);
        }
    }

    /// <summary>Provider badges, e.g. "Steam · VNDB".</summary>
    public string Sources => string.Join(" · ", Game.SourceIds
        .Select(s => s.Provider switch
        {
            "steam" => "Steam",
            "steamgriddb" => "SteamGridDB",
            "vndb" => "VNDB",
            "igdb" => "IGDB",
            var other => other,
        })
        .Distinct(StringComparer.OrdinalIgnoreCase));

    public double Confidence => Game.Confidence;

    /// <summary>Only shown for reasonably strong matches; a low number is noise, not information.</summary>
    public bool ShowConfidence => Game.Confidence >= 0.5;

    public BitmapSource? Thumbnail
    {
        get => _thumbnail;
        private set => SetProperty(ref _thumbnail, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    /// <summary>Loads the cover art once, the first time the row is realized.</summary>
    public async Task EnsureThumbnailAsync(CancellationToken ct = default)
    {
        if (_thumbnailRequested) return;
        _thumbnailRequested = true;

        if (string.IsNullOrWhiteSpace(Game.PreviewImageUrl)) return;

        var image = await imageLoader.LoadAsync(Game.PreviewImageUrl, decodeWidth: 120, ct).ConfigureAwait(true);
        if (image is not null) Thumbnail = image;
    }
}
