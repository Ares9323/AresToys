using System.IO;
using AresToys.Core.Pipeline;
using Microsoft.Extensions.Logging;

namespace AresToys.App.Services.ImageResize;

/// <summary>
/// Collects the files of the Explorer resize verbs. Explorer starts one process per selected file
/// (<c>arestoys.exe --resize &lt;workflowId&gt; "&lt;path&gt;"</c>), so the primary instance receives
/// the files one at a time. <see cref="Add"/> gathers them per workflow id and restarts a short
/// timer on every call; when the requests stop arriving the whole batch runs once: the workflow
/// gets every file together in the bag key <see cref="PipelineBagKeys.SelectedFiles"/>.
///
/// The workflow id <see cref="CustomWorkflowId"/> is not a workflow: the batch is handed to
/// <see cref="CustomRequested"/> instead, so the custom size dialog can open for all the files.
/// </summary>
public sealed class ResizeRequestBatcher : IDisposable
{
    /// <summary>The id the Custom verb passes instead of a workflow id.</summary>
    public const string CustomWorkflowId = "custom";

    /// <summary>How long the batch waits for one more file before it runs.</summary>
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(700);

    private readonly WorkflowRunner _runner;
    private readonly ILogger<ResizeRequestBatcher> _logger;
    private readonly object _gate = new();
    private readonly Dictionary<string, List<string>> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer _timer;
    private bool _disposed;

    public ResizeRequestBatcher(WorkflowRunner runner, ILogger<ResizeRequestBatcher> logger)
    {
        _runner = runner;
        _logger = logger;
        _timer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Raised when a batch for <see cref="CustomWorkflowId"/> is ready, with the existing,
    /// de-duplicated absolute paths. Raised on a thread-pool thread: marshal to the UI dispatcher
    /// before touching WPF state. With no subscriber the batch is dropped with a warning.</summary>
    public event Action<IReadOnlyList<string>>? CustomRequested;

    /// <summary>Queues one file (an absolute path) for <paramref name="workflowId"/> and restarts
    /// the quiet timer. Safe to call from any thread.</summary>
    public void Add(string workflowId, string path)
    {
        if (string.IsNullOrWhiteSpace(workflowId) || string.IsNullOrWhiteSpace(path)) return;
        var id = workflowId.Trim();

        lock (_gate)
        {
            if (_disposed) return;
            if (!_pending.TryGetValue(id, out var list))
            {
                list = new List<string>();
                _pending[id] = list;
            }
            list.Add(path);
            _timer.Change(Quiet, Timeout.InfiniteTimeSpan);
        }
    }

    private void Flush()
    {
        Dictionary<string, List<string>> batches;
        lock (_gate)
        {
            if (_disposed || _pending.Count == 0) return;
            batches = new Dictionary<string, List<string>>(_pending, StringComparer.OrdinalIgnoreCase);
            _pending.Clear();
        }

        foreach (var (id, raw) in batches)
        {
            // File.Exists never throws, so a malformed path is simply dropped here.
            var paths = raw
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(File.Exists)
                .ToArray();
            if (paths.Length == 0)
            {
                _logger.LogWarning("ResizeRequestBatcher: no existing file left for '{Id}'; nothing to do", id);
                continue;
            }

            if (string.Equals(id, CustomWorkflowId, StringComparison.OrdinalIgnoreCase))
            {
                var handler = CustomRequested;
                if (handler is null)
                {
                    _logger.LogWarning("ResizeRequestBatcher: custom resize requested for {Count} file(s) but nothing handles it", paths.Length);
                    continue;
                }
                try { handler(paths); }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "ResizeRequestBatcher: the custom resize handler failed");
                }
                continue;
            }

            _logger.LogInformation("ResizeRequestBatcher: running workflow {Id} on {Count} file(s)", id, paths.Length);
            _runner.RunDetached(id, new Dictionary<string, object> { [PipelineBagKeys.SelectedFiles] = paths });
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _pending.Clear();
        }
        _timer.Dispose();
    }
}
