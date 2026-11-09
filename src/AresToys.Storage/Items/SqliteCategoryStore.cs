using Microsoft.Data.Sqlite;
using AresToys.Storage.Database;

namespace AresToys.Storage.Items;

public sealed class SqliteCategoryStore : ICategoryStore
{
    private readonly IAresToysDatabase _database;

    public SqliteCategoryStore(IAresToysDatabase database) { _database = database; }

    public event EventHandler? Changed;

    public async Task<IReadOnlyList<Category>> ListAsync(CancellationToken cancellationToken)
    {
        await using var lease = await _database.AcquireAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = lease.Connection.CreateCommand();
        cmd.CommandText = "SELECT name, icon, sort_order, max_items, auto_cleanup_after FROM categories ORDER BY sort_order, name;";
        var results = new List<Category>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(MapCategory(reader));
        }
        return results;
    }

    public async Task<Category?> GetAsync(string name, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        await using var lease = await _database.AcquireAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = lease.Connection.CreateCommand();
        cmd.CommandText = "SELECT name, icon, sort_order, max_items, auto_cleanup_after FROM categories WHERE name = $name;";
        cmd.Parameters.AddWithValue("$name", name);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        return MapCategory(reader);
    }

    public async Task AddAsync(Category category, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(category);
        ArgumentException.ThrowIfNullOrWhiteSpace(category.Name);
        await using (var lease = await _database.AcquireAsync(cancellationToken).ConfigureAwait(false))
        {
            await using var cmd = lease.Connection.CreateCommand();
            cmd.CommandText = "INSERT INTO categories (name, icon, sort_order, max_items, auto_cleanup_after) VALUES ($name, $icon, $order, $max, $cleanup);";
            cmd.Parameters.AddWithValue("$name", category.Name.Trim());
            cmd.Parameters.AddWithValue("$icon", (object?)category.Icon ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$order", category.SortOrder);
            cmd.Parameters.AddWithValue("$max", category.MaxItems);
            cmd.Parameters.AddWithValue("$cleanup", category.AutoCleanupAfter);
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task UpdateAsync(Category category, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(category);
        await using (var lease = await _database.AcquireAsync(cancellationToken).ConfigureAwait(false))
        {
            await using var cmd = lease.Connection.CreateCommand();
            cmd.CommandText = "UPDATE categories SET icon = $icon, sort_order = $order, max_items = $max, auto_cleanup_after = $cleanup WHERE name = $name;";
            cmd.Parameters.AddWithValue("$name", category.Name);
            cmd.Parameters.AddWithValue("$icon", (object?)category.Icon ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$order", category.SortOrder);
            cmd.Parameters.AddWithValue("$max", category.MaxItems);
            cmd.Parameters.AddWithValue("$cleanup", category.AutoCleanupAfter);
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task RenameAsync(string oldName, string newName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(oldName);
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);
        if (string.Equals(oldName, Category.Default, StringComparison.Ordinal))
            throw new InvalidOperationException($"The '{Category.Default}' category cannot be renamed.");
        var trimmed = newName.Trim();

        await using (var lease = await _database.AcquireAsync(cancellationToken).ConfigureAwait(false))
        {
            var conn = lease.Connection;
            await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            // Two-step: insert the new name (so the FK on items isn't violated mid-flight even
            // though we don't have one declared, and so a unique-name constraint races safely)
            // then re-point items, then drop the old row. Single TX so partial failure rolls back.
            await using (var renameCat = conn.CreateCommand())
            {
                renameCat.Transaction = tx;
                renameCat.CommandText = "UPDATE categories SET name = $new WHERE name = $old;";
                renameCat.Parameters.AddWithValue("$new", trimmed);
                renameCat.Parameters.AddWithValue("$old", oldName);
                await renameCat.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await using (var migrate = conn.CreateCommand())
            {
                migrate.Transaction = tx;
                migrate.CommandText = "UPDATE items SET category = $new WHERE category = $old;";
                migrate.Parameters.AddWithValue("$new", trimmed);
                migrate.Parameters.AddWithValue("$old", oldName);
                await migrate.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task DeleteAsync(string name, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (string.Equals(name, Category.Default, StringComparison.Ordinal))
            throw new InvalidOperationException($"The '{Category.Default}' category cannot be deleted.");

        await using (var lease = await _database.AcquireAsync(cancellationToken).ConfigureAwait(false))
        {
            var conn = lease.Connection;
            await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            // Re-route every item back to the default bucket so deleting a category never loses
            // user data. Same transaction so the delete and re-route succeed or fail together.
            await using (var migrate = conn.CreateCommand())
            {
                migrate.Transaction = tx;
                migrate.CommandText = "UPDATE items SET category = $def WHERE category = $name;";
                migrate.Parameters.AddWithValue("$def", Category.Default);
                migrate.Parameters.AddWithValue("$name", name);
                await migrate.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await using (var del = conn.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = "DELETE FROM categories WHERE name = $name;";
                del.Parameters.AddWithValue("$name", name);
                await del.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task ReorderAsync(IReadOnlyList<string> orderedNames, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(orderedNames);
        await using (var lease = await _database.AcquireAsync(cancellationToken).ConfigureAwait(false))
        {
            var conn = lease.Connection;
            await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            for (var i = 0; i < orderedNames.Count; i++)
            {
                await using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE categories SET sort_order = $order WHERE name = $name;";
                cmd.Parameters.AddWithValue("$order", i);
                cmd.Parameters.AddWithValue("$name", orderedNames[i]);
                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static Category MapCategory(SqliteDataReader reader) => new(
        Name: reader.GetString(0),
        Icon: reader.IsDBNull(1) ? null : reader.GetString(1),
        SortOrder: reader.GetInt32(2),
        MaxItems: reader.GetInt32(3),
        AutoCleanupAfter: reader.GetInt32(4));
}
