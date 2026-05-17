using AresToys.App.Services.KeySequences;
using Xunit;

namespace AresToys.App.Tests.KeySequences;

public class SequenceMatcherTests
{
    [Fact]
    public void Match_EmptyBuffer_ReturnsEmpty()
    {
        var m = new SequenceMatcher();
        Assert.Empty(m.Match(""));
    }

    [Fact]
    public void Match_NullBuffer_ReturnsEmpty()
    {
        var m = new SequenceMatcher();
        Assert.Empty(m.Match(null!));
    }

    [Fact]
    public void Match_UninitialisedMatcher_ReturnsEmpty()
    {
        var m = new SequenceMatcher();
        Assert.Empty(m.Match("anything"));
    }

    [Fact]
    public void Match_AfterRebuild_ExactMatchReturnsBinding()
    {
        var m = new SequenceMatcher();
        var binding = new SequenceBinding("mpg", new ReplaceWithItem(1));
        m.Rebuild(new[] { binding });

        var matches = m.Match("mpg");
        Assert.Single(matches);
        Assert.Same(binding, matches[0]);
    }

    [Fact]
    public void Match_IsCaseSensitive()
    {
        var m = new SequenceMatcher();
        m.Rebuild(new[] { new SequenceBinding("mpg", new ReplaceWithItem(1)) });

        Assert.Empty(m.Match("Mpg"));
        Assert.Empty(m.Match("MPG"));
    }

    [Fact]
    public void Match_RequiresExactMatch_NotPrefix()
    {
        var m = new SequenceMatcher();
        m.Rebuild(new[] { new SequenceBinding("mpg", new ReplaceWithItem(1)) });

        Assert.Empty(m.Match("mp"));
        Assert.Empty(m.Match("mpga"));
    }

    [Fact]
    public void Match_MultipleBindingsForSameSequence_ReturnsAll()
    {
        var m = new SequenceMatcher();
        var b1 = new SequenceBinding("mail", new ReplaceWithItem(1));
        var b2 = new SequenceBinding("mail", new ReplaceWithItem(2));
        m.Rebuild(new[] { b1, b2 });

        var matches = m.Match("mail");
        Assert.Equal(2, matches.Count);
        Assert.Contains(b1, matches);
        Assert.Contains(b2, matches);
    }

    [Fact]
    public void HasMatch_MirrorsMatchCountGreaterThanZero()
    {
        var m = new SequenceMatcher();
        Assert.False(m.HasMatch("mail"));

        m.Rebuild(new[] { new SequenceBinding("mail", new ReplaceWithItem(1)) });
        Assert.True(m.HasMatch("mail"));
        Assert.False(m.HasMatch("nope"));
        Assert.False(m.HasMatch(""));
        Assert.False(m.HasMatch(null!));
    }

    [Fact]
    public void BindingCount_ReflectsTotalAcrossSequences()
    {
        var m = new SequenceMatcher();
        Assert.Equal(0, m.BindingCount);

        m.Rebuild(new[]
        {
            new SequenceBinding("a", new ReplaceWithItem(1)),
            new SequenceBinding("a", new ReplaceWithItem(2)),
            new SequenceBinding("b", new ReplaceWithItem(3)),
            new SequenceBinding("c", new RunWorkflow("wf")),
        });
        Assert.Equal(4, m.BindingCount);
    }

    [Fact]
    public void Rebuild_NullBindings_Throws()
    {
        var m = new SequenceMatcher();
        Assert.Throws<ArgumentNullException>(() => m.Rebuild(null!));
    }

    [Fact]
    public void Rebuild_ReplacesPreviousIndex()
    {
        var m = new SequenceMatcher();
        m.Rebuild(new[] { new SequenceBinding("old", new ReplaceWithItem(1)) });
        Assert.True(m.HasMatch("old"));

        m.Rebuild(new[] { new SequenceBinding("new", new ReplaceWithItem(2)) });
        Assert.False(m.HasMatch("old"));
        Assert.True(m.HasMatch("new"));
    }

    [Fact]
    public async Task Rebuild_AtomicSwap_NoTornReadsUnderConcurrentAccess()
    {
        // Atomicity guarantee: a single Match() call always returns a self-consistent snapshot —
        // either the bindings from the just-replaced index or the previous one, never a partial
        // or empty intermediate state. We alternate between two non-overlapping binding sets on a
        // writer thread (each set has the SAME key "X" but different binding instances so we can
        // tell them apart) while a reader thread asks for Match("X") in a tight loop. The matcher
        // must always return exactly one binding (never zero, never two, never throw).
        // Per-call atomicity is what we're testing — not coherence across multiple calls (the
        // index can legitimately swap between consecutive calls).
        var m = new SequenceMatcher();
        var bindingA = new SequenceBinding("X", new ReplaceWithItem(1));
        var bindingB = new SequenceBinding("X", new ReplaceWithItem(2));
        var snapshotA = new[] { bindingA };
        var snapshotB = new[] { bindingB };
        m.Rebuild(snapshotA);

        using var cts = new CancellationTokenSource();
        Exception? failure = null;

        var writer = Task.Run(() =>
        {
            try
            {
                var toggle = false;
                for (var i = 0; i < 1000 && !cts.IsCancellationRequested; i++)
                {
                    m.Rebuild(toggle ? snapshotA : snapshotB);
                    toggle = !toggle;
                }
            }
            catch (Exception ex) { failure = ex; }
        });

        var reader = Task.Run(() =>
        {
            try
            {
                for (var i = 0; i < 1000 && !cts.IsCancellationRequested; i++)
                {
                    var matches = m.Match("X");
                    // Each Match call must return exactly one binding — either A or B, never both
                    // (we never registered two bindings for "X" in the same Rebuild) and never empty
                    // (a Rebuild always either has "X" or — if torn — would give us 0 or >1).
                    var single = Assert.Single(matches);
                    Assert.True(ReferenceEquals(single, bindingA) || ReferenceEquals(single, bindingB),
                        "Match returned a binding that was never in any snapshot.");
                }
            }
            catch (Exception ex) { failure = ex; }
        });

        await Task.WhenAll(writer, reader).WaitAsync(TimeSpan.FromSeconds(10));
        cts.Cancel();
        Assert.Null(failure);
    }
}
