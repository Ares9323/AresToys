using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace AresToys.App.Services.Recording;

/// <summary>Downloads ffmpeg.exe from github.com/ShareX/FFmpeg releases (the same source ShareX itself
/// uses on first run). Extracts only ffmpeg.exe into our Tools folder.</summary>
public sealed class FfmpegDownloader
{
    private const string ApiUrl = "https://api.github.com/repos/ShareX/FFmpeg/releases/latest";
    private static readonly HttpClient Http = CreateClient();

    private readonly ILogger<FfmpegDownloader> _logger;

    public FfmpegDownloader(ILogger<FfmpegDownloader> logger)
    {
        _logger = logger;
    }

    private static HttpClient CreateClient()
    {
        var c = new HttpClient();
        // GitHub API requires a User-Agent header.
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("AresToys", "1.0"));
        c.Timeout = TimeSpan.FromMinutes(5);
        return c;
    }

    /// <summary>Returns the path to the installed ffmpeg.exe, or null on failure.</summary>
    public async Task<string?> DownloadAsync(IProgress<string>? status, CancellationToken cancellationToken)
    {
        try
        {
            status?.Report("Looking up latest FFmpeg release…");
            var url = await ResolveDownloadUrlAsync(cancellationToken).ConfigureAwait(false);
            if (url is null) { _logger.LogWarning("No win64 asset in latest release"); return null; }

            Directory.CreateDirectory(FfmpegLocator.ToolsFolder);
            var zipPath = Path.Combine(FfmpegLocator.ToolsFolder, "ffmpeg-download.zip");

            status?.Report("Downloading FFmpeg…");
            using (var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                await using var fs = File.Create(zipPath);
                await resp.Content.CopyToAsync(fs, cancellationToken).ConfigureAwait(false);
            }

            status?.Report("Extracting ffmpeg.exe…");
            var ffmpegPath = Path.Combine(FfmpegLocator.ToolsFolder, "ffmpeg.exe");
            using (var archive = ZipFile.OpenRead(zipPath))
            {
                var entry = archive.Entries.FirstOrDefault(e => e.Name.Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase));
                if (entry is null) { _logger.LogWarning("ffmpeg.exe not found inside archive"); return null; }
                if (File.Exists(ffmpegPath)) File.Delete(ffmpegPath);
                entry.ExtractToFile(ffmpegPath);
            }
            try { File.Delete(zipPath); } catch { /* leftover zip is harmless */ }

            _logger.LogInformation("FFmpeg installed at {Path}", ffmpegPath);
            return ffmpegPath;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FFmpeg download failed");
            return null;
        }
    }

    /// <summary>Picks the win64.zip asset from the GitHub release JSON.</summary>
    private static async Task<string?> ResolveDownloadUrlAsync(CancellationToken cancellationToken)
    {
        await using var stream = await Http.GetStreamAsync(ApiUrl, cancellationToken).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!doc.RootElement.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;
        // Match either "...win64.zip" (older naming) or "...win-x64.zip" (current ShareX/FFmpeg
        // release naming, e.g. ffmpeg-8.0-win-x64.zip). Avoid arm64 / win-arm64.
        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
            if (name is null) continue;
            if (name.Contains("arm", StringComparison.OrdinalIgnoreCase)) continue;
            var lower = name.ToLowerInvariant();
            if (!lower.EndsWith(".zip", StringComparison.Ordinal)) continue;
            if (!lower.Contains("win-x64", StringComparison.Ordinal) && !lower.Contains("win64", StringComparison.Ordinal)) continue;
            return asset.TryGetProperty("browser_download_url", out var urlProp) ? urlProp.GetString() : null;
        }
        return null;
    }
}
