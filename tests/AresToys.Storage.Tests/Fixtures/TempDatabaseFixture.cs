using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using AresToys.Storage.Database;
using AresToys.Storage.Database.Migrations;
using AresToys.Storage.Options;
using AresToys.Storage.Paths;

namespace AresToys.Storage.Tests.Fixtures;

/// <summary>
/// Per-test fixture that creates a unique temp directory + database and disposes both at end-of-life.
/// Each fixture instance is independent → tests using it are parallel-safe by default.
/// </summary>
public sealed class TempDatabaseFixture : IAsyncDisposable
{
    public string RootDirectory { get; }
    public AresToysDatabase Database { get; }
    public IStoragePathResolver Paths { get; }
    public StorageOptions Options { get; }

    public TempDatabaseFixture()
    {
        RootDirectory = Path.Combine(Path.GetTempPath(), "AresToys.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(RootDirectory);

        Options = new StorageOptions { RootDirectoryOverride = RootDirectory };
        Paths = new StoragePathResolver(Microsoft.Extensions.Options.Options.Create(Options));

        var migrations = new IMigration[] { new Migration001InitialSchema(), new Migration002AddItemLabel(), new Migration003AddPinSortOrder(), new Migration004AddItemTrigger() };
        Database = new AresToysDatabase(Paths, new MigrationRunner(migrations), NullLogger<AresToysDatabase>.Instance);
    }

    public async Task<TempDatabaseFixture> InitializeAsync(CancellationToken ct = default)
    {
        await Database.InitializeAsync(ct).ConfigureAwait(false);
        return this;
    }

    public async ValueTask DisposeAsync()
    {
        await Database.DisposeAsync().ConfigureAwait(false);
        try
        {
            Directory.Delete(RootDirectory, recursive: true);
        }
        catch (IOException)
        {
            // Some OS file locks may linger briefly — ignore. Temp cleanup runs eventually.
        }
    }
}
