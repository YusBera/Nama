using System.Collections.ObjectModel;
using System.Windows.Input;
using Nama.App.Infrastructure;
using Nama.App.Services;
using Nama.Core.Aggregation;
using Nama.Core.Models;
using Nama.Core.Providers;

namespace Nama.App.ViewModels;

/// <summary>
/// One artwork category. Shows five recommended results, and expands into a scrollable
/// picker holding everything when the user asks for more.
/// </summary>
public sealed class ArtworkSectionViewModel : ObservableObject
{
    private readonly List<ArtworkItemViewModel> _all;
    private bool _isExpanded;
    private ArtworkItemViewModel? _selectedItem;

    public ArtworkSectionViewModel(ArtworkSection section, ImageLoader imageLoader, IArtworkVotingProvider? voting = null)
    {
        Type = section.Type;

        var (width, height) = TileSizeFor(section.Type);
        TileWidth = width;
        TileHeight = height;

        // Decode at roughly twice the tile size so images stay crisp on high-DPI displays
        // without holding full-resolution bitmaps for every thumbnail.
        var decodeWidth = (int)(width * 2);

        _all = section.Items.Select(a => new ArtworkItemViewModel(a, imageLoader, decodeWidth, voting)).ToList();

        Displayed = new ObservableCollection<ArtworkItemViewModel>(
            _all.Take(ArtworkAggregator.RecommendedCount));

        ToggleExpandCommand = RelayCommand.Create(ToggleExpand);
        SelectCommand = new RelayCommand(parameter =>
        {
            if (parameter is ArtworkItemViewModel item) Select(item);
        });
        ClearSelectionCommand = RelayCommand.Create(() => Select(null));

        LoadVisibleImages();
    }

    public ArtworkType Type { get; }

    public string Label => ArtworkTypeInfo.Label(Type);

    public string Description => ArtworkTypeInfo.Description(Type);

    /// <summary>Items currently rendered: the top five, or all of them when expanded.</summary>
    public ObservableCollection<ArtworkItemViewModel> Displayed { get; }

    public int TotalCount => _all.Count;

    public double TileWidth { get; }
    public double TileHeight { get; }

    /// <summary>Height the expanded picker scrolls within, sized to show roughly two rows.</summary>
    public double ExpandedHeight => Math.Min(460, (TileHeight + 58) * 2.4);

    public bool HasMore => _all.Count > ArtworkAggregator.RecommendedCount;

    public bool IsExpanded
    {
        get => _isExpanded;
        private set
        {
            if (!SetProperty(ref _isExpanded, value)) return;
            OnPropertyChanged(nameof(ToggleLabel));
            OnPropertyChanged(nameof(ShowRecommendedCaption));
        }
    }

    public string ToggleLabel => IsExpanded ? "Show less ↑" : "Show more ↓";

    /// <summary>The "Recommended" caption only makes sense while the top five are shown.</summary>
    public bool ShowRecommendedCaption => !IsExpanded;

    public ArtworkItemViewModel? SelectedItem
    {
        get => _selectedItem;
        private set
        {
            if (!SetProperty(ref _selectedItem, value)) return;
            OnPropertyChanged(nameof(HasSelection));
        }
    }

    public bool HasSelection => SelectedItem is not null;

    /// <summary>The chosen artwork, or null when the user skipped this category.</summary>
    public Artwork? Selection => SelectedItem?.Artwork;

    public ICommand ToggleExpandCommand { get; }
    public ICommand SelectCommand { get; }
    public ICommand ClearSelectionCommand { get; }

    /// <summary>Raised when the selection changes, so the page can update its summary.</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>Preselects the top recommendation, matching what the user most often wants.</summary>
    public void SelectTopRecommendation()
    {
        if (_all.Count > 0) Select(_all[0]);
    }

    private void Select(ArtworkItemViewModel? item)
    {
        // Clicking the selected tile again clears it, which is how a user opts out of a category.
        if (item is not null && ReferenceEquals(item, SelectedItem)) item = null;

        foreach (var candidate in _all)
            candidate.IsSelected = ReferenceEquals(candidate, item);

        SelectedItem = item;
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ToggleExpand()
    {
        IsExpanded = !IsExpanded;

        Displayed.Clear();

        foreach (var item in IsExpanded ? _all : _all.Take(ArtworkAggregator.RecommendedCount))
            Displayed.Add(item);

        LoadVisibleImages();
    }

    /// <summary>Starts fetching previews for whatever is on screen now.</summary>
    private void LoadVisibleImages()
    {
        foreach (var item in Displayed)
            _ = item.EnsureImageAsync();
    }

    /// <summary>
    /// Tile dimensions per type, matching each format's real aspect ratio so previews
    /// look like what Steam will actually render.
    /// </summary>
    private static (double Width, double Height) TileSizeFor(ArtworkType type) => type switch
    {
        ArtworkType.Cover => (124, 186),
        ArtworkType.Grid => (196, 92),
        ArtworkType.Hero => (232, 75),
        ArtworkType.Logo => (150, 84),
        ArtworkType.Icon => (76, 76),
        _ => (180, 101),
    };
}
