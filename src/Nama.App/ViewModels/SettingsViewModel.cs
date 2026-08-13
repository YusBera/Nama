using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net.Http;
using System.Windows.Input;
using Microsoft.Win32;
using System.Windows;
using Nama.App.Infrastructure;
using Nama.App.Services;
using Nama.App.WindowsIntegration;
using Nama.Providers;
using Nama.Providers.NamaDb;

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

    private CancellationTokenSource? _linkCts;
    private string? _linkUserCode;
    private string? _linkStatus;
    private bool _isLinking;

    public SettingsViewModel(AppServices services, Action close)
    {
        _services = services;
        _close = close;

        _services.Providers.NamaDbAuth.LinkChanged += (_, _) => OnPropertyChanged(nameof(IsNamaDbLinked));

        LinkNamaDbCommand = AsyncRelayCommand.Create(LinkNamaDbAsync, () => !IsLinking);
        CancelNamaDbLinkCommand = RelayCommand.Create(CancelNamaDbLink, () => IsLinking);
        UnlinkNamaDbCommand = AsyncRelayCommand.Create(UnlinkNamaDbAsync, () => IsNamaDbLinked);

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

    /// <summary>True once this installation holds a NamaDB identity it can renew.</summary>
    public bool IsNamaDbLinked => _services.Providers.NamaDbAuth.IsLinked;

    /// <summary>The short code the user checks against the page while a link is in flight.</summary>
    public string? LinkUserCode
    {
        get => _linkUserCode;
        private set => SetProperty(ref _linkUserCode, value);
    }

    public string? LinkStatus
    {
        get => _linkStatus;
        private set => SetProperty(ref _linkStatus, value);
    }

    /// <summary>True while Nama is waiting for the browser half of the handshake.</summary>
    public bool IsLinking
    {
        get => _isLinking;
        private set
        {
            if (!SetProperty(ref _isLinking, value)) return;
            (LinkNamaDbCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (CancelNamaDbLinkCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public ICommand LinkNamaDbCommand { get; }
    public ICommand CancelNamaDbLinkCommand { get; }
    public ICommand UnlinkNamaDbCommand { get; }

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
                if (status.Id == "namadb" && enabled && settings.NamaDbAdultAcceptedAt is null)
                {
                    var answer = MessageBox.Show(
                        "NamaDB is an unfiltered community artwork catalog intended only for adults. It may contain explicit material. Illegal content remains prohibited.\n\nConfirm that you are 18 or older and want to enable NamaDB.",
                        "Enable NamaDB (18+)", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    if (answer != MessageBoxResult.Yes)
                    {
                        settings.NamaDbEnabled = false;
                        settings.SetProviderEnabled(status.Id, false);
                        _services.SaveSettings(settings);
                        RefreshProviders();
                        return;
                    }
                    settings.NamaDbAdultAcceptedAt = DateTimeOffset.UtcNow;
                }
                if (status.Id == "namadb") settings.NamaDbEnabled = enabled;
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

    /// <summary>
    /// Runs the device-link handshake: ask NamaDB for a code pair, open the verification page,
    /// then poll until the user approves it. Nama never sees the user's Steam credentials.
    /// </summary>
    private async Task LinkNamaDbAsync()
    {
        var auth = _services.Providers.NamaDbAuth;

        _linkCts?.Dispose();
        _linkCts = new CancellationTokenSource();
        var ct = _linkCts.Token;

        IsLinking = true;
        LinkStatus = "Contacting NamaDB…";
        try
        {
            var link = await auth.StartAsync(ct).ConfigureAwait(true);
            LinkUserCode = link.UserCode;
            LinkStatus = "Approve the code in your browser. Nama is waiting…";
            OpenUrl(link.VerificationUri);

            var result = await auth.WaitForLinkAsync(link, ct).ConfigureAwait(true);
            LinkStatus = result == DeviceLinkStatus.Linked
                ? "Nama is linked to your NamaDB account."
                : "The code expired before it was approved. Try again.";
        }
        catch (OperationCanceledException)
        {
            LinkStatus = "Linking cancelled.";
        }
        catch (HttpRequestException)
        {
            LinkStatus = "Could not reach NamaDB.";
        }
        finally
        {
            LinkUserCode = null;
            IsLinking = false;
            OnPropertyChanged(nameof(IsNamaDbLinked));
            (UnlinkNamaDbCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    private void CancelNamaDbLink() => _linkCts?.Cancel();

    private async Task UnlinkNamaDbAsync()
    {
        await _services.Providers.NamaDbAuth.SignOutAsync().ConfigureAwait(true);
        LinkStatus = "Nama is no longer linked to NamaDB.";
        OnPropertyChanged(nameof(IsNamaDbLinked));
        (UnlinkNamaDbCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
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
