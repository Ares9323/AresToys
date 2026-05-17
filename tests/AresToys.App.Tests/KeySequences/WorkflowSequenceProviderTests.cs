using AresToys.App.Services.KeySequences;
using AresToys.App.Tests.KeySequences.Fakes;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AresToys.App.Tests.KeySequences;

public class WorkflowSequenceProviderTests
{
    private static (WorkflowSequenceProvider provider, FakeSettingsStore settings, TestLogger<WorkflowSequenceProvider> logger) Build()
    {
        var settings = new FakeSettingsStore();
        var logger = new TestLogger<WorkflowSequenceProvider>();
        var provider = new WorkflowSequenceProvider(settings, logger);
        return (provider, settings, logger);
    }

    [Fact]
    public async Task LoadAsync_EmptyKey_SnapshotIsEmpty()
    {
        var (provider, _, _) = Build();
        await provider.LoadAsync(CancellationToken.None);
        Assert.Empty(provider.Snapshot());
    }

    [Fact]
    public async Task LoadAsync_MalformedJson_SnapshotIsEmpty_ErrorLogged()
    {
        var (provider, settings, logger) = Build();
        settings.Backing[WorkflowSequenceProvider.StorageKey] = "{ this is not json ]";

        await provider.LoadAsync(CancellationToken.None);

        Assert.Empty(provider.Snapshot());
        // The provider logs at Error level for corrupt JSON (per implementation).
        Assert.True(logger.HasLevel(LogLevel.Error), "Expected an error log for malformed JSON.");
    }

    [Fact]
    public async Task LoadAsync_ValidJson_SnapshotMatches()
    {
        var (provider, settings, _) = Build();
        settings.Backing[WorkflowSequenceProvider.StorageKey] =
            """[{"sequence":"insta","workflowId":"wf-1","label":"Open Instagram"},{"sequence":"gh","workflowId":"wf-2","label":"Open GitHub"}]""";

        await provider.LoadAsync(CancellationToken.None);

        var snap = provider.Snapshot();
        Assert.Equal(2, snap.Count);
        Assert.Equal("insta", snap[0].Sequence);
        Assert.Equal("wf-1", snap[0].WorkflowId);
        Assert.Equal("Open Instagram", snap[0].Label);
        Assert.Equal("gh", snap[1].Sequence);
        Assert.Equal("wf-2", snap[1].WorkflowId);
    }

    [Fact]
    public async Task ReplaceAsync_ValidEntries_PersistsAndUpdatesSnapshotAndFiresEvent()
    {
        var (provider, settings, _) = Build();
        var raised = 0;
        provider.BindingsChanged += (_, _) => raised++;

        var entries = new[]
        {
            new WorkflowTriggerEntry("insta", "wf-1", "Open Instagram"),
            new WorkflowTriggerEntry("gh", "wf-2", "Open GitHub"),
        };

        await provider.ReplaceAsync(entries, CancellationToken.None);

        Assert.Equal(1, raised);
        Assert.True(settings.Backing.ContainsKey(WorkflowSequenceProvider.StorageKey));
        Assert.Contains("insta", settings.Backing[WorkflowSequenceProvider.StorageKey]);
        Assert.Equal(2, provider.Snapshot().Count);
    }

    [Fact]
    public async Task ReplaceAsync_EntryWithEmptySequence_Throws_NoPersistence()
    {
        var (provider, settings, _) = Build();
        var entries = new[]
        {
            new WorkflowTriggerEntry("", "wf-1", "Open Instagram"),
        };

        await Assert.ThrowsAsync<ArgumentException>(
            () => provider.ReplaceAsync(entries, CancellationToken.None));

        Assert.False(settings.Backing.ContainsKey(WorkflowSequenceProvider.StorageKey));
        Assert.Empty(provider.Snapshot());
    }

    [Fact]
    public async Task ReplaceAsync_EntryWithEmptyWorkflowId_Throws()
    {
        var (provider, _, _) = Build();
        var entries = new[]
        {
            new WorkflowTriggerEntry("insta", "", "Open Instagram"),
        };

        await Assert.ThrowsAsync<ArgumentException>(
            () => provider.ReplaceAsync(entries, CancellationToken.None));
    }

    [Fact]
    public async Task GetBindings_AfterReplaceAsync_ReturnsRunWorkflowBindings()
    {
        var (provider, _, _) = Build();
        await provider.ReplaceAsync(new[]
        {
            new WorkflowTriggerEntry("insta", "wf-1", "Open Instagram"),
            new WorkflowTriggerEntry("gh", "wf-2", "Open GitHub"),
        }, CancellationToken.None);

        var bindings = provider.GetBindings();
        Assert.Equal(2, bindings.Count);

        var byseq = bindings.ToDictionary(b => b.Sequence);
        Assert.Equal(new RunWorkflow("wf-1"), byseq["insta"].Target);
        Assert.Equal(new RunWorkflow("wf-2"), byseq["gh"].Target);
    }

    [Fact]
    public async Task GetBindings_InvalidSequenceInStoredJson_SkipsRow_LogsWarning()
    {
        var (provider, settings, logger) = Build();
        // "mpg@" contains a char outside [a-zA-Z0-9_], so the SequenceBinding constructor will reject.
        // The provider must catch and log, not crash.
        settings.Backing[WorkflowSequenceProvider.StorageKey] =
            """[{"sequence":"mpg@","workflowId":"wf-1","label":"Bad"},{"sequence":"good","workflowId":"wf-2","label":"OK"}]""";

        await provider.LoadAsync(CancellationToken.None);

        var bindings = provider.GetBindings();
        Assert.Single(bindings);
        Assert.Equal("good", bindings[0].Sequence);
        Assert.True(logger.HasLevel(LogLevel.Warning), "Expected a warning for the invalid row.");
    }
}
