using Microsoft.Data.Sqlite;
using Nama.Core.Abstractions;

namespace Nama.Storage;

/// <summary>
/// SQLite-backed provider response cache under <c>%LOCALAPPDATA%\Nama\cache</c>.
/// <para>
/// Every operation swallows its own failures, per <see cref="ISearchCache"/>. A locked,
/// corrupt or unwritable cache degrades to live requests — the app stays fully functional
/// and the user never sees an error about it.
/// </para>
/// </summary>
public sealed class SqliteSearchCache : ISearchCache, IDisposable
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _usable = true;

    public SqliteSearchCache(string? databasePath = null)
    {
        DatabasePath = databasePath ?? DefaultPath;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString();

        Initialize();
    }

    public string DatabasePath { get; }

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Nama", "cache", "responses.db");

    /// <summary>False once the cache has failed to initialise; every call then no-ops.</summary>
    public bool IsUsable => _usable;

    private void Initialize()
    {
        try
        {
            var directory = Path.GetDirectoryName(DatabasePath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS responses (
                    key        TEXT PRIMARY KEY,
                    payload    TEXT NOT NULL,
                    expires_at INTEGER NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_responses_expiry ON responses (expires_at);
                """;
            command.ExecuteNonQuery();

            PurgeExpired(connection);
        }
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException)
        {
            _usable = false;
        }
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        if (!_usable) return null;

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT payload FROM responses WHERE key = $key AND expires_at > $now;";
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());

            return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SetAsync(string key, string payload, TimeSpan ttl, CancellationToken ct = default)
    {
        if (!_usable) return;

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO responses (key, payload, expires_at) VALUES ($key, $payload, $expires)
                ON CONFLICT(key) DO UPDATE SET payload = excluded.payload, expires_at = excluded.expires_at;
                """;
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$payload", payload);
            command.Parameters.AddWithValue("$expires", DateTimeOffset.UtcNow.Add(ttl).ToUnixTimeSeconds());

            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Losing a cache write costs one repeated request.
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task ClearAsync(CancellationToken ct = default)
    {
        if (!_usable) return;

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM responses;";
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Number of live (unexpired) entries. For the settings screen.</summary>
    public int Count()
    {
        if (!_usable) return 0;

        try
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM responses WHERE expires_at > $now;";
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());

            return Convert.ToInt32(command.ExecuteScalar());
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static void PurgeExpired(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM responses WHERE expires_at <= $now;";
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        _lock.Dispose();
        SqliteConnection.ClearAllPools();
    }
}
