using System.Net.Http;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Nama.App.Infrastructure;
using Nama.App.Services;
using Nama.Core.Models;
using Nama.Core.Providers;
using Nama.Providers.NamaDb;

namespace Nama.App.ViewModels;

/// <summary>A single selectable artwork tile.</summary>
public sealed class ArtworkItemViewModel : ObservableObject
{
    private readonly ImageLoader _imageLoader;
    private readonly int _decodeWidth;
    private readonly IArtworkVotingProvider? _voting;
    private BitmapSource? _image;
    private bool _isSelected;
    private bool _isLoading;
    private bool _failedToLoad;
    private bool _requested;
    private int _upvotes;
    private int _downvotes;
    private int _currentVote;
    private string? _voteError;

    public ArtworkItemViewModel(Artwork artwork, ImageLoader imageLoader, int decodeWidth, IArtworkVotingProvider? voting = null)
    {
        Artwork = artwork;
        _imageLoader = imageLoader;
        _decodeWidth = decodeWidth;
        _voting = voting;
        _upvotes = artwork.Upvotes ?? 0;
        _downvotes = artwork.Downvotes ?? 0;
        _currentVote = artwork.CurrentUserVote;

        // Clicking the active arrow again retracts the vote, which is what the zero value means.
        VoteUpCommand = AsyncRelayCommand.Create(() => VoteAsync(ArtworkVoteValue.Up), () => CanVote);
        VoteDownCommand = AsyncRelayCommand.Create(() => VoteAsync(ArtworkVoteValue.Down), () => CanVote);
    }

    public Artwork Artwork { get; }

    /// <summary>Provider badge shown in the tile corner.</summary>
    public string Source => Artwork.Source;

    public string? Author => string.IsNullOrWhiteSpace(Artwork.Author) ? null : $"by {Artwork.Author}";

    public string Dimensions => Artwork.Dimensions;

    /// <summary>True for artwork from a provider that accepts votes from a linked account.</summary>
    public bool CanVote => _voting is not null && Artwork.CanVote;

    /// <summary>True when the tile carries vote counts, whether or not this user may vote.</summary>
    public bool HasVotes => Artwork.Upvotes is not null && Artwork.Downvotes is not null;

    public int Upvotes
    {
        get => _upvotes;
        private set { if (SetProperty(ref _upvotes, value)) OnPropertyChanged(nameof(VoteSummary)); }
    }

    public int Downvotes
    {
        get => _downvotes;
        private set { if (SetProperty(ref _downvotes, value)) OnPropertyChanged(nameof(VoteSummary)); }
    }

    public int CurrentVote
    {
        get => _currentVote;
        private set
        {
            if (!SetProperty(ref _currentVote, value)) return;
            OnPropertyChanged(nameof(IsUpvoted));
            OnPropertyChanged(nameof(IsDownvoted));
        }
    }

    public bool IsUpvoted => CurrentVote > 0;
    public bool IsDownvoted => CurrentVote < 0;

    /// <summary>Set when the last vote failed, so the tile can explain itself without a dialog.</summary>
    public string? VoteError
    {
        get => _voteError;
        private set => SetProperty(ref _voteError, value);
    }

    public ICommand VoteUpCommand { get; }
    public ICommand VoteDownCommand { get; }

    public string? VoteSummary => HasVotes ? $"▲ {Upvotes}  ▼ {Downvotes}" : null;

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
            var image = await _imageLoader.LoadAsync(Artwork.PreviewUrl, _decodeWidth, ct).ConfigureAwait(true);

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

    /// <summary>
    /// Casts, changes or retracts a vote. The counts move optimistically so the tile responds
    /// instantly, then settle on whatever the server reports; a failure rolls them back.
    /// </summary>
    private async Task VoteAsync(ArtworkVoteValue value)
    {
        if (_voting is null || !Artwork.CanVote) return;

        var previous = (Upvotes, Downvotes, CurrentVote);
        // Pressing the arrow that is already active means "take my vote back".
        var intended = CurrentVote == (int)value ? ArtworkVoteValue.None : value;

        VoteError = null;
        ApplyOptimistic(intended);

        try
        {
            var result = await _voting.VoteAsync(Artwork.Id, intended).ConfigureAwait(true);
            Upvotes = result.Upvotes;
            Downvotes = result.Downvotes;
            CurrentVote = (int)result.CurrentVote;
            Artwork.CurrentUserVote = CurrentVote;
        }
        catch (Exception ex) when (ex is NamaDbNotLinkedException or HttpRequestException or TaskCanceledException)
        {
            (Upvotes, Downvotes, CurrentVote) = previous;
            Artwork.CurrentUserVote = previous.CurrentVote;
            VoteError = ex is NamaDbNotLinkedException
                ? "Link Nama to NamaDB in Settings to vote."
                : "Could not reach NamaDB.";
        }
    }

    /// <summary>Moves the counts as if the server had accepted <paramref name="intended"/>.</summary>
    private void ApplyOptimistic(ArtworkVoteValue intended)
    {
        if (CurrentVote > 0) Upvotes = Math.Max(0, Upvotes - 1);
        if (CurrentVote < 0) Downvotes = Math.Max(0, Downvotes - 1);

        if (intended == ArtworkVoteValue.Up) Upvotes++;
        if (intended == ArtworkVoteValue.Down) Downvotes++;

        CurrentVote = (int)intended;
    }
}
