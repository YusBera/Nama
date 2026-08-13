using System.Windows;
using System.Windows.Threading;
using Nama.App.Services;
using Nama.App.ViewModels;

namespace Nama.App;

/// <summary>
/// Application entry point. Accepts an optional path argument so the Explorer
/// context-menu verb can jump straight into identification.
/// </summary>
public partial class App : Application
{
    private AppServices? _services;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // A crash dialog is friendlier than a silent disappearance, and Nama is a
        // utility the user launched for one specific task.
        DispatcherUnhandledException += OnUnhandledException;

        _services = new AppServices();

        var shell = new ShellViewModel(_services);
        var window = new MainWindow { DataContext = shell };

        MainWindow = window;
        window.Show();

        shell.Start(ExtractPath(e.Args));
    }

    /// <summary>
    /// Reads the target path from the command line. Explorer passes it as the first
    /// argument; quoting and trailing separators vary, so it is normalized here.
    /// </summary>
    private static string? ExtractPath(string[] args)
    {
        var candidate = args.FirstOrDefault(a => !a.StartsWith('-') && !a.StartsWith('/'));
        if (string.IsNullOrWhiteSpace(candidate)) return null;

        var path = candidate.Trim().Trim('"');

        // Directory\Background passes the folder with a trailing separator.
        if (path.Length > 3 && path.EndsWith('\\')) path = path.TrimEnd('\\');

        return path;
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            $"Nama hit an unexpected error and needs to close.\n\n{e.Exception.Message}",
            "Nama",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        e.Handled = true;
        Shutdown(1);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.Dispose();
        base.OnExit(e);
    }
}
