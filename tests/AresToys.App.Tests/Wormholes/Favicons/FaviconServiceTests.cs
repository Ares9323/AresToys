using System.IO;
using AresToys.App.Services.Wormholes.Favicons;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AresToys.App.Tests.Wormholes.Favicons;

public class FaviconServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly FaviconCache _cache;

    public FaviconServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "arestoys-favsvc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _cache = new FaviconCache(Path.Combine(_dir, "cache"));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private string WriteUrlFile(string url)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".url");
        File.WriteAllLines(path, new[] { "[InternetShortcut]", "URL=" + url });
        return path;
    }

    private FaviconService NewService(FakeDownloader downloader)
        => new(_cache, downloader, NullLogger<FaviconService>.Instance);

    [Fact]
    public async Task EnsureFavicon_DownloadsAndWritesIconFile()
    {
        var dl = new FakeDownloader { Result = new byte[] { 0, 0, 1, 0, 9 } };
        var svc = NewService(dl);
        var url = WriteUrlFile("https://example.com/page");

        var changed = await svc.EnsureFaviconAsync(url, CancellationToken.None);

        Assert.True(changed);
        Assert.Equal(1, dl.Calls);
        Assert.True(_cache.Has("example.com"));
        Assert.Equal(_cache.PathForHost("example.com"), UrlShortcutFile.ReadIconFile(url));
    }

    [Fact]
    public async Task EnsureFavicon_SecondCall_IsNoOp()
    {
        var dl = new FakeDownloader { Result = new byte[] { 0, 0, 1, 0, 9 } };
        var svc = NewService(dl);
        var url = WriteUrlFile("https://example.com/page");

        Assert.True(await svc.EnsureFaviconAsync(url, CancellationToken.None));
        Assert.False(await svc.EnsureFaviconAsync(url, CancellationToken.None));
        Assert.Equal(1, dl.Calls); // cached → no second download
    }

    [Fact]
    public async Task EnsureFavicon_NonHttp_Skips()
    {
        var dl = new FakeDownloader { Result = new byte[] { 0, 0, 1, 0 } };
        var svc = NewService(dl);
        var url = WriteUrlFile("ftp://example.com/file");

        Assert.False(await svc.EnsureFaviconAsync(url, CancellationToken.None));
        Assert.Equal(0, dl.Calls);
    }

    [Fact]
    public async Task EnsureFavicon_DownloadFails_NegativeCachePreventsRefetch()
    {
        var dl = new FakeDownloader { Result = null }; // every download fails
        var svc = NewService(dl);
        var url = WriteUrlFile("https://nope.example/x");

        Assert.False(await svc.EnsureFaviconAsync(url, CancellationToken.None));
        Assert.False(await svc.EnsureFaviconAsync(url, CancellationToken.None));
        Assert.Equal(1, dl.Calls); // second attempt short-circuited by the negative cache
        Assert.Null(UrlShortcutFile.ReadIconFile(url));
    }

    private sealed class FakeDownloader : IFaviconDownloader
    {
        public int Calls;
        public byte[]? Result;

        public Task<byte[]?> DownloadIcoAsync(string pageUrl, string host, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(Result);
        }
    }
}
