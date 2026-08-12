using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nama.Core.Models;

namespace Nama.App.ViewModels;

/// <summary>
/// Step 2: "What game is this?" — Nama's guess, and the means to correct it.
/// </summary>
public sealed partial class IdentifyViewModel(ShellViewModel shell) : ObservableObject
{
    /// <summary>
    /// Debounce for the search box. Long enough that ordinary typing produces one request
    /// rather than one per keystroke, short enough to feel immediate.
    /// </summary>
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(300);

    private CancellationTokenSource? _pending;

    /// <summary>Suppresses the debounce while the box is being filled in programmatically.</summary>
    private bool _suppressSearch;

    public ObservableCollection<GameMatchViewModel> Results { get; } = [];

    [ObservableProperty]
    private string query = string.Empty;

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private string? statusMessage;

    [ObservableProperty]
    private string? errorMessage;

    [ObservableProperty]
    private GameMatchViewModel? selectedResult;

    [ObservableProperty]
    private string? sourcePath;

    public bool HasResults => Results.Count > 0;

    public bool CanConfirm => SelectedResult is not null;

    partial void OnSelectedResultChanged(GameMatchViewModel? value)
    {
        foreach (var result in Results) result.IsSelected = ReferenceEquals(result, value);
        OnPropertyChanged(nameof(CanConfirm));
    }

    partial void OnQueryChanged(string value)
    {
        if (_suppressSearch) return;

        _ = DebouncedSearchAsync(value);
    }

    /// <summary>Reads the path and runs the first search.</summary>
    public async Task LoadAsync(string path)
    {
        IsBusy = true;
        ErrorMessage = null;
        StatusMessage = "Reading files…";
        SourcePath = path;
        Results.Clear();

        try
        {
            var result = await Task.Run(() => shell.Services.Identifier.IdentifyAsync(path)).ConfigureAwait(true);

            shell.SetExtraction(result.Extraction);

            _suppressSearch = true;
            Query = result.Extraction.BestGuess;
            _suppressSearch = false;

            if (result.Extraction.Warning is not null) ErrorMessage = result.Extraction.Warning;

            Populate(result.Matches);

            StatusMessage = Results.Count == 0
                ? "No matches found. Try editing the name above."
                : null;

            if (result.FailedProviders.Count > 0)
            {
                StatusMessage = $"Some sources did not respond: {string.Join(", ", result.FailedProviders)}";
            }
        }
        catch (Exception e)
        {
            ErrorMessage = $"Could not read '{path}': {e.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task DebouncedSearchAsync(string text)
    {
        _pending?.Cancel();
        _pending?.Dispose();

        var cts = new CancellationTokenSource();
        _pending = cts;

        try
        {
            await Task.Delay(Debounce, cts.Token).ConfigureAwait(true);

            if (string.IsNullOrWhiteSpace(text)) return;

            IsBusy = true;
            StatusMessage = null;

            var matches = await Task.Run(
                () => shell.Services.Identifier.SearchAsync(text, cts.Token), cts.Token).ConfigureAwait(true);

            if (cts.Token.IsCancellationRequested) return;

            Populate(matches);
            StatusMessage = Results.Count == 0 ? "No matches for that name." : null;
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer keystroke.
        }
        catch (Exception e)
        {
            ErrorMessage = e.Message;
        }
        finally
        {
            if (ReferenceEquals(_pending, cts)) IsBusy = false;
        }
    }

    private void Populate(IReadOnlyList<GameCandidate> matches)
    {
        Results.Clear();

        foreach (var match in matches.Take(30))
        {
            Results.Add(new GameMatchViewModel(match, shell.Services.Thumbnails));
        }

        // Always pre-select the best match. Confirmation is still required — the user has
        // to press Continue — but landing on a screen whose primary button is dead, with
        // no hint that a row must be clicked first, is a worse kind of "confirmation".
        SelectedResult = Results.FirstOrDefault();

        OnPropertyChanged(nameof(HasResults));
    }

    /// <summary>Single click: choose a row without committing to it.</summary>
    [RelayCommand]
    private void Select(GameMatchViewModel? result) => SelectedResult = result;

    /// <summary>Double click: choose and move on, since that is the obvious shortcut.</summary>
    [RelayCommand]
    private async Task ChooseAsync(GameMatchViewModel? result)
    {
        if (result is null) return;

        SelectedResult = result;
        await shell.ConfirmGameAsync(result.Candidate).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task ConfirmAsync()
    {
        if (SelectedResult is null) return;

        await shell.ConfirmGameAsync(SelectedResult.Candidate).ConfigureAwait(true);
    }

    public void Clear()
    {
        _pending?.Cancel();
        Results.Clear();
        Query = string.Empty;
        SelectedResult = null;
        StatusMessage = null;
        ErrorMessage = null;
        SourcePath = null;
    }
}
