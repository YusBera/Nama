using System.Security.Cryptography;
using Nama.Steam.Vdf;

namespace Nama.Steam.Writing;

/// <summary>Thrown when a verified write could not be completed safely.</summary>
public sealed class ShortcutWriteException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Writes <c>shortcuts.vdf</c> without ever risking the entries already in it.
/// <para>
/// The sequence is: fingerprint the existing entries, back the file up, write a temporary
/// file in the same directory, atomically replace, then re-read and confirm every
/// fingerprint that should have survived did. If verification fails the backup is restored,
/// so a failed write leaves the file exactly as it was.
/// </para>
/// </summary>
internal static class ShortcutFileWriter
{
    private const string BackupPrefix = ".nama-bak-";

    private const int BackupsToKeep = 5;

    /// <summary>
    /// SHA-256 of each entry's serialized bytes, keyed by its index.
    /// <para>
    /// Comparing entries as bytes rather than field by field is deliberate: it covers keys
    /// Nama does not model, which is exactly the data most at risk of being silently lost.
    /// </para>
    /// </summary>
    public static Dictionary<string, string> Fingerprint(VdfMap container)
    {
        var fingerprints = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (key, map) in container.ChildMaps())
        {
            fingerprints[key] = HashEntry(map);
        }

        return fingerprints;
    }

    private static string HashEntry(VdfMap entry)
    {
        // Wrap the entry so it can be serialized on its own.
        var wrapper = new VdfMap();
        wrapper.Add("e", entry);

        return Convert.ToHexString(SHA256.HashData(BinaryVdf.Write(wrapper)));
    }

    /// <summary>
    /// Performs the whole guarded write. Returns the backup path, or null when the file did
    /// not previously exist.
    /// </summary>
    /// <param name="path">Target shortcuts.vdf.</param>
    /// <param name="payload">Serialized replacement contents.</param>
    /// <param name="expectedSurvivors">Entry fingerprints that must still be present afterwards.</param>
    public static string? WriteVerified(
        string path, byte[] payload, IReadOnlyDictionary<string, string> expectedSurvivors)
    {
        var directory = Path.GetDirectoryName(path)
                        ?? throw new ShortcutWriteException($"'{path}' has no containing directory.");

        Directory.CreateDirectory(directory);

        var backup = CreateBackup(path);

        try
        {
            WriteAtomic(path, payload);
            Verify(path, expectedSurvivors);
        }
        catch (Exception e)
        {
            RestoreBackup(backup, path);

            throw e as ShortcutWriteException
                  ?? new ShortcutWriteException($"Write failed and the original was restored: {e.Message}", e);
        }

        PruneBackups(path);
        return backup;
    }

    /// <summary>Copies the current file aside. Returns null when there is nothing to back up.</summary>
    public static string? CreateBackup(string path)
    {
        if (!File.Exists(path)) return null;

        var backup = $"{path}{BackupPrefix}{DateTime.Now:yyyyMMddHHmmss}";

        // Two writes in the same second must not collide.
        var attempt = 1;
        while (File.Exists(backup)) backup = $"{path}{BackupPrefix}{DateTime.Now:yyyyMMddHHmmss}-{attempt++}";

        File.Copy(path, backup);
        return backup;
    }

    /// <summary>
    /// Writes via a temporary file in the same directory then replaces, so the file is
    /// never observed half-written — including if the process dies mid-write.
    /// </summary>
    private static void WriteAtomic(string path, byte[] payload)
    {
        var temporary = path + ".nama-tmp";

        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(payload);
            stream.Flush(flushToDisk: true);
        }

        if (File.Exists(path)) File.Replace(temporary, path, destinationBackupFileName: null);
        else File.Move(temporary, path);
    }

    /// <summary>Re-reads the file and confirms nothing that should have survived is missing.</summary>
    private static void Verify(string path, IReadOnlyDictionary<string, string> expectedSurvivors)
    {
        byte[] written;
        try
        {
            written = File.ReadAllBytes(path);
        }
        catch (IOException e)
        {
            throw new ShortcutWriteException("Could not re-read the file to verify it.", e);
        }

        if (!BinaryVdf.RoundTrips(written, out var parsed) || parsed is null)
        {
            throw new ShortcutWriteException("The file Nama just wrote does not parse cleanly.");
        }

        if (!parsed.TryGetMap("shortcuts", out var container))
        {
            throw new ShortcutWriteException("The written file has no 'shortcuts' section.");
        }

        var actual = new HashSet<string>(Fingerprint(container).Values, StringComparer.Ordinal);

        foreach (var (key, fingerprint) in expectedSurvivors)
        {
            if (!actual.Contains(fingerprint))
            {
                throw new ShortcutWriteException(
                    $"Verification failed: existing shortcut '{key}' was altered or lost.");
            }
        }
    }

    private static void RestoreBackup(string? backup, string path)
    {
        try
        {
            if (backup is not null && File.Exists(backup)) File.Copy(backup, path, overwrite: true);
            else if (backup is null && File.Exists(path)) File.Delete(path); // we created it; remove it
        }
        catch (IOException)
        {
            // Nothing further can be done here; the backup file is still on disk and the
            // exception being thrown names it.
        }
    }

    /// <summary>Keeps only the most recent backups.</summary>
    private static void PruneBackups(string path, int keep = BackupsToKeep)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (directory is null) return;

            var stale = Directory
                .EnumerateFiles(directory, Path.GetFileName(path) + BackupPrefix + "*")
                .OrderByDescending(f => f)
                .Skip(keep)
                .ToList();

            foreach (var file in stale) File.Delete(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Leaving extra backups behind is harmless.
        }
    }

    /// <summary>Existing backups, newest first.</summary>
    public static IReadOnlyList<string> ListBackups(string path)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (directory is null || !Directory.Exists(directory)) return [];

            return Directory
                .EnumerateFiles(directory, Path.GetFileName(path) + BackupPrefix + "*")
                .OrderByDescending(f => f)
                .ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>True when the file (or its directory, if absent) can be written to.</summary>
    public static bool CanWrite(string path)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(directory)) return false;

            Directory.CreateDirectory(directory);

            var probe = Path.Combine(directory, $".nama-write-probe-{Guid.NewGuid():N}");
            using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.WriteByte(0);
            }

            File.Delete(probe);

            if (!File.Exists(path)) return true;

            using var existing = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
