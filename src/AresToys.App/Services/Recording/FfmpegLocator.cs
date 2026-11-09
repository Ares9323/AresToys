using System.IO;
using Microsoft.Extensions.Logging;

namespace AresToys.App.Services.Recording;

/// <summary>Finds ffmpeg.exe — first in PATH, then in our tools folder under %APPDATA%/AresToys/Tools.
/// We don't bundle FFmpeg (size, license — gpl/lgpl matters); the user is expected to either install
/// it system-wide or drop ffmpeg.exe into the tools folder.</summary>
public sealed class FfmpegLocator
{
    private readonly ILogger<FfmpegLocator> _logger;
    private string? _cachedPath;

    public FfmpegLocator(ILogger<FfmpegLocator> logger)
    {
        _logger = logger;
    }

    public static string ToolsFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AresToys", "Tools");

    public string? Find()
    {
        if (_cachedPath is not null && File.Exists(_cachedPath)) return _cachedPath;

        // 1) tools folder (preferred — user-installed for AresToys specifically)
        var local = Path.Combine(ToolsFolder, "ffmpeg.exe");
        if (File.Exists(local)) { _cachedPath = local; return local; }

        // 2) PATH
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathEnv.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            try
            {
                var candidate = Path.Combine(dir, "ffmpeg.exe");
                if (File.Exists(candidate)) { _cachedPath = candidate; return candidate; }
            }
            catch (ArgumentException) { /* malformed PATH entry */ }
        }

        _logger.LogWarning("ffmpeg.exe not found. Drop it into {Folder} or add to PATH.", ToolsFolder);
        return null;
    }
}
