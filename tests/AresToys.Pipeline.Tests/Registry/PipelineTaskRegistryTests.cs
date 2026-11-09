using System.Text.Json.Nodes;
using AresToys.Core.Pipeline;
using AresToys.Pipeline.Registry;
using Xunit;

namespace AresToys.Pipeline.Tests.Registry;

public class PipelineTaskRegistryTests
{
    private sealed class StubTask(string id) : IPipelineTask
    {
        public string Id => id;
        public string DisplayName => id;
        public PipelineTaskKind Kind => PipelineTaskKind.Both;
        public Task ExecuteAsync(PipelineContext context, JsonNode? config, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    [Fact]
    public void Resolve_ReturnsRegisteredTask()
    {
        var registry = new PipelineTaskRegistry([new StubTask("a"), new StubTask("b")]);

        var resolved = registry.Resolve("a");

        Assert.NotNull(resolved);
        Assert.Equal("a", resolved!.Id);
    }

    [Fact]
    public void Resolve_OfUnknownId_ReturnsNull()
    {
        var registry = new PipelineTaskRegistry([new StubTask("a")]);

        Assert.Null(registry.Resolve("missing"));
    }

    [Fact]
    public void Constructor_OnDuplicateId_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new PipelineTaskRegistry([new StubTask("dup"), new StubTask("dup")]));
    }

    [Fact]
    public void All_ReturnsEverything()
    {
        var registry = new PipelineTaskRegistry([new StubTask("a"), new StubTask("b"), new StubTask("c")]);

        Assert.Equal(3, registry.All.Count);
    }
}
