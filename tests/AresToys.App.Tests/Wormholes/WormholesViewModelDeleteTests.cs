using System.Linq;
using AresToys.App.Services.Wormholes;
using AresToys.App.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AresToys.App.Tests.Wormholes;

/// <summary>Keeps the Settings → Wormholes grid honest about deletions that happen elsewhere.
/// Deleting a wormhole from its own chrome menu used to leave its row sitting in an open Settings
/// window until the user re-clicked the sidebar entry: the manager removed the record without
/// telling anyone, and the grid only ever removed rows from inside its own Delete command.</summary>
public sealed class WormholesViewModelDeleteTests
{
    private static (WormholesViewModel vm, FakeWormholeStore store, FakeWormholeWindowManager manager)
        Build(params string[] titles)
    {
        var store = new FakeWormholeStore();
        foreach (var t in titles) store.Records.Add(new WormholeRecord { Title = t });
        var manager = new FakeWormholeWindowManager();
        var defaults = new WormholeDefaultsService(new FakeSettingsStore(),
            NullLogger<WormholeDefaultsService>.Instance);
        return (new WormholesViewModel(store, manager, defaults), store, manager);
    }

    [Fact]
    public async Task DeletingFromTheWormholeChromeDropsTheRowFromTheOpenSettingsGrid()
    {
        var (vm, store, manager) = Build("Keep me", "Delete me");
        await vm.ReloadAsync();
        var doomed = store.Records.Single(r => r.Title == "Delete me").Id;
        Assert.Equal(2, vm.Rows.Count);

        // What the chrome's "Delete wormhole…" ends up doing, via the manager.
        await manager.DeleteAsync(doomed, CancellationToken.None);

        Assert.Single(vm.Rows);
        Assert.DoesNotContain(vm.Rows, r => r.Id == doomed);
        Assert.Equal("Keep me", vm.Rows[0].Record.Title);
    }

    [Fact]
    public async Task TheGridFallsBackToItsEmptyStateAfterTheLastWormholeGoes()
    {
        var (vm, store, manager) = Build("Only one");
        await vm.ReloadAsync();
        Assert.False(vm.IsEmpty);

        await manager.DeleteAsync(store.Records[0].Id, CancellationToken.None);

        Assert.Empty(vm.Rows);
        Assert.True(vm.IsEmpty);
    }

    [Fact]
    public async Task ADeletionWeHaveNoRowForIsIgnored()
    {
        var (vm, _, manager) = Build("Untouched");
        await vm.ReloadAsync();

        await manager.DeleteAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Single(vm.Rows);
        Assert.False(vm.IsEmpty);
    }

    [Fact]
    public async Task RowsStillRefreshInPlaceOnAnOrdinaryRecordChange()
    {
        // Guard against "fixed the delete, broke the live X/Y/W/H refresh": a change must update
        // the row, not remove it.
        var (vm, store, manager) = Build("Dragged");
        await vm.ReloadAsync();

        manager.RaiseRecordChanged(store.Records[0].Id);

        Assert.Single(vm.Rows);
    }
}
