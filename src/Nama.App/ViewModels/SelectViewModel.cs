using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace Nama.App.ViewModels;

/// <summary>
/// Step 1: choose a game. Usually skipped entirely, because the intended way in is a
/// right-click on the executable.
/// </summary>
public sealed partial class SelectViewModel(ShellViewModel shell) : ObservableObject
{
    [ObservableProperty]
    private bool isDragOver;

    [RelayCommand]
    private async Task BrowseFileAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select a game executable",
            Filter = "Programs (*.exe;*.lnk)|*.exe;*.lnk|All files (*.*)|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() == true) await shell.StartWithPathAsync(dialog.FileName).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task BrowseFolderAsync()
    {
        var dialog = new OpenFolderDialog { Title = "Select a game folder" };

        if (dialog.ShowDialog() == true) await shell.StartWithPathAsync(dialog.FolderName).ConfigureAwait(true);
    }

    /// <summary>Handles a file or folder dropped onto the window.</summary>
    public async Task DropAsync(string path)
    {
        IsDragOver = false;
        await shell.StartWithPathAsync(path).ConfigureAwait(true);
    }
}
