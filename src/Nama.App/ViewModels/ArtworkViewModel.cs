using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nama.Core.Models;
using Nama.Steam.Models;
using Nama.Steam.Writing;

namespace Nama.App.ViewModels;

/// <summary>
/// Step 3: pick artwork, adjust the Steam name, add it.
/// <para>
/// This is also where every way the write can be refused surfaces — a duplicate entry,
/// Steam being open, a file Nama does not fully understand. Each gets an explicit choice
/// rather than a failure message.
/// </para>
/// </summary>
public sealed partial class ArtworkViewModel(ShellViewModel shell) : ObservableObject
{
    public ObservableCollection<ArtworkSectionViewModel> Sections { get; } = [];

    [ObservableProperty]
    private string displayName = string.Empty;

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private string? busyMessage;

    [ObservableProperty]
    private string? errorMessage;

    [ObservableProperty]
    private string? noticeMessage;

    [ObservableProperty]
    private bool hasArtwork;

    /// <summary>Set when the game is already in the library. Drives the duplicate prompt.</summary>
    [ObservableProperty]
    private SteamShortcutSummary? duplicate;

    /// <summary>Set when Steam is open and would discard the write.</summary>
    [ObservableProperty]
    private bool steamIsRunning;

    public bool ShowDuplicatePrompt => Duplicate is not null;

    public bool CanAdd => !IsBusy && !string.IsNullOrWhiteSpace(DisplayName);

    partial void OnDuplicateChanged(SteamShortcutSummary? value) => OnPropertyChanged(nameof(ShowDuplicatePrompt));

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanAdd));

    partial void OnDisplayNameChanged(string value) => OnPropertyChanged(nameof(CanAdd));

    /// <summary>Fetches artwork for the confirmed game and builds the sections.</summary>
    public async Task LoadAsync(GameCandidate game)
    {
        Clear();

        DisplayName = game.Name;
        IsBusy = true;
        BusyMessage = "Finding artwork…";

        try
        {
            var reference = Game.FromCandidate(game).Ref;
            var collection = await Task.Run(() => shell.Services.Artwork.GetArtworkAsync(reference)).ConfigureAwait(true);

            ArtworkType[] steamSlots =
                [ArtworkType.Icon, ArtworkType.Grid, ArtworkType.Cover, ArtworkType.Hero, ArtworkType.Logo];

            foreach (var type in steamSlots)
            {
                var section = new ArtworkSectionViewModel(type, collection.OfType(type), shell.Services.Thumbnails);
                section.SelectionChanged += (_, _) => OnPropertyChanged(nameof(SelectedCount));
                Sections.Add(section);

                // Pre-select the top recommendation: the common case is accepting it.
                section.Selected = section.Tiles.FirstOrDefault();
            }

            HasArtwork = Sections.Any(s => s.TotalCount > 0);
            OnPropertyChanged(nameof(SelectedCount));

            if (!HasArtwork)
            {
                NoticeMessage = "No artwork found. The game can still be added without it.";
            }
            else if (shell.Services.ArtworkIsLimited)
            {
                NoticeMessage = "Add a SteamGridDB key in settings for many more artwork options.";
            }

            if (collection.FailedProviders.Count > 0)
            {
                NoticeMessage = $"Some artwork sources did not respond: {string.Join(", ", collection.FailedProviders)}";
            }
        }
        catch (Exception e)
        {
            ErrorMessage = $"Could not load artwork: {e.Message}";
        }
        finally
        {
            IsBusy = false;
            BusyMessage = null;
        }
    }

    public int SelectedCount => Sections.Count(s => s.Selected is not null);

    private Dictionary<ArtworkType, Artwork> BuildSelections() =>
        Sections
            .Where(s => s.Selected is not null)
            .ToDictionary(s => s.Type, s => s.Selected!.Artwork);

    [RelayCommand]
    private Task AddAsync() => WriteAsync(DuplicateAction.Fail);

    [RelayCommand]
    private Task UpdateArtworkOnlyAsync() => WriteAsync(DuplicateAction.UpdateArtwork);

    [RelayCommand]
    private Task ReplaceEntryAsync() => WriteAsync(DuplicateAction.ReplaceEntry);

    [RelayCommand]
    private void CancelDuplicate() => Duplicate = null;

    /// <summary>Backs out of the close-Steam prompt without shutting anything down.</summary>
    [RelayCommand]
    private void DismissSteamPrompt() => SteamIsRunning = false;

    /// <summary>Closes Steam on the user's behalf, then retries.</summary>
    [RelayCommand]
    private async Task CloseSteamAndRetryAsync()
    {
        var installation = shell.Services.Steam.FindSteamInstallation();
        if (installation is null)
        {
            ErrorMessage = "Steam installation not found.";
            return;
        }

        IsBusy = true;
        BusyMessage = "Closing Steam…";

        try
        {
            if (!await shell.Services.Steam.ShutdownSteamAsync(installation).ConfigureAwait(true))
            {
                ErrorMessage = "Steam did not close. Close it manually and try again.";
                return;
            }

            SteamIsRunning = false;
        }
        finally
        {
            IsBusy = false;
            BusyMessage = null;
        }

        await WriteAsync(Duplicate is not null ? DuplicateAction.UpdateArtwork : DuplicateAction.Fail)
            .ConfigureAwait(true);
    }

    private async Task WriteAsync(DuplicateAction onDuplicate)
    {
        if (shell.Extraction is null) return;

        IsBusy = true;
        ErrorMessage = null;
        BusyMessage = "Adding to Steam…";

        try
        {
            var installation = shell.Services.Steam.FindSteamInstallation();
            if (installation is null)
            {
                ErrorMessage = "Could not find your Steam installation.";
                return;
            }

            var account = shell.Services.Steam.ResolveAccount(
                shell.Services.Steam.FindLibraryData(installation),
                shell.Services.Settings.PreferredSteamAccountId);

            if (account is null)
            {
                ErrorMessage = "No Steam account was found to add the game to.";
                return;
            }

            var request = new ShortcutRequest
            {
                ExecutablePath = shell.Extraction.ExecutablePath,
                DisplayName = DisplayName.Trim(),
                StartDirectory = shell.Extraction.StartDirectory,
                Artwork = BuildSelections(),
                OnDuplicate = onDuplicate,
            };

            var result = await shell.Services.Steam.AddOrUpdateShortcutAsync(
                account, request, shell.Services.Downloader).ConfigureAwait(true);

            if (result.Success)
            {
                Duplicate = null;
                shell.SuccessStep.Show(DisplayName.Trim(), result, installation);
                shell.ShowSuccess();
                return;
            }

            // Each refusal is a decision for the user, not a dead end.
            switch (result.BlockReason)
            {
                case WriteBlockReason.SteamRunning:
                    SteamIsRunning = true;
                    break;

                default:
                    if (result.ExistingEntry is not null) Duplicate = result.ExistingEntry;
                    else ErrorMessage = result.Error;
                    break;
            }
        }
        catch (Exception e)
        {
            ErrorMessage = e.Message;
        }
        finally
        {
            IsBusy = false;
            BusyMessage = null;
        }
    }

    public void Clear()
    {
        Sections.Clear();
        DisplayName = string.Empty;
        ErrorMessage = null;
        NoticeMessage = null;
        Duplicate = null;
        SteamIsRunning = false;
        HasArtwork = false;
    }
}
