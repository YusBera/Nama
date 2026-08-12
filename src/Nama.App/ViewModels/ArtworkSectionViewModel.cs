using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nama.App.Services;
using Nama.Core.Aggregation;
using Nama.Core.Models;
using Microsoft.Win32;

namespace Nama.App.ViewModels;

/// <summary>
/// One artwork category: five recommended images, and a "Show more" that expands this
/// section — and only this section — into a scrollable picker.
/// </summary>
public sealed partial class ArtworkSectionViewModel : ObservableObject
{
    private const int RecommendedCount = 5;

    private readonly List<ArtworkTileViewModel> _all;
    private readonly ThumbnailLoader _thumbnails;

    public ArtworkSectionViewModel(ArtworkType type, IReadOnlyList<Artwork> artwork, ThumbnailLoader thumbnails)
    {
        _thumbnails = thumbnails;
        Type = type;
        Header = type switch
        {
            ArtworkType.Grid => "BANNER",
            ArtworkType.Hero => "HERO",
            var other => other.ToString().ToUpperInvariant(),
        };

        // Tile shape follows what the slot actually is, so a row of covers and a row of
        // banners do not pretend to be the same thing.
        (TileWidth, TileHeight) = type switch
        {
            ArtworkType.Cover => (110, 165),
            ArtworkType.Grid => (172, 80),
            ArtworkType.Hero => (200, 65),
            ArtworkType.Logo => (140, 80),
            ArtworkType.Icon => (72, 72),
            _ => (150, 90),
        };

        _all = ArtworkRanker.Rank(artwork, type)
            .Select(a => new ArtworkTileViewModel(a, thumbnails, TileWidth * 2))
            .ToList();

        Tiles = new ObservableCollection<ArtworkTileViewModel>(_all.Take(RecommendedCount));
    }

    public ArtworkType Type { get; }

    public string Header { get; }

    public int TileWidth { get; }

    public int TileHeight { get; }

    public ObservableCollection<ArtworkTileViewModel> Tiles { get; }

    public int TotalCount => _all.Count;

    /// <summary>Only worth offering when there is something more to show.</summary>
    public bool CanExpand => _all.Count > RecommendedCount;

    public string ExpandLabel => IsExpanded ? "Show less ↑" : $"Show more ↓  ({_all.Count - RecommendedCount} more)";

    [ObservableProperty]
    private bool isExpanded;

    [ObservableProperty]
    private ArtworkTileViewModel? selected;

    public event EventHandler? SelectionChanged;

    partial void OnIsExpandedChanged(bool value)
    {
        Tiles.Clear();

        foreach (var tile in value ? _all : _all.Take(RecommendedCount)) Tiles.Add(tile);

        OnPropertyChanged(nameof(ExpandLabel));
    }

    partial void OnSelectedChanged(ArtworkTileViewModel? value)
    {
        foreach (var tile in _all) tile.IsSelected = ReferenceEquals(tile, value);

        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Expands in place. The page is never replaced by a full-screen gallery.</summary>
    [RelayCommand]
    private void ToggleExpand() => IsExpanded = !IsExpanded;

    [RelayCommand]
    private void Select(ArtworkTileViewModel? tile)
    {
        // Clicking the selected tile again clears it — nothing is written for this slot.
        Selected = ReferenceEquals(Selected, tile) ? null : tile;
    }

    [RelayCommand]
    private void ImportLocal()
    {
        var dialog = new OpenFileDialog
        {
            Title = $"Select {Header.ToLowerInvariant()} artwork",
            Filter = "Image files|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.gif;*.ico|All files|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() != true) return;

        var file = new FileInfo(dialog.FileName);
        var artwork = new Artwork
        {
            Id = $"local-{Guid.NewGuid():N}", Type = Type,
            Url = new Uri(file.FullName).AbsoluteUri,
            ThumbnailUrl = new Uri(file.FullName).AbsoluteUri,
            Source = "local", Score = 1,
        };
        var tile = new ArtworkTileViewModel(artwork, _thumbnails, TileWidth * 2);
        _all.Insert(0, tile);
        Tiles.Insert(0, tile);
        Selected = tile;

        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(CanExpand));
        OnPropertyChanged(nameof(ExpandLabel));
    }
}
