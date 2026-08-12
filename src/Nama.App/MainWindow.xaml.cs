using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Nama.App.ViewModels;

namespace Nama.App;

public partial class MainWindow : Window
{
    private readonly ShellViewModel _shell;

    public MainWindow(ShellViewModel shell)
    {
        _shell = shell;
        DataContext = shell;

        InitializeComponent();

        // Drag and drop is handled here rather than in the view model: it is a
        // presentation concern, and the view model only ever sees a path.
        DragEnter += OnDragEnter;
        DragLeave += OnDragLeave;
        Drop += OnDrop;
        StateChanged += (_, _) => MaximizeButton.Content = WindowState == WindowState.Maximized ? "❐" : "□";
    }

    private void OnTitleBarMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Buttons live inside the title bar; they must remain clickable rather than starting a drag.
        if (e.OriginalSource is DependencyObject source && FindParent<ButtonBase>(source) is not null) return;

        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private static T? FindParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child is not null)
        {
            if (child is T match) return match;
            child = System.Windows.Media.VisualTreeHelper.GetParent(child);
        }

        return null;
    }

    private void OnDragEnter(object sender, DragEventArgs e)
    {
        var accepted = TryGetPath(e) is not null;

        e.Effects = accepted ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;

        if (accepted) _shell.SelectStep.IsDragOver = true;
    }

    private void OnDragLeave(object sender, DragEventArgs e) => _shell.SelectStep.IsDragOver = false;

    /// <summary>
    /// Opening a window is a view concern, so it lives here rather than in a view model.
    /// </summary>
    private void OnSettingsClick(object sender, RoutedEventArgs e) => OpenSettings();

    /// <summary>Also reachable directly via <c>Nama.exe --settings</c>.</summary>
    public void OpenSettings()
    {
        var settings = new SettingsWindow(new SettingsViewModel(_shell.Services)) { Owner = this };

        settings.ShowDialog();
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        _shell.SelectStep.IsDragOver = false;

        if (TryGetPath(e) is not { } path) return;

        e.Handled = true;
        await _shell.SelectStep.DropAsync(path);
    }

    /// <summary>Accepts a dropped file or folder, ignoring multi-selections beyond the first.</summary>
    private static string? TryGetPath(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return null;

        return e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths ? paths[0] : null;
    }
}
