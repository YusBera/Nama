using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using Nama.App.Infrastructure;
using Nama.App.Services;
using Nama.Core.Identification;
using Nama.Core.Models;
using Nama.SteamIntegration;

namespace Nama.App.ViewModels;

/// <summary>The final confirmation screen.</summary>
public sealed class SuccessViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly ShellViewModel _shell;
    private readonly AddGameResult _result;

    public SuccessViewModel(
        AppServices services,
        ShellViewModel shell,
        AddGameResult result,
        LocalMetadata local,
        Game game)
    {
        _services = services;
        _shell = shell;
        _result = result;

        GameName = result.Shortcut.AppName;

        foreach (var type in ArtworkTypeInfo.SteamApplicable)
            if (result.AppliedArtwork.Contains(type))
                AppliedArtwork.Add(ArtworkTypeInfo.Label(type).ToLowerInvariant() switch
                {
                    var label => char.ToUpper(label[0]) + label[1..],
                });

        foreach (var (type, reason) in result.FailedArtwork)
            FailedArtwork.Add($"{ArtworkTypeInfo.Label(type)} — {reason}");

        DoneCommand = RelayCommand.Create(() => shell.ShowSelect());
        AddAnotherCommand = RelayCommand.Create(() => shell.ShowSelect());
        OpenSteamCommand = RelayCommand.Create(OpenSteam);
    }

    public string GameName { get; }

    public string Headline => _result.Action == ShortcutAction.Created
        ? "Added to Steam"
        : "Updated in Steam";

    public ObservableCollection<string> AppliedArtwork { get; } = [];

    public ObservableCollection<string> FailedArtwork { get; } = [];

    public bool HasFailures => FailedArtwork.Count > 0;

    public bool HasAppliedArtwork => AppliedArtwork.Count > 0;

    public ICommand DoneCommand { get; }
    public ICommand AddAnotherCommand { get; }
    public ICommand OpenSteamCommand { get; }

    private void OpenSteam()
    {
        try
        {
            var installation = _services.SteamManager.FindSteamInstallation(_services.Settings.SteamPathOverride);

            // The library URL only works against a running client, so launch the
            // executable when Steam is closed.
            if (installation is not null && SteamInstallation.IsSteamRunning())
            {
                Process.Start(new ProcessStartInfo("steam://open/games") { UseShellExecute = true });
                return;
            }

            if (installation is not null && File.Exists(installation.SteamExecutable))
            {
                Process.Start(new ProcessStartInfo(installation.SteamExecutable) { UseShellExecute = true });
                return;
            }

            _shell.ShowError("Nama could not start Steam.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            _shell.ShowError($"Nama could not start Steam: {ex.Message}");
        }
    }
}
