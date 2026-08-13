using System.IO;
using Microsoft.Win32;

namespace Nama.App.WindowsIntegration;

/// <summary>
/// Registers Nama's Explorer context-menu entries. Everything is written under
/// <c>HKEY_CURRENT_USER</c>, so installing needs no elevation and cannot affect other
/// accounts on the machine.
/// </summary>
public static class ContextMenuInstaller
{
    private const string EntryKeyName = "Nama";
    private const string MenuText = "Add to Steam with Nama";

    /// <summary>The file and folder classes Nama attaches to.</summary>
    private static readonly (string ClassPath, string Argument)[] Targets =
    [
        // Executables: the primary entry point from the spec.
        (@"Software\Classes\exefile\shell", "\"%1\""),

        // A folder, right-clicked directly.
        (@"Software\Classes\Directory\shell", "\"%1\""),

        // The empty space inside an open folder. %V is the folder being viewed.
        (@"Software\Classes\Directory\Background\shell", "\"%V\""),
    ];

    /// <summary>True when every entry is present and points at the current executable.</summary>
    public static bool IsInstalled()
    {
        try
        {
            var expected = ExecutablePath();

            foreach (var (classPath, _) in Targets)
            {
                using var key = Registry.CurrentUser.OpenSubKey($@"{classPath}\{EntryKeyName}\command");
                if (key?.GetValue(null) is not string command) return false;
                if (!command.Contains(expected, StringComparison.OrdinalIgnoreCase)) return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Creates or refreshes the context-menu entries.
    /// </summary>
    /// <exception cref="InvalidOperationException">The registry could not be written.</exception>
    public static void Install()
    {
        var executable = ExecutablePath();

        if (!File.Exists(executable))
            throw new InvalidOperationException(
                "Nama could not locate its own executable, so the context menu was not installed.");

        try
        {
            foreach (var (classPath, argument) in Targets)
            {
                using var shell = Registry.CurrentUser.CreateSubKey(classPath, writable: true)
                    ?? throw new InvalidOperationException($"Could not open {classPath}.");

                using var entry = shell.CreateSubKey(EntryKeyName, writable: true);
                entry.SetValue(null, MenuText, RegistryValueKind.String);
                entry.SetValue("Icon", $"\"{executable}\",0", RegistryValueKind.String);

                using var command = entry.CreateSubKey("command", writable: true);
                command.SetValue(null, $"\"{executable}\" {argument}", RegistryValueKind.String);
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            throw new InvalidOperationException(
                "Windows would not let Nama write its context-menu entries.", ex);
        }
    }

    /// <summary>Removes the entries. Missing keys are not an error.</summary>
    public static void Uninstall()
    {
        foreach (var (classPath, _) in Targets)
        {
            try
            {
                using var shell = Registry.CurrentUser.OpenSubKey(classPath, writable: true);
                shell?.DeleteSubKeyTree(EntryKeyName, throwOnMissingSubKey: false);
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                // Leaving a stale entry behind is preferable to failing the settings screen.
            }
        }
    }

    /// <summary>
    /// Path to the running executable. Under <c>dotnet run</c> the process is the host,
    /// so the app's own path is preferred when it resolves to a real .exe.
    /// </summary>
    private static string ExecutablePath()
    {
        var path = Environment.ProcessPath;

        if (!string.IsNullOrWhiteSpace(path) &&
            path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
            !Path.GetFileName(path).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
            return path;

        var assembly = System.Reflection.Assembly.GetEntryAssembly()?.Location;
        if (!string.IsNullOrWhiteSpace(assembly))
        {
            var candidate = Path.ChangeExtension(assembly, ".exe");
            if (File.Exists(candidate)) return candidate;
        }

        return path ?? string.Empty;
    }
}
