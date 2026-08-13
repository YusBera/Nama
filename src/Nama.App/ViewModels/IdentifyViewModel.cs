using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using Nama.App.Infrastructure;
using Nama.App.Services;
using Nama.Core.Identification;
using Nama.Core.Models;

namespace Nama.App.ViewModels;

/// <summary>
/// "What game is this?" — shows Nama's guess, lets the user correct it with a
/// debounced search, and confirms one candidate.
/// </summary>
public sealed class IdentifyViewModel : ObservableObject, IDisposable
{
    private readonly AppServices _services;
    private readonly ShellViewModel _shell;
    private readonly LocalMetadata _local;

    /// <summary>Cancels the in-flight search when a newer one supersedes it.</summary>
    private CancellationTokenSource? _searchCts;

    private string _searchText = string.Empty;
    private bool _isBusy;
    private bool _hasSearched;
    private string? _providerWarning;
    private GameCandidateViewModel? _selectedCandidate;

    public IdentifyViewModel(AppServices services, ShellViewModel shell, LocalMetadata local)
    {
        _services = services;
        _shell = shell;
        _local = local;

        Normalization = services.Normalizer.Normalize(local.PrimaryRawName);
        _searchText = Normalization.Normalized;

        BackCommand = RelayCommand.Create(shell.ShowSelect);
        ConfirmCommand = new RelayCommand(_ => Confirm(), _ => SelectedCandidate is not null);
        UseTypedNameCommand = RelayCommand.Create(UseTypedName);
    }

    public ObservableCollection<GameCandidateViewModel> Candidates { get; } = [];

    public Core.Normalization.NormalizationResult Normalization { get; }

    /// <summary>The file the user picked, shown so they can confirm Nama has the right target.</summary>
    public string SourceFileName => Path.GetFileName(_local.Target.ExecutablePath);

    public string SourceFolder => _local.Target.InstallRoot;

    /// <summary>"ELDEN-RING-v1.12.2-FITGIRL" — what the name was cleaned from.</summary>
    public string RawName => Normalization.Raw;

    /// <summary>True when cleaning actually changed the name, so the before/after is worth showing.</summary>
    public bool ShowNormalization =>
        !string.Equals(Normalization.Raw, Normalization.Normalized, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The search term. Assigning restarts the debounce timer rather than searching
    /// immediately, so typing does not fire a request per keystroke.
    /// </summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetProperty(ref _searchText, value)) return;
            _ = DebouncedSearchAsync(value);
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value)) OnPropertyChanged(nameof(ShowEmptyState));
        }
    }

    /// <summary>Shown when a completed search returned nothing.</summary>
    public bool ShowEmptyState => !IsBusy && _hasSearched && Candidates.Count == 0;

    /// <summary>Populated when a provider failed, so the user knows the list may be short.</summary>
    public string? ProviderWarning
    {
        get => _providerWarning;
        private set => SetProperty(ref _providerWarning, value);
    }

    public GameCandidateViewModel? SelectedCandidate
    {
        get => _selectedCandidate;
        set
        {
            var previous = _selectedCandidate;
            if (!SetProperty(ref _selectedCandidate, value)) return;

            if (previous is not null) previous.IsSelected = false;
            if (value is not null) value.IsSelected = true;

            (ConfirmCommand as RelayCommand)?.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(CanConfirm));
        }
    }

    public bool CanConfirm => SelectedCandidate is not null;

    public ICommand BackCommand { get; }
    public ICommand ConfirmCommand { get; }

    /// <summary>Escape hatch: proceed with exactly what the user typed, matching nothing.</summary>
    public ICommand UseTypedNameCommand { get; }

    /// <summary>Runs the full identification pipeline for the local target.</summary>
    public async void BeginIdentification()
    {
        IsBusy = true;

        try
        {
            var identifier = _services.CreateIdentifier();
            var result = await identifier.IdentifyAsync(_local).ConfigureAwait(true);

            Populate(result.Candidates);
            ReportFailures(result.Failures.Select(f => (f.Provider, f.Message)));

            // Preselect only when the top match is both strong and clearly ahead, so the
            // user is never nudged toward a coin-flip guess.
            if (result.IsConfident && Candidates.Count > 0)
                SelectedCandidate = Candidates[0];
        }
        catch (Exception ex)
        {
            _shell.ShowError($"Identification failed: {ex.Message}");
        }
        finally
        {
            _hasSearched = true;
            IsBusy = false;
        }
    }

    /// <summary>
    /// Waits out the debounce interval, then searches — unless another keystroke
    /// arrives first, which cancels this attempt.
    /// </summary>
    private async Task DebouncedSearchAsync(string query)
    {
        _searchCts?.Cancel();
        _searchCts?.Dispose();

        var cts = new CancellationTokenSource();
        _searchCts = cts;

        try
        {
            await Task.Delay(Math.Clamp(_services.Settings.SearchDebounceMs, 100, 1000), cts.Token)
                .ConfigureAwait(true);

            if (string.IsNullOrWhiteSpace(query))
            {
                Candidates.Clear();
                OnPropertyChanged(nameof(ShowEmptyState));
                return;
            }

            IsBusy = true;

            var identifier = _services.CreateIdentifier();
            var results = await identifier.SearchAsync(query, cts.Token).ConfigureAwait(true);

            if (cts.Token.IsCancellationRequested) return;

            Populate(results);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer keystroke.
        }
        catch (Exception ex)
        {
            _shell.ShowError($"Search failed: {ex.Message}");
        }
        finally
        {
            if (!cts.Token.IsCancellationRequested)
            {
                _hasSearched = true;
                IsBusy = false;
            }
        }
    }

    private void Populate(IReadOnlyList<Game> games)
    {
        var previouslySelected = SelectedCandidate?.Game.CanonicalName;

        SelectedCandidate = null;
        Candidates.Clear();

        foreach (var game in games)
        {
            var candidate = new GameCandidateViewModel(game, _services.ImageLoader);
            Candidates.Add(candidate);

            // Thumbnails load in the background; the list is usable immediately.
            _ = candidate.EnsureThumbnailAsync();
        }

        // Keep the user's choice across a re-search when the same game is still listed.
        if (previouslySelected is not null)
            SelectedCandidate = Candidates.FirstOrDefault(c =>
                string.Equals(c.Game.CanonicalName, previouslySelected, StringComparison.OrdinalIgnoreCase));

        OnPropertyChanged(nameof(ShowEmptyState));
    }

    private void ReportFailures(IEnumerable<(string Provider, string Message)> failures)
    {
        var list = failures.ToList();

        ProviderWarning = list.Count == 0
            ? null
            : $"{string.Join(", ", list.Select(f => f.Provider))} could not be reached, so some matches may be missing.";
    }

    private void Confirm()
    {
        if (SelectedCandidate is null) return;

        _shell.ClearBanner();
        _shell.ShowArtwork(_local, SelectedCandidate.Game);
    }

    /// <summary>
    /// Builds a bare game record from the typed text so a user with an obscure title can
    /// still add it, picking artwork by name search alone.
    /// </summary>
    private void UseTypedName()
    {
        var name = SearchText.Trim();
        if (name.Length == 0) return;

        _shell.ClearBanner();
        _shell.ShowArtwork(_local, new Game
        {
            CanonicalName = name,
            DisplayName = name,
            Confidence = 1.0,
        });
    }

    public void Dispose()
    {
        _searchCts?.Cancel();
        _searchCts?.Dispose();
    }
}
