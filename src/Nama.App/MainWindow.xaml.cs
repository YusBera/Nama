using System.Windows;
using System.Windows.Input;
using Nama.App.ViewModels;

namespace Nama.App;

/// <summary>
/// The application shell. Owns the custom title bar and forwards window-level drag and
/// drop to whichever step is currently showing.
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private ShellViewModel? Shell => DataContext as ShellViewModel;

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            return;
        }

        // DragMove throws if the button was released between the event and this call,
        // which happens with fast clicks.
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximize(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Dropping is accepted anywhere in the window, but only acted on while the
    /// selection step is showing — dropping a file mid-flow would be ambiguous.
    /// </summary>
    private void OnWindowDrop(object sender, DragEventArgs e)
    {
        if (Shell?.CurrentPage is not SelectViewModel select) return;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;

        select.HandleDrop(e.Data.GetData(DataFormats.FileDrop) as string[]);
        e.Handled = true;
    }

    private void OnWindowDragOver(object sender, DragEventArgs e)
    {
        var isSelectStep = Shell?.CurrentPage is SelectViewModel;
        var hasFiles = e.Data.GetDataPresent(DataFormats.FileDrop);

        e.Effects = isSelectStep && hasFiles ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;

        if (Shell?.CurrentPage is SelectViewModel select) select.IsDragOver = hasFiles;
    }

    private void OnWindowDragLeave(object sender, DragEventArgs e)
    {
        if (Shell?.CurrentPage is SelectViewModel select) select.IsDragOver = false;
    }
}
