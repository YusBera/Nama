using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nama.App.Services;
using Nama.App.WindowsIntegration;
using Nama.Steam.Models;

namespace Nama.App.ViewModels;

/// <summary>
/// Settings, kept deliberately short. Nama is meant to be opinionated, so this covers only
/// the things it genuinely cannot decide for the user: the API key it is not allowed to
/// ship, which Steam account to use when there is more than one, and whether the Explorer
/// entry is installed.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppServices _services;

    public SettingsViewModel(AppServices services)
    {
        _services = services;

        ApiKey = services.Settings.SteamGridDbApiKey ?? string.Empty;
        ExperimentalDlsiteEnabled = services.Settings.ExperimentalDlsiteEnabled;
        ExperimentalVndbEnabled = services.Settings.ExperimentalVndbEnabled;
        ContextMenuInstalled = ContextMenuInstaller.IsInstalled();

        LoadAccounts();
        UpdateCacheCount();
    }

    public ObservableCollection<SteamAccount> Accounts { get; } = [];

    [ObservableProperty]
    private string apiKey = string.Empty;

    [ObservableProperty]
    private bool experimentalDlsiteEnabled;

    [ObservableProperty]
    private bool experimentalVndbEnabled;

    [ObservableProperty]
    private SteamAccount? selectedAccount;

    [ObservableProperty]
    private bool contextMenuInstalled;

    [ObservableProperty]
    private string? statusMessage;

    [ObservableProperty]
    private string? errorMessage;

    [ObservableProperty]
    private int cacheCount;

    public bool HasMultipleAccounts => Accounts.Count > 1;

    public string ContextMenuNote =>
        "On Windows 11 this appears under \"Show more options\" in the right-click menu.";

    private void LoadAccounts()
    {
        var installation = _services.Steam.FindSteamInstallation();
        if (installation is null)
        {
            ErrorMessage = "Steam installation not found.";
            return;
        }

        foreach (var account in _services.Steam.FindLibraryData(installation)) Accounts.Add(account);

        var preferred = _services.Settings.PreferredSteamAccountId;
        SelectedAccount = Accounts.FirstOrDefault(a => a.AccountId == preferred) ?? Accounts.FirstOrDefault();

        OnPropertyChanged(nameof(HasMultipleAccounts));
    }

    private void UpdateCacheCount() => CacheCount = _services.Cache.Count();

    [RelayCommand]
    private void Save()
    {
        ErrorMessage = null;

        try
        {
            var settings = _services.Settings;

            // Assigning through the property encrypts it; only ciphertext reaches disk.
            settings.SteamGridDbApiKey = string.IsNullOrWhiteSpace(ApiKey) ? null : ApiKey.Trim();
            settings.PreferredSteamAccountId = SelectedAccount?.AccountId;
            settings.ContextMenuInstalled = ContextMenuInstalled;
            settings.ExperimentalDlsiteEnabled = ExperimentalDlsiteEnabled;
            settings.ExperimentalVndbEnabled = ExperimentalVndbEnabled;
            settings.Save();

            // Rebuild providers so a newly entered key takes effect without a restart.
            _services.ReloadProviders();

            StatusMessage = "Saved.";
        }
        catch (Exception e)
        {
            ErrorMessage = e.Message;
        }
    }

    [RelayCommand]
    private void ToggleContextMenu()
    {
        ErrorMessage = null;

        var error = ContextMenuInstalled ? ContextMenuInstaller.Uninstall() : ContextMenuInstaller.Install();

        if (error is not null)
        {
            ErrorMessage = error;
            return;
        }

        ContextMenuInstalled = !ContextMenuInstalled;
        StatusMessage = ContextMenuInstalled
            ? "Added to the Explorer right-click menu."
            : "Removed from the Explorer right-click menu.";

        _services.Settings.ContextMenuInstalled = ContextMenuInstalled;
        _services.Settings.Save();
    }

    [RelayCommand]
    private async Task ClearCacheAsync()
    {
        await _services.Cache.ClearAsync().ConfigureAwait(true);

        UpdateCacheCount();
        StatusMessage = "Cache cleared.";
    }

    [RelayCommand]
    private static void OpenKeyPage()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "https://www.steamgriddb.com/profile/preferences/api",
                UseShellExecute = true,
            });
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // No default browser; the URL is shown in the dialog anyway.
        }
    }
}
