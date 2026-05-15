using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using AresToys.Core.Pipeline;
using AresToys.Pipeline.Tasks;
using AresToys.Storage.Settings;

namespace AresToys.App.Services.PipelineTasks;

/// <summary>Pipeline step that picks up the SVG string written into the bag by
/// <see cref="TraceToSvgTask"/> (key <c>svg_output</c>) and writes it to disk as a
/// <c>.svg</c> file alongside the raster. Distinct from Save-as-Image because the standard
/// Save-to-file reads the raster <c>payload_bytes</c> bag key and would otherwise overwrite
/// or ignore the SVG side channel. Silent no-op when <c>svg_output</c> is missing.
///
/// Folder resolution is the SAME as Save-as-Image: when a previous Save-as-Image step set
/// <see cref="PipelineBagKeys.LocalPath"/>, we reuse that path's directory + stem so the
/// .svg sits literally next to the .png in whatever subfolder the raster ended up in. When
/// no raster was saved (Trace-only workflow), fall back to <c>capture.folder</c> +
/// <c>capture.subfolder_pattern</c> resolved fresh — same setting keys + token expansion as
/// SaveToFileTask, so a custom %y/%mo subfolder pattern applies to standalone SVG saves
/// too.</summary>
public sealed class SaveSvgTask : IPipelineTask
{
    public const string TaskId = "arestoys.save-svg";

    /// <summary>Bag key under which we publish the absolute path to the saved .svg, mirroring
    /// <see cref="PipelineBagKeys.LocalPath"/> for the raster. Downstream toast / clipboard
    /// steps can reference <c>{bag.svg_local_path}</c> in their templates.</summary>
    public const string SvgLocalPathBagKey = "svg_local_path";

    private const string DefaultFolder = "%USERPROFILE%\\Pictures\\AresToys";
    private const string FolderSettingKey = "capture.folder";
    private const string SubFolderPatternSettingKey = "capture.subfolder_pattern";

    private readonly ISettingsStore _settings;
    private readonly ILogger<SaveSvgTask> _logger;

    public SaveSvgTask(ISettingsStore settings, ILogger<SaveSvgTask> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public string Id => TaskId;
    public string DisplayName => "Save as SVG";
    public PipelineTaskKind Kind => PipelineTaskKind.PostCapture;

    public async Task ExecuteAsync(PipelineContext context, JsonNode? config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.Bag.TryGetValue(TraceToSvgTask.SvgOutputBagKey, out var raw) || raw is not string svg || svg.Length == 0)
        {
            return; // silent skip — Trace-to-SVG didn't run or produced nothing
        }

        // Folder + stem resolution: prefer "lands beside the raster" (uses raster's whole
        // directory, including the SaveToFileTask-applied subfolder pattern). Falls back to
        // computing folder+pattern fresh when no raster was written this run.
        string folder;
        string stem;
        if (context.Bag.TryGetValue(PipelineBagKeys.LocalPath, out var rawPath)
            && rawPath is string rasterPath
            && !string.IsNullOrEmpty(rasterPath))
        {
            folder = Path.GetDirectoryName(rasterPath) ?? await ResolveCaptureFolderAsync(config, cancellationToken).ConfigureAwait(false);
            stem = Path.GetFileNameWithoutExtension(rasterPath);
        }
        else
        {
            folder = await ResolveCaptureFolderAsync(config, cancellationToken).ConfigureAwait(false);
            stem = "arestoys-" + DateTimeOffset.Now.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture);
        }
        Directory.CreateDirectory(folder);

        var svgPath = Path.Combine(folder, stem + ".svg");
        // Append " (2)", " (3)", … on collision — SVG strings are small, the existence check
        // + write is cheap. Bound at 999 attempts; if we somehow hit that, fall back to a
        // guid suffix rather than spinning forever.
        var actualPath = svgPath;
        for (var n = 2; File.Exists(actualPath) && n < 1000; n++)
            actualPath = Path.Combine(folder, $"{stem} ({n}).svg");

        try
        {
            // UTF-8 NO BOM. SVG is XML; Inkscape / Illustrator / browser parsers tolerate a
            // BOM but most svgo / build-pipeline tooling around SVG prefers BOMless.
            await File.WriteAllTextAsync(actualPath, svg, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken).ConfigureAwait(false);
            context.Bag[SvgLocalPathBagKey] = actualPath;
            _logger.LogInformation("SaveSvgTask: wrote {Path} ({Bytes} bytes)", actualPath, svg.Length);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SaveSvgTask: failed to write {Path}", actualPath);
        }
    }

    /// <summary>Resolve the capture folder + subfolder pattern using the same settings as
    /// SaveToFileTask. Used as the fallback path when no <c>bag.local_path</c> exists (Trace-
    /// only workflow with no preceding Save-as-Image).</summary>
    private async Task<string> ResolveCaptureFolderAsync(JsonNode? config, CancellationToken ct)
    {
        var folderTemplate = (string?)config?["folder"]
            ?? await _settings.GetAsync(FolderSettingKey, ct).ConfigureAwait(false)
            ?? DefaultFolder;
        var folder = Environment.ExpandEnvironmentVariables(folderTemplate);

        var subPatternRaw = (string?)config?["subfolder_pattern"]
            ?? await _settings.GetAsync(SubFolderPatternSettingKey, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(subPatternRaw))
        {
            var sub = DatePatternExpander.Expand(Environment.ExpandEnvironmentVariables(subPatternRaw), DateTime.Now);
            folder = Path.Combine(folder, sub);
        }
        return folder;
    }
}
