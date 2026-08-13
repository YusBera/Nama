using System.Collections.ObjectModel;
using System.Windows.Input;
using Nama.App.Infrastructure;
using Nama.App.Services;
using Nama.Core.Identification;
using Nama.Core.Models;
using Nama.SteamIntegration;

namespace Nama.App.ViewModels;

/// <summary>How the user chose to resolve an existing library entry.</summary>
public enum DuplicateResolution
{
    /// <summary>Nothing decided yet; the prompt is showing.</summary>
    Pending,

    /// <summary>Keep the entry, write the new artwork over it.</summary>
    UpdateArtwork,

    /// <summary>Rewrite the entry entirely, including target and name.</summary>
    ReplaceEntry,
}

/// <summary>
/// The artwork picker plus the final commit step: choose art, edit the Steam name,
/// resolve any duplicate, and write everything into the library.
/// </summary>
public sealed class ArtworkViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly ShellViewModel _shell;
    private readonly LocalMetadata _local;
    private readonly Game _game;

    private string _displayName;
    private bool _isLoading;
    private bool _isCommitting;
    private string? _loadWarning;
    private bool _hasNoArtwork;

    private SteamInstallation? _installation;
    private SteamUser? _steamUser;
    private ExistingEntry? _existingEntry;
    private DuplicateResolution _resolution = DuplicateResolution.Pending;
    private bool _isDuplicatePromptOpen;

    public ArtworkViewModel(AppServices services, ShellViewModel shell, LocalMetadata local, Game game)
    {
        _services = services;
        _shell = shell;
        _local = local;
        _game = game;

        _displayName = game.EffectiveDisplayName;

        BackCommand = RelayCommand.Create(() => shell.ShowIdentify(local.Target.ExecutablePath));
        AddToSteamCommand = new AsyncRelayCommand(_ => CommitAsync(), _ => CanCommit);

        ChooseUpdateArtworkCommand = RelayCommand.Create(() => ResolveDuplicate(DuplicateResolution.UpdateArtwork));
        ChooseReplaceEntryCommand = RelayCommand.Create(() => ResolveDuplicate(DuplicateResolution.ReplaceEntry));
        CancelDuplicateCommand = RelayCommand.Create(CancelDuplicate);
    }

    public ObservableCollection<ArtworkSectionViewModel> Sections { get; } = [];

    public Game Game => _game;

    /// <summary>The name Nama detected, shown above the editable field.</summary>
    public string DetectedName => _game.CanonicalName;

    /// <summary>
    /// The name that will appear in Steam. Independent of the executable's filename and
    /// freely editable before committing.
    /// </summary>
    public string DisplayName
    {
        get => _displayName;
        set
        {
            if (!SetProperty(ref _displayName, value)) return;
            OnPropertyChanged(nameof(CanCommit));
            (AddToSteamCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public string ExecutablePath => _local.Target.ExecutablePath;

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value)) OnPropertyChanged(nameof(CanCommit));
            (AddToSteamCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public bool IsCommitting
    {
        get => _isCommitting;
        private set => SetProperty(ref _isCommitting, value);
    }

    /// <summary>Non-fatal problems from artwork providers.</summary>
    public string? LoadWarning
    {
        get => _loadWarning;
        private set => SetProperty(ref _loadWarning, value);
    }

    /// <summary>True when no provider returned any artwork; the game can still be added.</summary>
    public bool HasNoArtwork
    {
        get => _hasNoArtwork;
        private set => SetProperty(ref _hasNoArtwork, value);
    }

    /// <summary>"Icon, Grid, Cover" — a running summary of what will be applied.</summary>
    public string SelectionSummary
    {
        get
        {
            var chosen = Sections
                .Where(s => s.HasSelection)
                .Select(s => ArtworkTypeInfo.Label(s.Type).ToLowerInvariant())
                .ToList();

            return chosen.Count == 0
                ? "No artwork selected"
                : $"{chosen.Count} selected: {string.Join(", ", chosen)}";
        }
    }

    public bool CanCommit => !IsLoading && !IsCommitting && !string.IsNullOrWhiteSpace(DisplayName) && _steamUser is not null;

    /// <summary>Shown when Steam could not be found, since nothing can be written without it.</summary>
    public string? SteamProblem { get; private set; }

    public bool HasSteamProblem => SteamProblem is not null;

    /// <summary>True when Steam is running, which means changes only appear after a restart.</summary>
    public bool IsSteamRunning { get; private set; }

    public string? SteamAccountLabel => _steamUser?.DisplayLabel;

    public bool IsDuplicatePromptOpen
    {
        get => _isDuplicatePromptOpen;
        private set => SetProperty(ref _isDuplicatePromptOpen, value);
    }

    /// <summary>Message explaining what already exists in the library.</summary>
    public string DuplicateMessage => _existingEntry is { } entry
        ? entry.MatchKind switch
        {
            DuplicateMatch.SameExecutable =>
                $"\"{entry.Shortcut.AppName}\" already points at this game in your Steam library.",
            DuplicateMatch.SameAppId =>
                $"\"{entry.Shortcut.AppName}\" already occupies this entry in your Steam library.",
            _ => $"\"{entry.Shortcut.AppName}\" already exists in your Steam library.",
        }
        : string.Empty;

    public ICommand BackCommand { get; }
    public ICommand AddToSteamCommand { get; }
    public ICommand ChooseUpdateArtworkCommand { get; }
    public ICommand ChooseReplaceEntryCommand { get; }
    public ICommand CancelDuplicateCommand { get; }

    /// <summary>Locates Steam and fetches artwork. Both run as soon as the page appears.</summary>
    public async void BeginLoading()
    {
        IsLoading = true;
        DetectSteam();

        try
        {
            var aggregator = _services.CreateArtworkAggregator(_local.Target);
            var collection = await aggregator.CollectAsync(_game).ConfigureAwait(true);

            Sections.Clear();

            foreach (var section in collection.Sections)
            {
                var viewModel = new ArtworkSectionViewModel(section, _services.ImageLoader);
                viewModel.SelectionChanged += (_, _) => OnPropertyChanged(nameof(SelectionSummary));

                // The top result is preselected so the common case is a single click on
                // "Add to Steam"; the user can clear it by clicking the tile again.
                viewModel.SelectTopRecommendation();

                Sections.Add(viewModel);
            }

            HasNoArtwork = Sections.Count == 0;
            OnPropertyChanged(nameof(SelectionSummary));

            LoadWarning = collection.Failures.Count == 0
                ? BuildMissingProviderHint()
                : $"{string.Join(", ", collection.Failures.Select(f => f.Provider))} could not be reached, so fewer options are shown.";
        }
        catch (Exception ex)
        {
            _shell.ShowError($"Could not load artwork: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Nudges the user toward a SteamGridDB key when artwork is thin without one.</summary>
    private string? BuildMissingProviderHint()
    {
        if (_services.Providers.HasSteamGridDbKey) return null;

        return "Add a free SteamGridDB key in Settings for many more artwork options.";
    }

    private void DetectSteam()
    {
        _installation = _services.SteamManager.FindSteamInstallation(_services.Settings.SteamPathOverride);

        if (_installation is null)
        {
            SteamProblem = "Nama could not find your Steam installation. Set the path in Settings.";
        }
        else
        {
            _steamUser = _services.SteamManager.FindLibraryData(
                _installation, _services.Settings.PreferredSteamUserId);

            SteamProblem = _steamUser is null
                ? "Nama found Steam but no logged-in user profile. Sign in to Steam once, then try again."
                : null;
        }

        IsSteamRunning = SteamInstallation.IsSteamRunning();

        OnPropertyChanged(nameof(SteamProblem));
        OnPropertyChanged(nameof(HasSteamProblem));
        OnPropertyChanged(nameof(IsSteamRunning));
        OnPropertyChanged(nameof(SteamAccountLabel));
        OnPropertyChanged(nameof(CanCommit));
    }

    /// <summary>
    /// Writes the shortcut and artwork. Checks for an existing entry first and hands the
    /// decision to the user rather than ever creating a duplicate silently.
    /// </summary>
    private async Task CommitAsync()
    {
        if (_steamUser is null) return;

        var name = DisplayName.Trim();
        if (name.Length == 0) return;

        try
        {
            // Ask about duplicates once; a resolved choice carries through the retry.
            if (_resolution == DuplicateResolution.Pending)
            {
                _existingEntry = _services.SteamManager.DetectExistingEntry(
                    _steamUser, _local.Target.ExecutablePath, name);

                if (_existingEntry is not null)
                {
                    OnPropertyChanged(nameof(DuplicateMessage));
                    IsDuplicatePromptOpen = true;
                    return;
                }
            }

            IsCommitting = true;

            var selections = Sections
                .Where(s => s.Selection is not null && ArtworkTypeInfo.SteamApplicable.Contains(s.Type))
                .ToDictionary(s => s.Type, s => s.Selection!);

            var result = await WriteToSteamAsync(_steamUser, name, selections).ConfigureAwait(true);

            _shell.ShowSuccess(result, _local, _game);
        }
        catch (SteamException ex)
        {
            _shell.ShowError(ex.Message);
        }
        catch (Exception ex)
        {
            _shell.ShowError($"Could not add the game to Steam: {ex.Message}");
        }
        finally
        {
            IsCommitting = false;
        }
    }

    /// <summary>
    /// Performs the actual writes. Artwork is applied before the shortcut is saved so the
    /// icon path is already set when the entry lands in the file.
    /// </summary>
    private async Task<AddGameResult> WriteToSteamAsync(
        SteamUser user,
        string name,
        Dictionary<ArtworkType, Artwork> selections)
    {
        var manager = _services.SteamManager;
        var backup = _services.Settings.BackupShortcutsFile;

        SteamShortcut shortcut;
        ShortcutAction action;
        int previousAppId;

        if (_existingEntry is { } existing && _resolution == DuplicateResolution.UpdateArtwork)
        {
            // Keep the entry exactly as Steam has it; only the artwork changes.
            shortcut = existing.Shortcut;
            previousAppId = shortcut.AppId;
            action = ShortcutAction.Updated;
        }
        else if (_existingEntry is { } replaced && _resolution == DuplicateResolution.ReplaceEntry)
        {
            previousAppId = replaced.Shortcut.AppId;
            shortcut = SteamShortcut.Create(_local.Target.ExecutablePath, _local.Target.StartDirectory, name);
            action = ShortcutAction.Updated;
        }
        else
        {
            shortcut = SteamShortcut.Create(_local.Target.ExecutablePath, _local.Target.StartDirectory, name);
            previousAppId = shortcut.AppId;
            action = ShortcutAction.Created;
        }

        var (applied, failed) = await manager
            .ApplyArtworkAsync(user, shortcut, selections)
            .ConfigureAwait(true);

        if (action == ShortcutAction.Created)
            manager.AddShortcut(user, shortcut, backup);
        else
            manager.UpdateShortcut(user, shortcut, previousAppId, backup);

        return new AddGameResult
        {
            Shortcut = shortcut,
            Action = action,
            AppliedArtwork = applied,
            FailedArtwork = failed,
            RequiresSteamRestart = SteamInstallation.IsSteamRunning(),
        };
    }

    private void ResolveDuplicate(DuplicateResolution resolution)
    {
        _resolution = resolution;
        IsDuplicatePromptOpen = false;

        // Re-enter the commit path now that the decision is recorded.
        if (AddToSteamCommand.CanExecute(null)) AddToSteamCommand.Execute(null);
    }

    private void CancelDuplicate()
    {
        IsDuplicatePromptOpen = false;
        _resolution = DuplicateResolution.Pending;
        _existingEntry = null;
    }
}
