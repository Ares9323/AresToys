using AresToys.App.Services.KeySequences;
using Xunit;

namespace AresToys.App.Tests.KeySequences;

public class SequenceBindingTests
{
    [Fact]
    public void Constructor_RejectsEmptySequence()
    {
        Assert.Throws<ArgumentException>(() => new SequenceBinding("", new ReplaceWithItem(1)));
    }

    [Fact]
    public void Constructor_RejectsSequenceLongerThanMaxLength()
    {
        var tooLong = new string('a', SequenceBinding.MaxLength + 1);
        Assert.Throws<ArgumentException>(() => new SequenceBinding(tooLong, new ReplaceWithItem(1)));
    }

    [Theory]
    [InlineData("with.dot")]
    [InlineData("with-dash")]
    [InlineData("with space")]
    [InlineData("è")]
    [InlineData("café")]
    [InlineData("mpg@")] // @ is NOT in [a-zA-Z0-9_]
    public void Constructor_RejectsInvalidChars(string sequence)
    {
        Assert.Throws<ArgumentException>(() => new SequenceBinding(sequence, new ReplaceWithItem(1)));
    }

    [Theory]
    [InlineData("mpg")]
    [InlineData("mail_gmail")]
    [InlineData("insta")]
    [InlineData("gh42")]
    [InlineData("A")]
    [InlineData("_underscore_")]
    [InlineData("123")]
    public void Constructor_AcceptsValidSequences(string sequence)
    {
        var b = new SequenceBinding(sequence, new ReplaceWithItem(1));
        Assert.Equal(sequence, b.Sequence);
    }

    [Fact]
    public void Constructor_AcceptsExactlyMaxLength()
    {
        var atLimit = new string('a', SequenceBinding.MaxLength);
        var b = new SequenceBinding(atLimit, new ReplaceWithItem(1));
        Assert.Equal(atLimit, b.Sequence);
    }

    [Fact]
    public void RunWorkflow_RejectsNullWorkflowId()
    {
        Assert.Throws<ArgumentException>(() => new RunWorkflow(null!));
    }

    [Fact]
    public void RunWorkflow_RejectsEmptyWorkflowId()
    {
        Assert.Throws<ArgumentException>(() => new RunWorkflow(""));
    }

    [Fact]
    public void RecordEquality_TwoBindingsWithSameSequenceAndTarget_AreEqual()
    {
        var a = new SequenceBinding("mail", new ReplaceWithItem(7));
        var b = new SequenceBinding("mail", new ReplaceWithItem(7));
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void RecordEquality_DifferentSequence_NotEqual()
    {
        var a = new SequenceBinding("mail", new ReplaceWithItem(7));
        var b = new SequenceBinding("mpg", new ReplaceWithItem(7));
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void RecordEquality_DifferentTarget_NotEqual()
    {
        var a = new SequenceBinding("mail", new ReplaceWithItem(7));
        var b = new SequenceBinding("mail", new ReplaceWithItem(8));
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void RecordEquality_DifferentTargetKind_NotEqual()
    {
        var a = new SequenceBinding("mail", new ReplaceWithItem(7));
        var b = new SequenceBinding("mail", new RunWorkflow("wf-7"));
        Assert.NotEqual(a, b);
    }
}
