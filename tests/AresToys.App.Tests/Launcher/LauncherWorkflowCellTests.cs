using System.Collections.Generic;
using System.Runtime.CompilerServices;
using AresToys.App.Services.Launcher;
using AresToys.Storage.Settings;
using Xunit;

namespace AresToys.App.Tests.Launcher;

/// <summary>In-memory settings store so <see cref="LauncherStore"/> can round-trip without
/// touching the real SQLite file.</summary>
internal sealed class InMemorySettingsStore : ISettingsStore
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public Task<string?> GetAsync(string key, CancellationToken cancellationToken) =>
        Task.FromResult(_values.TryGetValue(key, out var v) ? v : null);

    public Task SetAsync(string key, string value, bool sensitive, CancellationToken cancellationToken)
    {
        _values[key] = value;
        return Task.CompletedTask;
    }

    public Task<bool> RemoveAsync(string key, CancellationToken cancellationToken) =>
        Task.FromResult(_values.Remove(key));

    public async IAsyncEnumerable<SettingEntry> EnumerateAsync(
        bool includeSensitive = false,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var (k, v) in _values) yield return new SettingEntry(k, v, false);
        await Task.CompletedTask;
    }
}

/// <summary>A launcher cell can run a workflow instead of launching a path (the Workflow picker in
/// the cell's Advanced panel, same wiring as the tray's click actions). These cover the two places
/// that has to hold up: a workflow-only cell must count as configured — otherwise it never renders
/// and never fires — and the workflow id has to survive a store round-trip.</summary>
public sealed class LauncherWorkflowCellTests
{
    private static LauncherCell Cell(string path = "", string workflowId = "") =>
        new("1", "Q", "Label", path, string.Empty, WorkflowId: workflowId);

    [Fact]
    public void ACellWithOnlyAWorkflowCountsAsConfigured()
    {
        var cell = Cell(workflowId: "arestoys.capture-region");

        Assert.True(cell.IsConfigured);
        Assert.True(cell.HasWorkflow);
    }

    [Fact]
    public void ACellWithNeitherPathNorWorkflowIsStillEmpty()
    {
        var cell = Cell();

        Assert.False(cell.IsConfigured);
        Assert.False(cell.HasWorkflow);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankWorkflowIdsDoNotCountAsAWorkflow(string workflowId)
    {
        Assert.False(Cell(workflowId: workflowId).HasWorkflow);
    }

    [Fact]
    public void AnOrdinaryLaunchCellReportsNoWorkflow()
    {
        var cell = Cell(path: @"C:\Windows\notepad.exe");

        Assert.True(cell.IsConfigured);
        Assert.False(cell.HasWorkflow);
    }

    [Fact]
    public async Task TheWorkflowIdSurvivesASaveLoadRoundTrip()
    {
        var store = new LauncherStore(new InMemorySettingsStore());
        await store.UpdateCellAsync(Cell(workflowId: "my.custom.workflow"), CancellationToken.None);

        var reloaded = (await store.LoadAsync(CancellationToken.None)).Get("1", "Q");

        Assert.Equal("my.custom.workflow", reloaded.WorkflowId);
        Assert.True(reloaded.IsConfigured);
    }

    [Fact]
    public async Task AWorkflowOnlyCellIsPersistedAtAllDespiteHavingNoPath()
    {
        // SaveAsync only writes cells that report IsConfigured, so a workflow cell that didn't
        // would be silently dropped on the way to disk.
        var store = new LauncherStore(new InMemorySettingsStore());
        await store.UpdateCellAsync(Cell(workflowId: "w"), CancellationToken.None);

        var reloaded = (await store.LoadAsync(CancellationToken.None)).Get("1", "Q");

        Assert.Equal("Label", reloaded.Label);
        Assert.Equal(string.Empty, reloaded.Path);
    }

    [Fact]
    public async Task ClearingTheWorkflowRemovesItFromStorage()
    {
        var store = new LauncherStore(new InMemorySettingsStore());
        await store.UpdateCellAsync(Cell(workflowId: "w"), CancellationToken.None);

        await store.UpdateCellAsync(Cell(path: @"C:\Windows\notepad.exe"), CancellationToken.None);
        var reloaded = (await store.LoadAsync(CancellationToken.None)).Get("1", "Q");

        Assert.Equal(string.Empty, reloaded.WorkflowId);
        Assert.False(reloaded.HasWorkflow);
    }
}
