using System.IO;
using System.Windows.Input;
using Microsoft.Win32;
using Nama.App.Infrastructure;

namespace Nama.App.ViewModels;

/// <summary>
/// The landing step: drop a game in, or browse for one. Kept deliberately bare —
/// this screen is skipped entirely when Nama is launched from the context menu.
/// </summary>
public sealed class SelectViewModel(ShellViewModel shell) : ObservableObject
{
    private bool _isDragOver;

    /// <summary>True while a file is hovering over the drop zone.</summary>
    public bool IsDragOver
    {
        get => _isDragOver;
        set => SetProperty(ref _isDragOver, value);
    }

    public ICommand BrowseExecutableCommand => RelayCommand.Create(BrowseExecutable);
    public ICommand BrowseFolderCommand => RelayCommand.Create(BrowseFolder);

    /// <summary>True when Explorer integration is not set up, so the hint can offer it.</summary>
    public bool ShowContextMenuHint => !WindowsIntegration.ContextMenuInstaller.IsInstalled();

    public ICommand OpenSettingsCommand => shell.OpenSettingsCommand;

    private void BrowseExecutable()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select a game executable",
            Filter = "Games (*.exe;*.lnk;*.bat;*.cmd)|*.exe;*.lnk;*.bat;*.cmd|All files (*.*)|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() == true)
            shell.ShowIdentify(dialog.FileName);
    }

    private void BrowseFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select a game folder",
        };

        if (dialog.ShowDialog() == true)
            shell.ShowIdentify(dialog.FolderName);
    }

    /// <summary>
    /// Accepts a dropped path. Anything that is not an existing file or folder is
    /// rejected with a message rather than silently ignored.
    /// </summary>
    public void HandleDrop(string[]? paths)
    {
        IsDragOver = false;

        var path = paths?.FirstOrDefault();

        if (string.IsNullOrWhiteSpace(path))
        {
            shell.ShowError("That drop did not contain a file or folder.");
            return;
        }

        if (!File.Exists(path) && !Directory.Exists(path))
        {
            shell.ShowError($"\"{Path.GetFileName(path)}\" could not be found.");
            return;
        }

        shell.ClearBanner();
        shell.ShowIdentify(path);
    }
}
