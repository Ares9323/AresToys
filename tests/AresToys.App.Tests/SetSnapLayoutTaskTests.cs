using AresToys.App.Services.PipelineTasks;
using Xunit;

namespace AresToys.App.Tests;

public class SetSnapLayoutTaskTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("cursor")]
    public void EmptyOrCursor_IsCursor(string? value)
    {
        Assert.Equal(SetSnapLayoutTask.MonitorKind.Cursor, SetSnapLayoutTask.ParseMonitor(value, 3).Kind);
    }

    [Fact]
    public void Primary_IsPrimary()
    {
        Assert.Equal(SetSnapLayoutTask.MonitorKind.Primary, SetSnapLayoutTask.ParseMonitor("primary", 3).Kind);
    }

    [Fact]
    public void NumberInRange_IsZeroBasedIndex()
    {
        var selector = SetSnapLayoutTask.ParseMonitor("2", 3);

        Assert.Equal(SetSnapLayoutTask.MonitorKind.Number, selector.Kind);
        Assert.Equal(1, selector.Index);
    }

    [Theory]
    [InlineData("5")]
    [InlineData("0")]
    [InlineData("abc")]
    public void OutOfRangeOrGarbage_IsInvalid(string value)
    {
        Assert.Equal(SetSnapLayoutTask.MonitorKind.Invalid, SetSnapLayoutTask.ParseMonitor(value, 3).Kind);
    }
}
