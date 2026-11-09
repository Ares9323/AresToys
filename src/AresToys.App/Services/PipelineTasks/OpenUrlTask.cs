using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using AresToys.Core.Pipeline;

namespace AresToys.App.Services.PipelineTasks;

/// <summary>
/// Opens <c>bag.upload_url</c> (or a custom URL via config) in the default browser. Typical use:
/// drop after an Upload step to auto-launch the result. Skips silently when no URL is available.
/// </summary>
public sealed class OpenUrlTask : IPipelineTask
{
    public const string TaskId = "arestoys.open-url";

    private readonly ILogger<OpenUrlTask> _logger;

    public OpenUrlTask(ILogger<OpenUrlTask> logger) { _logger = logger; }

    public string Id => TaskId;
    public string DisplayName => "Open URL";
    public PipelineTaskKind Kind => PipelineTaskKind.Both;

    public Task ExecuteAsync(PipelineContext context, JsonNode? config, CancellationToken cancellationToken)
    {
        // Resolution order: hardcoded config.url > bag.text. Upload steps now write their URL
        // straight into bag.text (the legacy bag.upload_url was retired in 0.1.17 — it only
        // ever held a duplicate of bag.text), so this single fallback covers both the
        // "open uploaded link" chain and the "open URL from Read clipboard" chain uniformly.
        var url = (string?)config?["url"];
        if (string.IsNullOrWhiteSpace(url)
            && context.Bag.TryGetValue(PipelineBagKeys.Text, out var rawText) && rawText is string text)
        {
            url = text;
        }
        if (string.IsNullOrWhiteSpace(url))
        {
            _logger.LogWarning("OpenUrlTask: no URL in config or bag; skipping");
            return Task.CompletedTask;
        }
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OpenUrlTask: failed to launch {Url}", url);
        }
        return Task.CompletedTask;
    }
}
