namespace AresToys.App.Services.Wormholes.Favicons;

/// <summary>Fetches a favicon for a web link and returns it as ready-to-write <c>.ico</c> bytes
/// (or null when nothing usable could be obtained). Abstracted so <see cref="FaviconService"/>
/// can be unit-tested with a fake that never touches the network.</summary>
public interface IFaviconDownloader
{
    /// <param name="pageUrl">The full URL from the <c>.url</c> file (used for the HTML
    /// <c>&lt;link rel=icon&gt;</c> probe and to resolve relative icon hrefs).</param>
    /// <param name="host">The host derived from <paramref name="pageUrl"/>.</param>
    /// <returns>Valid <c>.ico</c> bytes, or null if every source failed.</returns>
    Task<byte[]?> DownloadIcoAsync(string pageUrl, string host, CancellationToken ct);
}
