using AresToys.App.Services.KeySequences;
using AresToys.App.Tests.KeySequences.Fakes;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AresToys.App.Tests.KeySequences;

public class SequenceBindingStoreTests
{
    private static (SequenceBindingStore store, SequenceMatcher matcher, TestLogger<SequenceBindingStore> logger) BuildStore(
        params ISequenceBindingProvider[] providers)
    {
        var matcher = new SequenceMatcher();
        var logger = new TestLogger<SequenceBindingStore>();
        var store = new SequenceBindingStore(providers, matcher, logger);
        return (store, matcher, logger);
    }

    [Fact]
    public void SingleProviderSingleBinding_MatcherHasItAfterRebuild()
    {
        var p = new FakeProvider();
        var binding = new SequenceBinding("mail", new ReplaceWithItem(1));
        p.Bindings.Add(binding);

        var (store, matcher, _) = BuildStore(p);
        store.Rebuild();

        var matches = matcher.Match("mail");
        Assert.Single(matches);
        Assert.Same(binding, matches[0]);
    }

    [Fact]
    public void ProviderRaisesChanged_StoreCallsMatcherRebuild()
    {
        var p = new FakeProvider();
        var (store, matcher, _) = BuildStore(p);
        store.Rebuild();
        Assert.Equal(0, matcher.BindingCount);

        p.Bindings.Add(new SequenceBinding("insta", new RunWorkflow("wf-1")));
        p.RaiseChanged();

        Assert.Equal(1, matcher.BindingCount);
        Assert.True(matcher.HasMatch("insta"));
    }

    [Fact]
    public void TwoProvidersNoConflicts_MatcherAggregatesBoth()
    {
        var p1 = new FakeProvider();
        p1.Bindings.Add(new SequenceBinding("mail", new ReplaceWithItem(1)));
        var p2 = new FakeProvider();
        p2.Bindings.Add(new SequenceBinding("insta", new RunWorkflow("wf-1")));

        var (store, matcher, _) = BuildStore(p1, p2);
        store.Rebuild();

        Assert.Equal(2, matcher.BindingCount);
        Assert.True(matcher.HasMatch("mail"));
        Assert.True(matcher.HasMatch("insta"));
    }

    [Fact]
    public void TwoRunWorkflowBindingsOnSameSequence_OnlyFirstKept_AndWarningLogged()
    {
        var p = new FakeProvider();
        p.Bindings.Add(new SequenceBinding("gh", new RunWorkflow("wf-first")));
        p.Bindings.Add(new SequenceBinding("gh", new RunWorkflow("wf-second")));

        var (store, matcher, logger) = BuildStore(p);
        store.Rebuild();

        var matches = matcher.Match("gh");
        Assert.Single(matches);
        Assert.Equal(new RunWorkflow("wf-first"), matches[0].Target);
        Assert.True(logger.HasLevel(LogLevel.Warning), "Expected a warning about duplicate workflow binding.");
    }

    [Fact]
    public void ReplacerAndWorkflowOnSameSequence_ReplacerKept_WorkflowDropped()
    {
        var p = new FakeProvider();
        p.Bindings.Add(new SequenceBinding("mail", new ReplaceWithItem(7)));
        p.Bindings.Add(new SequenceBinding("mail", new RunWorkflow("wf-mail")));

        var (store, matcher, logger) = BuildStore(p);
        store.Rebuild();

        var matches = matcher.Match("mail");
        Assert.Single(matches);
        Assert.IsType<ReplaceWithItem>(matches[0].Target);
        Assert.True(logger.HasLevel(LogLevel.Warning), "Expected a warning about Replacer+Workflow collision.");
    }

    [Fact]
    public void MultipleReplacersOnSameSequence_AllKept()
    {
        var p = new FakeProvider();
        p.Bindings.Add(new SequenceBinding("mail", new ReplaceWithItem(1)));
        p.Bindings.Add(new SequenceBinding("mail", new ReplaceWithItem(2)));
        p.Bindings.Add(new SequenceBinding("mail", new ReplaceWithItem(3)));

        var (store, matcher, _) = BuildStore(p);
        store.Rebuild();

        var matches = matcher.Match("mail");
        Assert.Equal(3, matches.Count);
        Assert.All(matches, b => Assert.IsType<ReplaceWithItem>(b.Target));
    }

    [Fact]
    public void Dispose_UnsubscribesFromProviderBindingsChanged()
    {
        var p = new FakeProvider();
        var (store, matcher, _) = BuildStore(p);
        store.Rebuild();
        Assert.True(p.HasSubscribers);

        store.Dispose();

        Assert.False(p.HasSubscribers);

        // After disposal, a provider change should NOT mutate the matcher.
        p.Bindings.Add(new SequenceBinding("post", new ReplaceWithItem(1)));
        p.RaiseChanged();
        Assert.Equal(0, matcher.BindingCount);
    }
}
