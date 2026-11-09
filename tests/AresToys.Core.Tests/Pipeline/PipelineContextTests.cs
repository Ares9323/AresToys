using AresToys.Core.Pipeline;
using Xunit;

namespace AresToys.Core.Tests.Pipeline;

public class PipelineContextTests
{
    [Fact]
    public void NewContext_HasEmptyBag_AndIsNotAborted()
    {
        var ctx = new PipelineContext(services: EmptyServiceProvider.Instance);

        Assert.Empty(ctx.Bag);
        Assert.False(ctx.Aborted);
        Assert.Null(ctx.AbortReason);
    }

    [Fact]
    public void Abort_SetsAbortedAndCapturesReason()
    {
        var ctx = new PipelineContext(services: EmptyServiceProvider.Instance);

        ctx.Abort("upload failed");

        Assert.True(ctx.Aborted);
        Assert.Equal("upload failed", ctx.AbortReason);
    }

    [Fact]
    public void Abort_SecondCall_KeepsFirstReason()
    {
        var ctx = new PipelineContext(services: EmptyServiceProvider.Instance);

        ctx.Abort("first");
        ctx.Abort("second");

        Assert.Equal("first", ctx.AbortReason);
    }

    [Fact]
    public void Bag_StoresAndRetrievesArbitraryValues()
    {
        var ctx = new PipelineContext(services: EmptyServiceProvider.Instance);

        ctx.Bag["upload_url"] = "https://example.com/abc";
        ctx.Bag["count"] = 7;

        Assert.Equal("https://example.com/abc", ctx.Bag["upload_url"]);
        Assert.Equal(7, ctx.Bag["count"]);
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public static readonly EmptyServiceProvider Instance = new();
        public object? GetService(Type serviceType) => null;
    }
}
