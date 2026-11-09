using Microsoft.Data.Sqlite;

namespace AresToys.Storage.Database.Migrations;

public interface IMigration
{
    /// <summary>Schema version produced after applying this migration.</summary>
    int TargetVersion { get; }

    /// <summary>Applies the migration on an open connection.</summary>
    Task ApplyAsync(SqliteConnection connection, CancellationToken cancellationToken);
}
