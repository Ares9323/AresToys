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
