using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using AresToys.Storage.Database;
using AresToys.Storage.Database.Migrations;
using AresToys.Storage.Options;
using AresToys.Storage.Paths;

namespace AresToys.Pipeline.Tests.Fixtures;

public sealed class TempPipelineDatabaseFixture : IAsyncDisposable
{
    public string RootDirectory { get; }
    public AresToysDatabase Database { get; }
    public IStoragePathResolver Paths { get; }
    public StorageOptions Options { get; }

    public TempPipelineDatabaseFixture()
    {
        RootDirectory = Path.Combine(Path.GetTempPath(), "AresToys.PipelineTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(RootDirectory);

        Options = new StorageOptions { RootDirectoryOverride = RootDirectory };
        Paths = new StoragePathResolver(Microsoft.Extensions.Options.Options.Create(Options));

        var migrations = new IMigration[] { new Migration001InitialSchema(), new Migration002AddItemLabel(), new Migration003AddPinSortOrder(), new Migration004AddItemTrigger() };
        Database = new AresToysDatabase(Paths, new MigrationRunner(migrations), NullLogger<AresToysDatabase>.Instance);
    }

    public async Task<TempPipelineDatabaseFixture> InitializeAsync(CancellationToken ct = default)
    {
        await Database.InitializeAsync(ct).ConfigureAwait(false);
        return this;
    }

    public async ValueTask DisposeAsync()
    {
        await Database.DisposeAsync().ConfigureAwait(false);
        try { Directory.Delete(RootDirectory, recursive: true); }
        catch (IOException) { /* best effort */ }
    }
}
