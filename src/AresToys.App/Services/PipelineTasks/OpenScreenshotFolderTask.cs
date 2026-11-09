using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using AresToys.Core.Pipeline;
using AresToys.Storage.Settings;

namespace AresToys.App.Services.PipelineTasks;

/// <summary>
/// Opens the configured screenshot capture folder in Windows Explorer. Reuses the same
/// <c>capture.folder</c> setting / default as <c>SaveToFileTask</c> so the user always lands
/// where their captures are written. Creates the folder if missing.
/// </summary>
public sealed class OpenScreenshotFolderTask : IPipelineTask
{
    public const string TaskId = "arestoys.open-screenshot-folder";
    private const string DefaultFolder = "%USERPROFILE%\\Pictures\\AresToys";
    private const string FolderSettingKey = "capture.folder";

    private readonly ISettingsStore _settings;
    private readonly ILogger<OpenScreenshotFolderTask> _logger;

    public OpenScreenshotFolderTask(ISettingsStore settings, ILogger<OpenScreenshotFolderTask> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public string Id => TaskId;
    public string DisplayName => "Open screenshot folder";
    public PipelineTaskKind Kind => PipelineTaskKind.Both;

    public async Task ExecuteAsync(PipelineContext context, JsonNode? config, CancellationToken cancellationToken)
    {
        var folderTemplate = (string?)config?["folder"]
            ?? await _settings.GetAsync(FolderSettingKey, cancellationToken).ConfigureAwait(false)
            ?? DefaultFolder;
        var folder = Environment.ExpandEnvironmentVariables(folderTemplate);
        Directory.CreateDirectory(folder);

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{folder}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OpenScreenshotFolderTask: failed to open {Folder}", folder);
        }
    }
}
