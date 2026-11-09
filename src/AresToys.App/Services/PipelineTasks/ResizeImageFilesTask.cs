using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using AresToys.App.Services.ImageResize;
using AresToys.App.Services.Shell;
using AresToys.Core.Pipeline;
using Microsoft.Extensions.Logging;

namespace AresToys.App.Services.PipelineTasks;

/// <summary>
/// Resizes and converts the image files selected in the foreground Explorer window and writes
/// the results to disk with no dialog. The input is the bag <see cref="PipelineBagKeys.SelectedFiles"/>
/// when the caller seeded it, else the <see cref="ExplorerSelection"/>, else the bag
/// <see cref="PipelineBagKeys.LocalPath"/>; with none of them the task does nothing. The resize and encode work is <see cref="ImageResizeEngine"/>, driven by the
/// engine config keys (mode, unit, width, height, shrink_only, ignore_orientation, format,
/// quality, strip_metadata). Disk writing is <see cref="ImageFileWriter"/>.
///
/// Extra config keys (all optional): <c>output_folder</c> ("" = next to each source file, a
/// relative value is relative to the source folder), <c>file_name_template</c>
/// ("{name}_{h}"), <c>preset_name</c> ("Resized"), <c>replace_original</c> (false),
/// <c>keep_date_modified</c> (false), <c>on_collision</c> (uniquify, overwrite or skip) and
/// <c>show_notification</c> (true).
///
/// Template tokens: {name}, {preset}, {w}, {h} (configured size), {ow}, {oh} (output pixel size),
/// {format} (output extension), {date}, {time}, {counter} (1-based index in the batch).
///
/// A file that is not a decodable image is counted as skipped. A failure on one file is
/// collected and never stops the batch.
/// </summary>
public sealed class ResizeImageFilesTask : IPipelineTask
{
    public const string TaskId = "arestoys.resize-image-files";

    private const string DefaultTemplate = "{name}_{h}";
    private const string DefaultPreset = "Resized";

    private readonly IToastNotifier? _toast;
    private readonly ILogger<ResizeImageFilesTask> _logger;

    public ResizeImageFilesTask(ILogger<ResizeImageFilesTask> logger, IToastNotifier? toast = null)
    {
        _logger = logger;
        _toast = toast;
    }

    public string Id => TaskId;
    public string DisplayName => "Resize image files";
    public PipelineTaskKind Kind => PipelineTaskKind.Both;

    public async Task ExecuteAsync(PipelineContext context, JsonNode? config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Tray-menu dismissal grace period, same as CaptureSelectedExplorerFileTask: without it
        // GetForegroundWindow returns AresToys's popup instead of the Explorer window.
        await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false);

        // Input order: files seeded by the caller (Explorer resize verbs), else the foreground
        // Explorer selection, else a single bag local_path.
        IReadOnlyList<string> paths = Array.Empty<string>();
        if (context.Bag.TryGetValue(PipelineBagKeys.SelectedFiles, out var rawSelected)
            && rawSelected is string[] seeded)
        {
            paths = seeded;
        }
        if (paths.Count == 0)
        {
            // COM marshalling has to happen on an STA thread; the WPF dispatcher is the one we own.
            paths = await Application.Current.Dispatcher
                .InvokeAsync(() => ExplorerSelection.GetSelectedPaths(_logger)).Task.ConfigureAwait(false);
        }
        if (paths.Count == 0
            && context.Bag.TryGetValue(PipelineBagKeys.LocalPath, out var rawLocal)
            && rawLocal is string localPath && !string.IsNullOrWhiteSpace(localPath))
        {
            paths = new[] { localPath };
        }
        if (paths.Count == 0)
        {
            _logger.LogDebug("ResizeImageFilesTask: no Explorer selection and no local_path; nothing to do");
            return;
        }

        var firstResultFolder = await RunBatchAsync(paths, config, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(firstResultFolder)) context.Bag[PipelineBagKeys.Text] = firstResultFolder;
    }

    /// <summary>
    /// The one resize loop: resizes and writes every file in <paramref name="files"/> with the
    /// step <paramref name="config"/>, then shows the summary toast. Shared by
    /// <see cref="ExecuteAsync"/> and the Explorer "Custom" quick dialog. Returns the folder of
    /// the first written file, or null when nothing was written.
    /// </summary>
    public async Task<string?> RunBatchAsync(IReadOnlyList<string> files, JsonNode? config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(files);
        var paths = files;
        var resize = ImageResizeOptions.FromConfig(config);
        var encode = ImageEncodeOptions.FromConfig(config);
        var outputFolder = ConfigReader.GetString(config, "output_folder");
        var template = ConfigReader.GetString(config, "file_name_template");
        if (string.IsNullOrWhiteSpace(template)) template = DefaultTemplate;
        var preset = ConfigReader.GetString(config, "preset_name");
        if (string.IsNullOrWhiteSpace(preset)) preset = DefaultPreset;
        var replaceOriginal = ConfigReader.GetBool(config, "replace_original") ?? false;
        var keepDate = ConfigReader.GetBool(config, "keep_date_modified") ?? false;
        var policy = ImageFileWriter.ParsePolicy(ConfigReader.GetString(config, "on_collision"));
        var showNotification = ConfigReader.GetBool(config, "show_notification") ?? true;
        var configuredW = FormatSize(ConfigReader.GetDouble(config, "width"));
        var configuredH = FormatSize(ConfigReader.GetDouble(config, "height"));

        int resized = 0, skipped = 0;
        var failures = new List<string>();
        string? firstResultFolder = null;

        for (var i = 0; i < paths.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = paths[i];
            try
            {
                if (!File.Exists(path))
                {
                    // Folders and vanished files in the selection are not images.
                    skipped++;
                    continue;
                }

                var input = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
                byte[] output;
                string extension;
                try
                {
                    (output, extension) = await Task.Run(() =>
                    {
                        var bytes = ImageResizeEngine.Process(input, resize, encode, out var ext);
                        return (bytes, ext);
                    }, cancellationToken).ConfigureAwait(false);
                }
                catch (InvalidDataException)
                {
                    // Not a decodable image: counted as skipped, not as an error.
                    skipped++;
                    continue;
                }

                string? written;
                if (replaceOriginal)
                {
                    written = ImageFileWriter.ReplaceOriginal(path, extension, output, keepDate);
                }
                else
                {
                    ImageFileWriter.TryGetPixelSize(output, out var ow, out var oh);
                    var tokens = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["name"] = Path.GetFileNameWithoutExtension(path),
                        ["preset"] = preset,
                        ["w"] = configuredW,
                        ["h"] = configuredH,
                        ["ow"] = ow.ToString(CultureInfo.InvariantCulture),
                        ["oh"] = oh.ToString(CultureInfo.InvariantCulture),
                        ["format"] = extension,
                        ["counter"] = (i + 1).ToString(CultureInfo.InvariantCulture),
                    };
                    var name = FileNameTemplate.Render(template, tokens);
                    written = ImageFileWriter.WriteNew(
                        ImageFileWriter.ResolveDirectory(path, outputFolder), name, extension, output, policy,
                        keepDate ? path : null);
                }

                if (written is null)
                {
                    // on_collision = skip and the target already exists.
                    skipped++;
                    continue;
                }

                resized++;
                firstResultFolder ??= Path.GetDirectoryName(written);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "ResizeImageFilesTask: failed on '{Path}'", path);
                failures.Add($"{Path.GetFileName(path)}: {ex.Message}");
            }
        }

        _logger.LogInformation("ResizeImageFilesTask: {Resized} resized, {Skipped} skipped, {Failed} failed",
            resized, skipped, failures.Count);

        if (showNotification && _toast is not null)
        {
            var culture = AresToys.App.Markup.LocalizedStrings.Instance.Culture ?? CultureInfo.CurrentUICulture;
            var title = Resources.Strings.ResourceManager.GetString("WorkflowAction_arestoys_resize_image_files", culture)
                        ?? DisplayName;
            var format = Resources.Strings.ResourceManager.GetString("ResizeImageFiles_Toast_Summary", culture)
                         ?? "{0} resized, {1} skipped, {2} failed";
            var body = string.Format(culture, format, resized, skipped, failures.Count);
            if (failures.Count > 0) body += Environment.NewLine + failures[0];
            _toast.Show(title, body);
        }

        return firstResultFolder;
    }

    /// <summary>The configured size as it appears in a file name: no decimals unless needed, 0
    /// when the side is automatic or unset.</summary>
    private static string FormatSize(double? value)
        => value is { } v && double.IsFinite(v) && v > 0
            ? v.ToString("0.##", CultureInfo.InvariantCulture)
            : "0";
}
