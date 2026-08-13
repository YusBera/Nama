using System.IO;
using System.Windows.Input;
using Nama.App.Infrastructure;
using Nama.App.Services;
using Nama.Core.Identification;
using Nama.Core.Models;
using Nama.SteamIntegration;

namespace Nama.App.ViewModels;

/// <summary>
/// Owns navigation between the four steps of the flow and the settings overlay.
/// Each step is a separate view model; the shell is the only thing that knows the order.
/// </summary>
public sealed class ShellViewModel : ObservableObject
{
    private readonly AppServices _services;
    private object? _currentPage;
    private bool _isSettingsOpen;
    private string? _banner;
    private bool _isBannerError;

    public ShellViewModel(AppServices services)
    {
        _services = services;

        Settings = new SettingsViewModel(services, CloseSettings);

        OpenSettingsCommand = RelayCommand.Create(OpenSettings);
        CloseSettingsCommand = RelayCommand.Create(CloseSettings);
        DismissBannerCommand = RelayCommand.Create(() => Banner = null);
    }

    /// <summary>The step currently on screen.</summary>
    public object? CurrentPage
    {
        get => _currentPage;
        private set => SetProperty(ref _currentPage, value);
    }

    public SettingsViewModel Settings { get; }

    public bool IsSettingsOpen
    {
        get => _isSettingsOpen;
        private set => SetProperty(ref _isSettingsOpen, value);
    }

    /// <summary>Transient message shown across the top of the window.</summary>
    public string? Banner
    {
        get => _banner;
        private set => SetProperty(ref _banner, value);
    }

    public bool IsBannerError
    {
        get => _isBannerError;
        private set => SetProperty(ref _isBannerError, value);
    }

    public ICommand OpenSettingsCommand { get; }
    public ICommand CloseSettingsCommand { get; }
    public ICommand DismissBannerCommand { get; }

    /// <summary>
    /// Entry point. With a path — the context-menu case — Nama goes straight to
    /// identification; without one it asks the user to pick a game.
    /// </summary>
    public void Start(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path))
            ShowIdentify(path!);
        else
            ShowSelect();
    }

    public void ShowSelect() => CurrentPage = new SelectViewModel(this);

    /// <summary>Moves to identification for a chosen executable or folder.</summary>
    public void ShowIdentify(string path)
    {
        try
        {
            var extractor = new LocalMetadataExtractor();
            var local = extractor.Extract(path);

            var viewModel = new IdentifyViewModel(_services, this, local);
            CurrentPage = viewModel;
            viewModel.BeginIdentification();
        }
        catch (Exception ex) when (ex is FileNotFoundException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            ShowError(ex.Message);
            ShowSelect();
        }
    }

    public void ShowArtwork(LocalMetadata local, Game game)
    {
        var viewModel = new ArtworkViewModel(_services, this, local, game);
        CurrentPage = viewModel;
        viewModel.BeginLoading();
    }

    public void ShowSuccess(AddGameResult result, LocalMetadata local, Game game) =>
        CurrentPage = new SuccessViewModel(_services, this, result, local, game);

    public void OpenSettings()
    {
        Settings.Refresh();
        IsSettingsOpen = true;
    }

    public void CloseSettings() => IsSettingsOpen = false;

    /// <summary>Shows a red banner. Used for failures the user needs to act on.</summary>
    public void ShowError(string message)
    {
        IsBannerError = true;
        Banner = message;
    }

    /// <summary>Shows a neutral banner. Used for warnings that do not block the flow.</summary>
    public void ShowNotice(string message)
    {
        IsBannerError = false;
        Banner = message;
    }

    public void ClearBanner() => Banner = null;
}
