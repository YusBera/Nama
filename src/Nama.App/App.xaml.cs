using System.Windows;
using System.Windows.Threading;
using Nama.App.Services;
using Nama.App.ViewModels;
using Nama.App.WindowsIntegration;

namespace Nama.App;

public partial class App : Application
{
    private AppServices? _services;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // A crash in a utility that writes to the Steam library must not be silent.
        DispatcherUnhandledException += OnUnhandledException;

        // Switches that do one thing and exit, so the context menu can be set up from a
        // shortcut or a script without opening the app.
        if (e.Args.Length > 0)
        {
            switch (e.Args[0].ToLowerInvariant())
            {
                case "--install-context-menu":
                    ReportAndExit(ContextMenuInstaller.Install(), "Added Nama to the Explorer right-click menu.");
                    return;

                case "--uninstall-context-menu":
                    ReportAndExit(ContextMenuInstaller.Uninstall(), "Removed Nama from the Explorer right-click menu.");
                    return;
            }
        }

        _services = AppServices.Create();

        var shell = new ShellViewModel(_services);
        var window = new MainWindow(shell);

        MainWindow = window;
        window.Show();

        if (e.Args.Length == 0) return;

        if (e.Args[0].Equals("--settings", StringComparison.OrdinalIgnoreCase))
        {
            window.OpenSettings();
        }
        else if (!e.Args[0].StartsWith('-'))
        {
            // Launched from the context menu or with a path: skip step 1.
            var path = e.Args[0];
            window.Dispatcher.InvokeAsync(async () => await shell.StartWithPathAsync(path));
        }
    }

    private void ReportAndExit(string? error, string successMessage)
    {
        MessageBox.Show(
            error ?? successMessage,
            "Nama",
            MessageBoxButton.OK,
            error is null ? MessageBoxImage.Information : MessageBoxImage.Warning);

        Shutdown(error is null ? 0 : 1);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            $"Nama hit an unexpected error and stopped.\n\n{e.Exception.Message}\n\n" +
            "Your Steam library has not been modified unless the success screen said so.",
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
