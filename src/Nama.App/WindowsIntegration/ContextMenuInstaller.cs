using System.IO;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Nama.App.WindowsIntegration;

/// <summary>
/// Installs the "Add to Steam with Nama" entry into Explorer's right-click menu.
/// <para>
/// Everything is written under <c>HKEY_CURRENT_USER</c>, so no elevation is needed and
/// uninstalling is a clean delete of two keys. The cost of that choice is placement: on
/// Windows 11 a per-user verb appears under <b>Show more options</b> rather than the top
/// level. Getting it into the primary menu requires shipping a sparse MSIX package with an
/// <c>IExplorerCommand</c> handler, which is a much larger change and is deferred.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class ContextMenuInstaller
{
    private const string KeyName = "Nama";

    private const string VerbLabel = "Add to Steam with Nama";

    /// <summary>Executable files, and folders (so a game directory can be added directly).</summary>
    private static readonly string[] TargetClasses = ["exefile", "Directory"];

    /// <summary>Path to the running Nama executable.</summary>
    public static string? ExecutablePath
    {
        get
        {
            var path = Environment.ProcessPath;

            // Under `dotnet run` the host is dotnet.exe, which is not what the menu should
            // launch. Fall back to the assembly next to it.
            if (path is not null && !Path.GetFileName(path).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
            {
                return path;
            }

            var assembly = System.Reflection.Assembly.GetEntryAssembly()?.Location;
            if (string.IsNullOrEmpty(assembly)) return path;

            var candidate = Path.ChangeExtension(assembly, ".exe");
            return File.Exists(candidate) ? candidate : path;
        }
    }

    public static bool IsInstalled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath(TargetClasses[0]));
            return key is not null;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>
    /// Registers the verb. Returns an error message, or null on success.
    /// </summary>
    public static string? Install()
    {
        var executable = ExecutablePath;

        if (string.IsNullOrEmpty(executable) || !File.Exists(executable))
        {
            return "Could not determine where Nama is installed. Build a published copy and run that.";
        }

        try
        {
            foreach (var className in TargetClasses)
            {
                using var verb = Registry.CurrentUser.CreateSubKey(KeyPath(className));
                verb.SetValue("MUIVerb", VerbLabel);
                verb.SetValue("Icon", $"\"{executable}\",0");

                using var command = verb.CreateSubKey("command");
                // %1 is the file or folder the user right-clicked.
                command.SetValue(string.Empty, $"\"{executable}\" \"%1\"");
            }

            return null;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return $"Could not write to the registry: {e.Message}";
        }
    }

    /// <summary>Removes the verb. Returns an error message, or null on success.</summary>
    public static string? Uninstall()
    {
        try
        {
            foreach (var className in TargetClasses)
            {
                Registry.CurrentUser.DeleteSubKeyTree(KeyPath(className), throwOnMissingSubKey: false);
            }

            return null;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return $"Could not update the registry: {e.Message}";
        }
    }

    private static string KeyPath(string className) => $@"Software\Classes\{className}\shell\{KeyName}";
}
