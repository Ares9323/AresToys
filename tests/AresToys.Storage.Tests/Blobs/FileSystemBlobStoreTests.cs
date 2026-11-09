using System.Text;
using AresToys.Storage.Blobs;
using AresToys.Storage.Tests.Fixtures;
using Xunit;

namespace AresToys.Storage.Tests.Blobs;

public class FileSystemBlobStoreTests
{
    [Fact]
    public async Task Add_Then_ReadAll_RoundTripsContent()
    {
        using var fx = new TempDirectoryFixture();
        IBlobStore store = new FileSystemBlobStore(fx.Paths);
        var content = Encoding.UTF8.GetBytes("hello blob");

        var blobRef = await store.AddAsync(content, "bin", DateTimeOffset.UtcNow, CancellationToken.None);
        var readBack = await store.ReadAllAsync(blobRef, CancellationToken.None);

        Assert.Equal(content, readBack);
    }

    [Fact]
    public async Task Add_PlacesBlobUnderYearMonthDayPath()
    {
        using var fx = new TempDirectoryFixture();
        IBlobStore store = new FileSystemBlobStore(fx.Paths);
        var ts = new DateTimeOffset(2026, 4, 27, 0, 0, 0, TimeSpan.Zero);

        var blobRef = await store.AddAsync(new byte[] { 1, 2, 3 }, "png", ts, CancellationToken.None);

        Assert.StartsWith("2026/04/27/", blobRef);
        Assert.EndsWith(".png", blobRef);
    }

    [Fact]
    public async Task Delete_Existing_ReturnsTrueAndRemovesFile()
    {
        using var fx = new TempDirectoryFixture();
        IBlobStore store = new FileSystemBlobStore(fx.Paths);
        var blobRef = await store.AddAsync(new byte[] { 0xFF }, "bin", DateTimeOffset.UtcNow, CancellationToken.None);

        var deleted = await store.DeleteAsync(blobRef, CancellationToken.None);

        Assert.True(deleted);
        await Assert.ThrowsAsync<FileNotFoundException>(() => store.ReadAllAsync(blobRef, CancellationToken.None));
    }

    [Fact]
    public async Task Delete_Missing_ReturnsFalse()
    {
        using var fx = new TempDirectoryFixture();
        IBlobStore store = new FileSystemBlobStore(fx.Paths);

        var deleted = await store.DeleteAsync("2099/01/01/deadbeef.bin", CancellationToken.None);

        Assert.False(deleted);
    }

    [Fact]
    public async Task EnumerateAll_ListsEveryAddedBlob()
    {
        using var fx = new TempDirectoryFixture();
        IBlobStore store = new FileSystemBlobStore(fx.Paths);
        await store.AddAsync(new byte[] { 1 }, "bin", DateTimeOffset.UtcNow, CancellationToken.None);
        await store.AddAsync(new byte[] { 2 }, "bin", DateTimeOffset.UtcNow, CancellationToken.None);
        await store.AddAsync(new byte[] { 3 }, "bin", DateTimeOffset.UtcNow, CancellationToken.None);

        var listed = new List<string>();
        await foreach (var blobRef in store.EnumerateAllAsync(CancellationToken.None))
        {
            listed.Add(blobRef);
        }

        Assert.Equal(3, listed.Count);
    }
}
