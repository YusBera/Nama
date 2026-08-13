using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Input;
using Microsoft.Win32;
using Nama.App.Infrastructure;
using Nama.App.Services;
using Nama.App.WindowsIntegration;
using Nama.Providers;

namespace Nama.App.ViewModels;

/// <summary>
/// The settings overlay. Intentionally short: credentials, provider toggles, Explorer
/// integration and a cache reset — nothing that turns Nama into a configuration app.
/// </summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly Action _close;

    private string? _steamGridDbApiKey;
    private string? _igdbClientId;
    private string? _igdbClientSecret;
    private string? _steamPathOverride;
    private bool _contextMenuInstalled;
    private string? _status;

    public SettingsViewModel(AppServices services, Action close)
    {
        _services = services;
        _close = close;

        CloseCommand = RelayCommand.Create(Save);
        InstallContextMenuCommand = RelayCommand.Create(InstallContextMenu);
        RemoveContextMenuCommand = RelayCommand.Create(RemoveContextMenu);
        BrowseSteamPathCommand = RelayCommand.Create(BrowseSteamPath);
        ClearCacheCommand = RelayCommand.Create(ClearCache);
        OpenSteamGridDbKeyPageCommand = RelayCommand.Create(
            () => OpenUrl("https://www.steamgriddb.com/profile/preferences/api"));

        Refresh();
    }

    /// <summary>Reloads the form from current settings and system state.</summary>
    public void Refresh()
    {
        var settings = _services.Settings;

        _steamGridDbApiKey = settings.SteamGridDbApiKey;
        _igdbClientId = settings.IgdbClientId;
        _igdbClientSecret = settings.IgdbClientSecret;
        _steamPathOverride = settings.SteamPathOverride;
        _contextMenuInstalled = ContextMenuInstaller.IsInstalled();

        OnPropertyChanged(nameof(SteamGridDbApiKey));
        OnPropertyChanged(nameof(IgdbClientId));
        OnPropertyChanged(nameof(IgdbClientSecret));
        OnPropertyChanged(nameof(SteamPathOverride));
        OnPropertyChanged(nameof(ContextMenuInstalled));

        RefreshProviders();
        RefreshCacheSize();
    }

    public string? SteamGridDbApiKey
    {
        get => _steamGridDbApiKey;
        set
        {
            if (SetProperty(ref _steamGridDbApiKey, value)) Save();
        }
    }

    public string? IgdbClientId
    {
        get => _igdbClientId;
        set
        {
            if (SetProperty(ref _igdbClientId, value)) Save();
        }
    }

    public string? IgdbClientSecret
    {
        get => _igdbClientSecret;
        set
        {
            if (SetProperty(ref _igdbClientSecret, value)) Save();
        }
    }

    public string? SteamPathOverride
    {
        get => _steamPathOverride;
        set
        {
            if (SetProperty(ref _steamPathOverride, value)) Save();
        }
    }

    public bool ContextMenuInstalled
    {
        get => _contextMenuInstalled;
        private set => SetProperty(ref _contextMenuInstalled, value);
    }

    /// <summary>Live state of each provider, including why a disabled one cannot run.</summary>
    public ObservableCollection<ProviderToggleViewModel> Providers { get; } = [];

    public string CacheSize { get; private set; } = "0 MB";

    public string? Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    /// <summary>Windows 11 hides third-party verbs behind "Show more options".</summary>
    public string ContextMenuHint =>
        Environment.OSVersion.Version.Build >= 22000
            ? "On Windows 11 the entry appears under \"Show more options\" (Shift+F10)."
            : "The entry appears when you right-click a game executable or folder.";

    public ICommand CloseCommand { get; }
    public ICommand InstallContextMenuCommand { get; }
    public ICommand RemoveContextMenuCommand { get; }
    public ICommand BrowseSteamPathCommand { get; }
    public ICommand ClearCacheCommand { get; }
    public ICommand OpenSteamGridDbKeyPageCommand { get; }

    private void Save()
    {
        var settings = _services.Settings;

        settings.SteamGridDbApiKey = Blank(_steamGridDbApiKey);
        settings.IgdbClientId = Blank(_igdbClientId);
        settings.IgdbClientSecret = Blank(_igdbClientSecret);
        settings.SteamPathOverride = Blank(_steamPathOverride);

        _services.SaveSettings(settings);
        RefreshProviders();
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private void RefreshProviders()
    {
        Providers.Clear();

        foreach (var status in _services.Providers.Describe())
            Providers.Add(new ProviderToggleViewModel(status, enabled =>
            {
                var settings = _services.Settings;
                settings.SetProviderEnabled(status.Id, enabled);
                _services.SaveSettings(settings);
                RefreshProviders();
            }, _services.Settings.IsProviderEnabled(status.Id)));
    }

    private void RefreshCacheSize()
    {
        var bytes = _services.ImageCache.SizeOnDisk();
        CacheSize = bytes < 1024 * 1024
            ? $"{bytes / 1024.0:0} KB"
            : $"{bytes / (1024.0 * 1024.0):0.0} MB";

        OnPropertyChanged(nameof(CacheSize));
    }

    private void InstallContextMenu()
    {
        try
        {
            ContextMenuInstaller.Install();
            ContextMenuInstalled = true;
            Status = "Added to the Explorer right-click menu.";
        }
        catch (InvalidOperationException ex)
        {
            Status = ex.Message;
        }
    }

    private void RemoveContextMenu()
    {
        ContextMenuInstaller.Uninstall();
        ContextMenuInstalled = false;
        Status = "Removed from the Explorer right-click menu.";
    }

    private void BrowseSteamPath()
    {
        var dialog = new OpenFolderDialog { Title = "Select your Steam folder" };

        if (dialog.ShowDialog() == true)
            SteamPathOverride = dialog.FolderName;
    }

    private void ClearCache()
    {
        _services.SearchCache.Clear();
        _services.ImageCache.Clear();
        RefreshCacheSize();
        Status = "Cached searches and images cleared.";
    }

    private void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Status = "Could not open your browser.";
        }
    }
}

/// <summary>A provider row in settings, with its enable toggle and requirement note.</summary>
public sealed class ProviderToggleViewModel : ObservableObject
{
    private readonly Action<bool> _onChanged;
    private bool _isEnabled;

    public ProviderToggleViewModel(ProviderStatus status, Action<bool> onChanged, bool isEnabled)
    {
        _onChanged = onChanged;
        _isEnabled = isEnabled;

        DisplayName = status.DisplayName;
        Requirement = status.Requirement;
        IsActive = status.IsEnabled;
    }

    public string DisplayName { get; }

    /// <summary>Why the provider cannot run, e.g. a missing API key.</summary>
    public string? Requirement { get; }

    /// <summary>True when the provider will actually be queried right now.</summary>
    public bool IsActive { get; }

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (SetProperty(ref _isEnabled, value)) _onChanged(value);
        }
    }
}
