using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nama.Steam.Models;
using Nama.Steam.Writing;

namespace Nama.App.ViewModels;

/// <summary>Step 4: confirmation of what was actually written.</summary>
public sealed partial class SuccessViewModel(ShellViewModel shell) : ObservableObject
{
    private SteamInstallation? _installation;

    public ObservableCollection<string> AppliedArtwork { get; } = [];

    public ObservableCollection<string> FailedArtwork { get; } = [];

    [ObservableProperty]
    private string gameName = string.Empty;

    [ObservableProperty]
    private string headline = "Added to Steam";

    [ObservableProperty]
    private string? note;

    public bool HasFailures => FailedArtwork.Count > 0;

    public void Show(string name, WriteResult result, SteamInstallation installation)
    {
        _installation = installation;

        GameName = name;
        Headline = result.WasUpdate ? "Updated in Steam" : "Added to Steam";

        AppliedArtwork.Clear();
        FailedArtwork.Clear();

        if (result.Artwork is not null)
        {
            foreach (var (type, _) in result.Artwork.Applied)
            {
                AppliedArtwork.Add(type == Core.Models.ArtworkType.Grid ? "Banner" : type.ToString());
            }

            foreach (var (type, reason) in result.Artwork.Failed) FailedArtwork.Add($"{type}: {reason}");
        }

        // Steam only reads shortcuts.vdf at startup, so say so rather than leaving the
        // user wondering why the library looks unchanged.
        Note = "Start Steam to see it in your library.";

        OnPropertyChanged(nameof(HasFailures));
    }

    [RelayCommand]
    private void Done() => System.Windows.Application.Current.Shutdown();

    [RelayCommand]
    private void AddAnother() => shell.Reset();

    [RelayCommand]
    private void OpenSteam()
    {
        if (_installation is not null) shell.Services.Steam.StartSteam(_installation);
    }
}
