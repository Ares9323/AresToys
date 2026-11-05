using System.Runtime.CompilerServices;
using System.Text;
using AresToys.Storage.Database;
using AresToys.Storage.Protection;

namespace AresToys.Storage.Settings;

public sealed class SqliteSettingsStore : ISettingsStore
{
    private readonly IAresToysDatabase _database;
    private readonly IPayloadProtector _protector;

    public SqliteSettingsStore(IAresToysDatabase database, IPayloadProtector protector)
    {
        _database = database;
        _protector = protector;
    }

    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        string stored;
        bool sensitive;
        await using (var lease = await _database.AcquireAsync(cancellationToken).ConfigureAwait(false))
        {
            await using var cmd = lease.Connection.CreateCommand();
            cmd.CommandText = "SELECT value, is_sensitive FROM settings WHERE key = $key LIMIT 1;";
            cmd.Parameters.AddWithValue("$key", key);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;

            stored = reader.GetString(0);
            sensitive = reader.GetInt32(1) == 1;
        }
        if (!sensitive) return stored;

        // DPAPI runs after the lease is gone: it does not need the connection.
        var ciphertext = Convert.FromBase64String(stored);
        var plaintext = _protector.Unprotect(ciphertext);
        return Encoding.UTF8.GetString(plaintext);
    }

    public async Task SetAsync(string key, string value, bool sensitive, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(value);

        var stored = sensitive
            ? Convert.ToBase64String(_protector.Protect(Encoding.UTF8.GetBytes(value)))
            : value;

        await using var lease = await _database.AcquireAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = lease.Connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO settings (key, value, is_sensitive) VALUES ($key, $value, $sensitive)
            ON CONFLICT(key) DO UPDATE SET value = $value, is_sensitive = $sensitive;
            """;
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$value", stored);
        cmd.Parameters.AddWithValue("$sensitive", sensitive ? 1 : 0);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> RemoveAsync(string key, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        await using var lease = await _database.AcquireAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = lease.Connection.CreateCommand();
        cmd.CommandText = "DELETE FROM settings WHERE key = $key;";
        cmd.Parameters.AddWithValue("$key", key);
        var rows = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return rows == 1;
    }

    public async IAsyncEnumerable<SettingEntry> EnumerateAsync(
        bool includeSensitive = false,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // The rows are read in one go under the lease and handed out after it is released: a
        // reader kept open across the yields would hold the connection for as long as the
        // caller takes to consume each entry (and deadlock a caller that uses the store inside
        // its loop).
        var rows = new List<(string Key, string Stored, bool Sensitive)>();
        await using (var lease = await _database.AcquireAsync(cancellationToken).ConfigureAwait(false))
        {
            await using var cmd = lease.Connection.CreateCommand();
            // Filter at SQL level when sensitive entries should be skipped: saves us from
            // unprotecting payloads we'll discard anyway.
            cmd.CommandText = includeSensitive
                ? "SELECT key, value, is_sensitive FROM settings ORDER BY key;"
                : "SELECT key, value, is_sensitive FROM settings WHERE is_sensitive = 0 ORDER BY key;";
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add((reader.GetString(0), reader.GetString(1), reader.GetInt32(2) == 1));
            }
        }

        foreach (var (key, stored, sensitive) in rows)
        {
            string value;
            if (sensitive)
            {
                var ciphertext = Convert.FromBase64String(stored);
                var plaintext = _protector.Unprotect(ciphertext);
                value = Encoding.UTF8.GetString(plaintext);
            }
            else
            {
                value = stored;
            }
            yield return new SettingEntry(key, value, sensitive);
        }
    }
}
