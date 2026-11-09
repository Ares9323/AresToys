using Microsoft.Extensions.Options;
using AresToys.Storage.Options;
using AresToys.Storage.Paths;

namespace AresToys.Storage.Tests.Fixtures;

/// <summary>Temp directory + path resolver, no database — for testing pure FS components.</summary>
public sealed class TempDirectoryFixture : IDisposable
{
    public string RootDirectory { get; }
    public IStoragePathResolver Paths { get; }

    public TempDirectoryFixture()
    {
        RootDirectory = Path.Combine(Path.GetTempPath(), "AresToys.Tests.Dirs", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(RootDirectory);
        var options = new StorageOptions { RootDirectoryOverride = RootDirectory };
        Paths = new StoragePathResolver(Microsoft.Extensions.Options.Options.Create(options));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(RootDirectory, recursive: true);
        }
        catch (IOException) { /* best effort */ }
    }
}
