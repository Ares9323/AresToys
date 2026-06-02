using System.IO;
using AresToys.App.Services.Wormholes.Favicons;
using Xunit;

namespace AresToys.App.Tests.Wormholes.Favicons;

public class FaviconCacheTests : IDisposable
{
    private readonly string _dir;
    private readonly FaviconCache _cache;

    public FaviconCacheTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "arestoys-fav-tests-" + Guid.NewGuid().ToString("N"));
        _cache = new FaviconCache(_dir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    [Theory]
    [InlineData("Example.COM", "example.com")]
    [InlineData("sub.domain.co.uk", "sub.domain.co.uk")]
    [InlineData("weird/host:8080", "weird_host_8080")]
    public void SanitizeHost_LowercasesAndStripsUnsafe(string input, string expected)
        => Assert.Equal(expected, FaviconCache.SanitizeHost(input));

    [Fact]
    public void SaveThenHas_RoundTrips()
    {
        Assert.False(_cache.Has("example.com"));
        Assert.True(_cache.Save("example.com", new byte[] { 1, 2, 3 }));
        Assert.True(_cache.Has("example.com"));
        Assert.Equal(_cache.PathForHost("example.com"), Path.Combine(_dir, "example.com.ico"));
    }

    [Fact]
    public void Purge_DeletesUnreferenced_KeepsReferenced()
    {
        _cache.Save("keep.com", new byte[] { 1 });
        _cache.Save("drop.com", new byte[] { 1 });

        _cache.Purge(new[] { "keep.com" }, TimeSpan.FromDays(30), DateTime.UtcNow);

        Assert.True(_cache.Has("keep.com"));
        Assert.False(_cache.Has("drop.com"));
    }

    [Fact]
    public void Purge_DeletesStale_EvenIfReferenced()
    {
        _cache.Save("old.com", new byte[] { 1 });
        // Backdate the file 40 days.
        File.SetLastWriteTimeUtc(_cache.PathForHost("old.com"), DateTime.UtcNow.AddDays(-40));

        _cache.Purge(new[] { "old.com" }, TimeSpan.FromDays(30), DateTime.UtcNow);

        Assert.False(_cache.Has("old.com"));
    }

    [Fact]
    public void Purge_FreshReferenced_Survives()
    {
        _cache.Save("fresh.com", new byte[] { 1 });
        _cache.Purge(new[] { "fresh.com" }, TimeSpan.FromDays(30), DateTime.UtcNow);
        Assert.True(_cache.Has("fresh.com"));
    }

    [Fact]
    public void Purge_MissingDirectory_NoThrow()
    {
        var c = new FaviconCache(Path.Combine(_dir, "does-not-exist"));
        c.Purge(Array.Empty<string>(), TimeSpan.FromDays(30), DateTime.UtcNow); // must not throw
    }
}
