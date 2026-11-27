using AresToys.Core.Pipeline;
using AresToys.Pipeline.Registry;
using Xunit;

namespace AresToys.Pipeline.Tests;

public class PipelineExecutorTests
{
    [Fact]
    public async Task RunAsync_WithNoTasks_CompletesSuccessfully()
    {
        var executor = new PipelineExecutor();
        var ctx = new PipelineContext(EmptyServices.Instance);

        await executor.RunAsync(tasks: [], ctx, CancellationToken.None);

        Assert.False(ctx.Aborted);
    }

    [Fact]
    public async Task RunAsync_InvokesEachTaskInOrder()
    {
        var calls = new List<string>();
        IPipelineTask a = new RecordingTask("a", calls);
        IPipelineTask b = new RecordingTask("b", calls);
        IPipelineTask c = new RecordingTask("c", calls);

        var executor = new PipelineExecutor();
        var ctx = new PipelineContext(EmptyServices.Instance);

        await executor.RunAsync(tasks: [a, b, c], ctx, CancellationToken.None);

        Assert.Equal(new[] { "a", "b", "c" }, calls);
    }

    [Fact]
    public async Task RunAsync_StopsAfterAbort()
    {
        var calls = new List<string>();
        IPipelineTask first = new RecordingTask("first", calls);
        IPipelineTask aborter = new AbortingTask("aborter");
        IPipelineTask never = new RecordingTask("never", calls);

        var executor = new PipelineExecutor();
        var ctx = new PipelineContext(EmptyServices.Instance);

        await executor.RunAsync(tasks: [first, aborter, never], ctx, CancellationToken.None);

        Assert.Equal(new[] { "first" }, calls);
        Assert.True(ctx.Aborted);
        Assert.Equal("intentional abort", ctx.AbortReason);
    }

    private sealed class RecordingTask(string id, List<string> calls) : IPipelineTask
    {
        public string Id => id;
        public string DisplayName => id;
        public PipelineTaskKind Kind => PipelineTaskKind.Both;
        public Task ExecuteAsync(PipelineContext context, System.Text.Json.Nodes.JsonNode? config, CancellationToken cancellationToken)
        {
            calls.Add(id);
            return Task.CompletedTask;
        }
    }

    private sealed class AbortingTask(string id) : IPipelineTask
    {
        public string Id => id;
        public string DisplayName => id;
        public PipelineTaskKind Kind => PipelineTaskKind.Both;
        public Task ExecuteAsync(PipelineContext context, System.Text.Json.Nodes.JsonNode? config, CancellationToken cancellationToken)
        {
            context.Abort("intentional abort");
            return Task.CompletedTask;
        }
    }

    private sealed class EmptyServices : IServiceProvider
    {
        public static readonly EmptyServices Instance = new();
        public object? GetService(Type serviceType) => null;
    }

    private sealed class ThrowingTask(string id) : IPipelineTask
    {
        public string Id => id;
        public string DisplayName => id;
        public PipelineTaskKind Kind => PipelineTaskKind.Both;
        public Task ExecuteAsync(PipelineContext context, System.Text.Json.Nodes.JsonNode? config, CancellationToken cancellationToken)
            => throw new InvalidOperationException("intentional");
    }

    [Fact]
    public async Task RunAsync_ProfileWithEnabledSteps_RunsThemInOrder()
    {
        var calls = new List<string>();
        var registry = new PipelineTaskRegistry(new IPipelineTask[]
        {
            new RecordingTask("a", calls),
            new RecordingTask("b", calls),
            new RecordingTask("c", calls)
        });
        var executor = new PipelineExecutor(registry, Microsoft.Extensions.Logging.Abstractions.NullLogger<PipelineExecutor>.Instance);
        var ctx = new PipelineContext(EmptyServices.Instance);
        var profile = new PipelineProfile(
            Id: "p",
            DisplayName: "P",
            Trigger: "test",
            Steps: new[]
            {
                new PipelineStep("a"),
                new PipelineStep("b"),
                new PipelineStep("c")
            });

        await executor.RunAsync(profile, ctx, CancellationToken.None);

        Assert.Equal(new[] { "a", "b", "c" }, calls);
    }

    [Fact]
    public async Task RunAsync_ProfileWithDisabledStep_SkipsIt()
    {
        var calls = new List<string>();
        var registry = new PipelineTaskRegistry(new IPipelineTask[]
        {
            new RecordingTask("a", calls),
            new RecordingTask("b", calls)
        });
        var executor = new PipelineExecutor(registry, Microsoft.Extensions.Logging.Abstractions.NullLogger<PipelineExecutor>.Instance);
        var ctx = new PipelineContext(EmptyServices.Instance);
        var profile = new PipelineProfile(
            "p", "P", "test",
            new[]
            {
                new PipelineStep("a", Enabled: false),
                new PipelineStep("b")
            });

        await executor.RunAsync(profile, ctx, CancellationToken.None);

        Assert.Equal(new[] { "b" }, calls);
    }

    [Fact]
    public async Task RunAsync_ProfileWithMissingTask_LogsAndContinues()
    {
        var calls = new List<string>();
        var registry = new PipelineTaskRegistry(new IPipelineTask[]
        {
            new RecordingTask("a", calls)
        });
        var executor = new PipelineExecutor(registry, Microsoft.Extensions.Logging.Abstractions.NullLogger<PipelineExecutor>.Instance);
        var ctx = new PipelineContext(EmptyServices.Instance);
        var profile = new PipelineProfile(
            "p", "P", "test",
            new[]
            {
                new PipelineStep("a"),
                new PipelineStep("missing"),
                new PipelineStep("a")
            });

        await executor.RunAsync(profile, ctx, CancellationToken.None);

        Assert.Equal(new[] { "a", "a" }, calls);
        Assert.False(ctx.Aborted);
    }

    [Fact]
    public async Task RunAsync_ProfileWithMissingTaskAndAbortOnError_AbortsContext()
    {
        var registry = new PipelineTaskRegistry([]);
        var executor = new PipelineExecutor(registry, Microsoft.Extensions.Logging.Abstractions.NullLogger<PipelineExecutor>.Instance);
        var ctx = new PipelineContext(EmptyServices.Instance);
        var profile = new PipelineProfile(
            "p", "P", "test",
            new[] { new PipelineStep("missing", AbortOnError: true) });

        await executor.RunAsync(profile, ctx, CancellationToken.None);

        Assert.True(ctx.Aborted);
        Assert.Contains("missing", ctx.AbortReason);
    }

    [Fact]
    public async Task RunAsync_ProfileTaskThrows_LogsAndContinuesByDefault()
    {
        var registry = new PipelineTaskRegistry(new IPipelineTask[] { new ThrowingTask("boom") });
        var executor = new PipelineExecutor(registry, Microsoft.Extensions.Logging.Abstractions.NullLogger<PipelineExecutor>.Instance);
        var ctx = new PipelineContext(EmptyServices.Instance);
        var profile = new PipelineProfile(
            "p", "P", "test",
            new[] { new PipelineStep("boom") });

        await executor.RunAsync(profile, ctx, CancellationToken.None);

        Assert.False(ctx.Aborted);
    }

    [Theory]
    [InlineData(0, 1)] // a tap: released before the second pass
    [InlineData(3, 4)] // held for three checks
    public async Task Repeat_WhileHeld_RunsUntilTheKeyIsReleased(int heldChecks, int expectedRuns)
    {
        var calls = new List<string>();
        var keyHold = new FakeKeyHold(heldChecks);
        var ctx = RepeatContext(keyHold);

        await RepeatExecutor(calls).RunAsync(RepeatProfile(count: 2), ctx, CancellationToken.None);

        Assert.Equal(expectedRuns, calls.Count(c => c == "body"));
        Assert.Equal(new[] { "after" }, calls.Where(c => c != "body"));
    }

    [Fact]
    public async Task Repeat_WhileHeld_WithoutHotkey_RunsOnce()
    {
        var calls = new List<string>();
        var ctx = RepeatContext(new FakeKeyHold(heldChecks: 5, hasHotkey: false));

        await RepeatExecutor(calls).RunAsync(RepeatProfile(count: 7), ctx, CancellationToken.None);

        Assert.Equal(1, calls.Count(c => c == "body"));
    }

    private static PipelineExecutor RepeatExecutor(List<string> calls) => new(
        new PipelineTaskRegistry(new IPipelineTask[] { new RecordingTask("body", calls), new RecordingTask("after", calls) }),
        Microsoft.Extensions.Logging.Abstractions.NullLogger<PipelineExecutor>.Instance);

    private static PipelineContext RepeatContext(FakeKeyHold keyHold)
    {
        var ctx = new PipelineContext(new SingleService(typeof(IWorkflowKeyHold), keyHold));
        ctx.Bag[PipelineBagKeys.WorkflowId] = "wf";
        return ctx;
    }

    private static PipelineProfile RepeatProfile(int count) => new(
        "p", "P", "test",
        new[]
        {
            new PipelineStep("arestoys.repeat",
                Config: System.Text.Json.Nodes.JsonNode.Parse($"{{\"count\":{count},\"delayMs\":\"0\",\"whileHeld\":true}}")),
            new PipelineStep("body"),
            new PipelineStep("arestoys.end-repeat"),
            new PipelineStep("after"),
        });

    private sealed class FakeKeyHold(int heldChecks, bool hasHotkey = true) : IWorkflowKeyHold
    {
        private int _left = heldChecks;

        public Task<Func<bool>?> GetProbeAsync(string workflowId, CancellationToken cancellationToken)
            => Task.FromResult<Func<bool>?>(hasHotkey ? () => _left-- > 0 : null);
    }

    private sealed class SingleService(Type type, object instance) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == type ? instance : null;
    }

    [Fact]
    public async Task RunAsync_ProfileTaskThrowsWithAbortOnError_AbortsContext()
    {
        var registry = new PipelineTaskRegistry(new IPipelineTask[] { new ThrowingTask("boom") });
        var executor = new PipelineExecutor(registry, Microsoft.Extensions.Logging.Abstractions.NullLogger<PipelineExecutor>.Instance);
        var ctx = new PipelineContext(EmptyServices.Instance);
        var profile = new PipelineProfile(
            "p", "P", "test",
            new[] { new PipelineStep("boom", AbortOnError: true) });

        await executor.RunAsync(profile, ctx, CancellationToken.None);

        Assert.True(ctx.Aborted);
        Assert.Contains("boom", ctx.AbortReason);
    }
}
