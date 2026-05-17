using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using AresToys.Core.Pipeline;
using AresToys.Pipeline.Registry;

namespace AresToys.Pipeline;

public sealed class PipelineExecutor
{
    private readonly IPipelineTaskRegistry? _registry;
    private readonly ILogger<PipelineExecutor> _logger;

    public PipelineExecutor()
    {
        _registry = null;
        _logger = NullLogger<PipelineExecutor>.Instance;
    }

    public PipelineExecutor(ILogger<PipelineExecutor> logger)
    {
        _registry = null;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public PipelineExecutor(IPipelineTaskRegistry registry, ILogger<PipelineExecutor> logger)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task RunAsync(
        IReadOnlyList<IPipelineTask> tasks,
        PipelineContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(context);

        _logger.LogInformation("Pipeline starting ({Count} steps)", tasks.Count);
        var totalSw = System.Diagnostics.Stopwatch.StartNew();
        for (var index = 0; index < tasks.Count; index++)
        {
            if (context.Aborted)
            {
                _logger.LogInformation("Pipeline aborted at step {Index} ({Why})", index, context.AbortReason ?? "no reason");
                break;
            }
            cancellationToken.ThrowIfCancellationRequested();

            var task = tasks[index];
            _logger.LogInformation("→ Step {Index}/{Total}: {TaskId}", index + 1, tasks.Count, task.Id);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                await task.ExecuteAsync(context, config: null, cancellationToken).ConfigureAwait(false);
                sw.Stop();
                _logger.LogInformation("✓ Step {Index} {TaskId} done in {Ms} ms", index + 1, task.Id, sw.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                sw.Stop();
                _logger.LogError(ex, "✗ Step {Index} {TaskId} threw after {Ms} ms", index + 1, task.Id, sw.ElapsedMilliseconds);
                throw;
            }
        }
        totalSw.Stop();
        _logger.LogInformation("Pipeline done in {Ms} ms", totalSw.ElapsedMilliseconds);
    }

    public async Task RunAsync(
        PipelineProfile profile,
        PipelineContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(context);
        if (_registry is null)
            throw new InvalidOperationException("Profile-based RunAsync requires a registry; construct PipelineExecutor with one.");

        _logger.LogInformation("Workflow '{Profile}' starting ({Count} steps)", profile.Id, profile.Steps.Count);
        var totalSw = System.Diagnostics.Stopwatch.StartNew();
        var ran = 0;
        for (var index = 0; index < profile.Steps.Count; index++)
        {
            if (context.Aborted)
            {
                _logger.LogInformation("Workflow '{Profile}' aborted at step {Index}: {Why}",
                    profile.Id, index, context.AbortReason ?? "no reason");
                break;
            }
            cancellationToken.ThrowIfCancellationRequested();

            var step = profile.Steps[index];
            if (!step.Enabled)
            {
                _logger.LogDebug("Workflow '{Profile}' step {Index} ({TaskId}) skipped (disabled)",
                    profile.Id, index, step.TaskId);
                continue;
            }

            // Repeat task — special-cased: instead of executing it as a regular step, capture
            // every step between this one and the matching End Repeat (or end-of-workflow when
            // no End is present) and re-execute that block N times. The outer loop resumes at
            // the index AFTER the End Repeat — anything past the End runs normally, once per
            // workflow execution.
            if (string.Equals(step.TaskId, RepeatTaskId, StringComparison.Ordinal))
            {
                var (executed, resumeIndex) = await RunRepeatBlockAsync(profile, context, index, cancellationToken).ConfigureAwait(false);
                ran += executed;
                index = resumeIndex - 1; // -1 because the for-loop's ++ will land us on resumeIndex
                continue;
            }

            // Orphan End Repeat (no preceding Repeat opened a scope) — runtime no-op. The
            // editor's indent walker also clamps the visual depth to 0 and flags the step with
            // a warning banner so the user sees the inconsistency.
            if (string.Equals(step.TaskId, EndRepeatTaskId, StringComparison.Ordinal))
            {
                _logger.LogDebug("Workflow '{Profile}' step {Index}: orphan End Repeat — skipping", profile.Id, index + 1);
                continue;
            }

            var task = _registry.Resolve(step.TaskId);
            if (task is null)
            {
                _logger.LogWarning("Workflow '{Profile}' step {Index}: task {TaskId} not registered, skipping",
                    profile.Id, index, step.TaskId);
                if (step.AbortOnError) context.Abort($"task {step.TaskId} not registered");
                continue;
            }

            ran++;
            _logger.LogInformation("→ '{Profile}' step {Index}: {TaskId}", profile.Id, index + 1, task.Id);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                await task.ExecuteAsync(context, step.Config, cancellationToken).ConfigureAwait(false);
                sw.Stop();
                _logger.LogInformation("✓ '{Profile}' step {Index} {TaskId} done in {Ms} ms",
                    profile.Id, index + 1, task.Id, sw.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                sw.Stop();
                _logger.LogError(ex, "✗ '{Profile}' step {Index} ({TaskId}) threw after {Ms} ms",
                    profile.Id, index + 1, task.Id, sw.ElapsedMilliseconds);
                if (step.AbortOnError) context.Abort($"task {task.Id} threw: {ex.Message}");
            }
        }
        totalSw.Stop();
        _logger.LogInformation("Workflow '{Profile}' done — {Ran} step(s) ran in {Ms} ms",
            profile.Id, ran, totalSw.ElapsedMilliseconds);
    }

    // ── Repeat task — TaskId hardcoded here so the executor recognises it without needing a
    //    new IPipelineTask interface variant for control-flow tasks. RepeatTask.cs / EndRepeatTask.cs
    //    in App are no-op markers that surface the tasks in the workflow editor + DI; the actual
    //    loop logic lives below.
    private const string RepeatTaskId = "arestoys.repeat";
    private const string EndRepeatTaskId = "arestoys.end-repeat";

    /// <summary>Run the body slice [repeatIndex+1 .. endIndex-1] as a loop body, repeated
    /// <c>config.count</c> times with <c>config.delayMs</c> between iterations. <c>endIndex</c>
    /// is the position of the matching <see cref="EndRepeatTaskId"/> marker, or
    /// <c>profile.Steps.Count</c> if no End Repeat is present (back-compat: Repeat without End
    /// loops the entire tail). Optional <c>config.cancelCombo</c> registers a temporary global
    /// hotkey via <see cref="ICancelHotkeyRegistry"/> that cancels the inner CTS so the user
    /// can break out of long-running loops.
    /// Returns the body-task invocation count (across all iterations) and the index at which
    /// the outer executor loop should resume — past the End Repeat marker, or end-of-steps.
    /// </summary>
    private async Task<(int Ran, int ResumeIndex)> RunRepeatBlockAsync(PipelineProfile profile, PipelineContext context, int repeatIndex, CancellationToken outerToken)
    {
        var repeatStep = profile.Steps[repeatIndex];
        var rawCount = (int?)repeatStep.Config?["count"] ?? 1;
        var count = Math.Clamp(rawCount, 1, 1000);
        // delayMs is shaped as a string in the catalog (StringParameter, see note in
        // WorkflowActionCatalog.cs) so a JsonNode of type Number can't be cast directly. Read
        // the raw node and accept both shapes — int.TryParse on the string, GetValue on the
        // number — so back-compat profiles authored before the string switch keep working.
        var delayMs = 0;
        var delayNode = repeatStep.Config?["delayMs"];
        if (delayNode is not null)
        {
            try
            {
                delayMs = delayNode.GetValueKind() switch
                {
                    System.Text.Json.JsonValueKind.Number => delayNode.GetValue<int>(),
                    System.Text.Json.JsonValueKind.String when int.TryParse(delayNode.GetValue<string>(), out var ms) => ms,
                    _ => 0,
                };
            }
            catch { delayMs = 0; }
        }
        delayMs = Math.Max(0, delayMs);
        var cancelCombo = (string?)repeatStep.Config?["cancelCombo"];

        // Find the matching End Repeat marker. No nesting support — first End we hit closes
        // this Repeat. If we never find one, the body extends to end-of-workflow (back-compat
        // with the original tail-only behaviour).
        var endIndex = profile.Steps.Count;
        for (var i = repeatIndex + 1; i < profile.Steps.Count; i++)
        {
            if (string.Equals(profile.Steps[i].TaskId, EndRepeatTaskId, StringComparison.Ordinal))
            {
                endIndex = i;
                break;
            }
        }

        _logger.LogInformation("Repeat: {Count} iterations of {BodyCount} body steps (delay {Delay} ms, cancel '{Cancel}', body=[{Start}..{End}])",
            count, endIndex - repeatIndex - 1, delayMs, cancelCombo ?? "(none)", repeatIndex + 1, endIndex - 1);

        using var inner = CancellationTokenSource.CreateLinkedTokenSource(outerToken);
        IDisposable? hotkeyToken = null;
        if (!string.IsNullOrWhiteSpace(cancelCombo))
        {
            // Resolve the registry from the context's service provider — Pipeline can't reference
            // Hotkeys directly, so the App-layer DI binding is the bridge. Null result =
            // dev/test contexts without the binding; the loop just runs without a cancel
            // shortcut.
            var registry = (ICancelHotkeyRegistry?)context.Services.GetService(typeof(ICancelHotkeyRegistry));
            if (registry is not null)
            {
                hotkeyToken = registry.Register(cancelCombo, () =>
                {
                    _logger.LogInformation("Repeat: cancel hotkey '{Combo}' pressed — breaking loop", cancelCombo);
                    try { inner.Cancel(); }
                    catch (ObjectDisposedException) { /* loop already exited */ }
                });
            }
        }

        // Snapshot the bag state ONCE before the loop body kicks in. Each iteration after the
        // first restores the bag to this exact state — without this, transient bag keys written
        // by iter N (Capture region's payload_bytes, Save's local_path, etc.) carry over into
        // iter N+1, and tasks with "skip if X already present" shortcuts (CaptureRegionTask:57)
        // wrongly re-use the previous iteration's payload instead of re-prompting the user.
        // Pre-Repeat state survives the snapshot, so anything set outside the loop (a Capture
        // BEFORE the Repeat, an external CaptureCoordinator pre-fill) keeps working correctly.
        var bagSnapshot = new Dictionary<string, object>(context.Bag);

        var ran = 0;
        try
        {
            for (var iter = 0; iter < count; iter++)
            {
                if (inner.Token.IsCancellationRequested || context.Aborted) break;
                _logger.LogDebug("Repeat iteration {Iter}/{Count}", iter + 1, count);

                // Reset bag to pre-Repeat snapshot at the start of every iteration except the
                // first — the first iteration runs against the original bag verbatim.
                if (iter > 0)
                {
                    context.Bag.Clear();
                    foreach (var kv in bagSnapshot) context.Bag[kv.Key] = kv.Value;
                }

                for (var i = repeatIndex + 1; i < endIndex; i++)
                {
                    if (inner.Token.IsCancellationRequested || context.Aborted) break;

                    var bodyStep = profile.Steps[i];
                    if (!bodyStep.Enabled) continue;

                    // Defensive: a nested Repeat inside a Repeat body is unsupported (would
                    // need its own indent + scope tracking). Log and skip — the visual editor
                    // also discourages this via indent depth.
                    if (string.Equals(bodyStep.TaskId, RepeatTaskId, StringComparison.Ordinal))
                    {
                        _logger.LogWarning("Repeat: nested Repeat tasks are not supported — skipping inner Repeat at step {Index}", i + 1);
                        continue;
                    }

                    var bodyTask = _registry!.Resolve(bodyStep.TaskId);
                    if (bodyTask is null)
                    {
                        _logger.LogWarning("Repeat body: task {TaskId} not registered, skipping", bodyStep.TaskId);
                        if (bodyStep.AbortOnError) context.Abort($"task {bodyStep.TaskId} not registered");
                        continue;
                    }

                    ran++;
                    try
                    {
                        await bodyTask.ExecuteAsync(context, bodyStep.Config, inner.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (inner.Token.IsCancellationRequested)
                    {
                        // Cancellation breaks the loop, not an error
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Repeat body step {TaskId} threw on iteration {Iter}", bodyTask.Id, iter + 1);
                        if (bodyStep.AbortOnError) context.Abort($"task {bodyTask.Id} threw: {ex.Message}");
                    }
                }

                if (iter < count - 1 && delayMs > 0)
                {
                    try { await Task.Delay(delayMs, inner.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
            }
        }
        finally
        {
            hotkeyToken?.Dispose();
        }
        // Resume the outer executor loop AFTER the End Repeat marker (or past end-of-steps when
        // no End was found — in which case endIndex == Steps.Count and the outer loop exits).
        return (ran, endIndex + 1);
    }
}
