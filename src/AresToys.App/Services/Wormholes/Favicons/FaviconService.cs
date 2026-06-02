using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace AresToys.App.Services.Wormholes.Favicons;

/// <summary>Ensures a wormhole web link (<c>.url</c>) shows the real site favicon. Single entry
/// point <see cref="EnsureFaviconAsync"/>: parse the URL, resolve/download a per-host cached
/// <c>.ico</c>, and write <c>IconFile=</c> into the <c>.url</c> so the existing IconService (and
/// Explorer) render it. Idempotent and host-cached, so the repeated calls that happen on every
/// FolderWatcher refresh are cheap no-ops once a link is resolved.</summary>
public sealed class FaviconService : IDisposable
{
    private readonly FaviconCache _cache;
    private readonly IFaviconDownloader _downloader;
    private readonly ILogger<FaviconService> _logger;

    // Cap concurrent downloads so a wormhole full of links doesn't open dozens of sockets at once.
    private readonly SemaphoreSlim _gate = new(4, 4);
    // Per-host in-flight guard: collapses a burst of items for the same host onto one download.
    private readonly ConcurrentDictionary<string, Task<bool>> _inFlight = new(StringComparer.OrdinalIgnoreCase);
    // Negative cache: host → last failure (UTC). Skip re-fetching a failing host for a day so an
    // offline session / unreachable site doesn't hammer the network on every refresh.
    private readonly ConcurrentDictionary<string, DateTime> _negative = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan NegativeTtl = TimeSpan.FromHours(24);
    private static readonly TimeSpan RefreshAge = TimeSpan.FromDays(30);

    public FaviconService(FaviconCache cache, IFaviconDownloader downloader, ILogger<FaviconService> logger)
    {
        _cache = cache;
        _downloader = downloader;
        _logger = logger;
    }

    /// <summary>Resolve the favicon for a <c>.url</c> and make sure it's wired into the file.
    /// Returns true when something changed this call (a download happened or <c>IconFile=</c> was
    /// (re)written) — the caller uses that to know whether to re-render the tile. Returns false
    /// when the link is already wired up, isn't a web link, or resolution failed.</summary>
    public async Task<bool> EnsureFaviconAsync(string urlFilePath, CancellationToken ct)
    {
        var url = UrlShortcutFile.ReadUrl(urlFilePath);
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!TryGetHttpHost(url, out var host)) return false;

        var icoPath = _cache.PathForHost(host);
        var cached = _cache.Has(host);

        if (!cached)
        {
            if (RecentlyFailed(host)) return false;
            var downloaded = await DownloadOncePerHostAsync(url, host, ct).ConfigureAwait(false);
            if (!downloaded) return false;
        }

        // Cache file is present now — make sure the .url points at it. If it already does, nothing
        // changed (idempotent path); report change only when we downloaded or rewrote the link.
        var alreadyWired = string.Equals(
            UrlShortcutFile.ReadIconFile(urlFilePath), icoPath, StringComparison.OrdinalIgnoreCase);
        if (alreadyWired && cached) return false;

        var wrote = UrlShortcutFile.SetIcon(urlFilePath, icoPath, iconIndex: 0);
        if (!wrote)
        {
            // Read-only .url or write failure: the cached .ico still exists, so the tile can fall
            // back to in-app rendering. Treat as "changed" so the caller re-renders from cache.
            _logger.LogDebug("Could not write IconFile into {Path}; relying on cached .ico", urlFilePath);
        }
        // Either we wrote IconFile, or we just downloaded (cached flipped false→true): both warrant
        // a re-render of the tile.
        return true;
    }

    /// <summary>Delete cached icons no longer referenced by any live host (and stale ones past the
    /// refresh age). Called at startup by the wormhole manager with the set of hosts it finds
    /// across all live <c>.url</c> files.</summary>
    public void PurgeUnreferenced(IReadOnlyCollection<string> liveHosts)
        => _cache.Purge(liveHosts, RefreshAge, DateTime.UtcNow);

    /// <summary>Extract a host from an http(s) URL. Non-http schemes (file:, ftp:, custom
    /// protocols) and malformed URLs are rejected.</summary>
    public static bool TryGetHttpHost(string url, out string host)
    {
        host = string.Empty;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;
        if (string.IsNullOrEmpty(uri.Host)) return false;
        host = uri.Host;
        return true;
    }

    private async Task<bool> DownloadOncePerHostAsync(string url, string host, CancellationToken ct)
    {
        // Collapse concurrent requests for the same host onto a single task.
        var task = _inFlight.GetOrAdd(host, _ => DownloadAsync(url, host, ct));
        try { return await task.ConfigureAwait(false); }
        finally { _inFlight.TryRemove(host, out _); }
    }

    private async Task<bool> DownloadAsync(string url, string host, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Another item for the same host may have just populated the cache while we queued.
            if (_cache.Has(host)) return true;

            var ico = await _downloader.DownloadIcoAsync(url, host, ct).ConfigureAwait(false);
            if (ico is null || ico.Length == 0)
            {
                _negative[host] = DateTime.UtcNow;
                _logger.LogDebug("No favicon resolved for host {Host}", host);
                return false;
            }
            if (!_cache.Save(host, ico))
            {
                _negative[host] = DateTime.UtcNow;
                return false;
            }
            _negative.TryRemove(host, out _);
            return true;
        }
        catch (Exception ex)
        {
            _negative[host] = DateTime.UtcNow;
            _logger.LogDebug(ex, "Favicon download failed for host {Host}", host);
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool RecentlyFailed(string host)
        => _negative.TryGetValue(host, out var when) && (DateTime.UtcNow - when) < NegativeTtl;

    public void Dispose() => _gate.Dispose();
}
