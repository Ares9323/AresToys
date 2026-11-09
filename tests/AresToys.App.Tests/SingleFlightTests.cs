using AresToys.App.Services.Recording;
using Xunit;

namespace AresToys.App.Tests;

public class SingleFlightTests
{
    [Fact]
    public async Task CallsDuringARun_JoinTheRunInsteadOfStartingAnother()
    {
        var gate = new SingleFlight();
        var release = new TaskCompletionSource();
        var runs = 0;
        Func<Task> work = async () => { Interlocked.Increment(ref runs); await release.Task; };

        var first = gate.RunAsync(work);
        var second = gate.RunAsync(work);
        var third = gate.RunAsync(work);

        Assert.Same(first, second);
        Assert.Same(first, third);
        Assert.True(gate.IsRunning);
        release.SetResult();
        await Task.WhenAll(first, second, third);
        Assert.Equal(1, runs);
        Assert.False(gate.IsRunning);
    }

    [Fact]
    public async Task AfterCompletion_NextCallStartsAFreshRun()
    {
        var gate = new SingleFlight();
        var runs = 0;
        Func<Task> work = () => { runs++; return Task.CompletedTask; };

        await gate.RunAsync(work);
        await gate.RunAsync(work);

        Assert.Equal(2, runs);
    }

    [Fact]
    public async Task FailedRun_PropagatesToEveryJoinerAndReleasesTheGate()
    {
        var gate = new SingleFlight();
        var release = new TaskCompletionSource();
        Func<Task> failing = async () => { await release.Task; throw new InvalidOperationException("boom"); };

        var first = gate.RunAsync(failing);
        var joined = gate.RunAsync(failing);
        release.SetResult();

        await Assert.ThrowsAsync<InvalidOperationException>(() => first);
        await Assert.ThrowsAsync<InvalidOperationException>(() => joined);
        var ran = false;
        await gate.RunAsync(() => { ran = true; return Task.CompletedTask; });
        Assert.True(ran);
    }
}
