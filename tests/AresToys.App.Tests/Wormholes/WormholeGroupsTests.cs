using System.Linq;
using AresToys.App.Services.Wormholes;
using Xunit;

namespace AresToys.App.Tests.Wormholes;

/// <summary>Membership rules for wormhole tab groups: merging one wormhole onto another, pulling a
/// tab back out, and what happens to a group as it empties. Pure bookkeeping over ids, kept apart
/// from the window manager so the awkward cases (detaching the parent, a group down to one member,
/// a wormhole deleted while grouped) are pinned without a visual tree.</summary>
public sealed class WormholeGroupsTests
{
    private static readonly Guid A = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid B = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid C = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid D = Guid.Parse("44444444-4444-4444-4444-444444444444");

    [Fact]
    public void DroppingOneWormholeOntoAnotherFormsAGroupOfTwo()
    {
        var groups = new WormholeGroups();

        var group = groups.Merge(dragged: B, target: A);

        Assert.NotNull(group);
        Assert.Equal([A, B], group!.Members);
        // The one that was already there keeps its shape: the group lives at the target's geometry.
        Assert.Equal(A, group.ParentId);
        // The tab you just dropped is the one you want to look at.
        Assert.Equal(B, group.ActiveId);
        Assert.True(groups.IsGrouped(A));
        Assert.True(groups.IsGrouped(B));
    }

    [Fact]
    public void DroppingOntoAWormholeThatIsAlreadyAGroupJoinsIt()
    {
        var groups = new WormholeGroups();
        groups.Merge(dragged: B, target: A);

        var group = groups.Merge(dragged: C, target: B);   // dropped on a tab of the existing group

        Assert.NotNull(group);
        Assert.Equal([A, B, C], group!.Members);
        Assert.Equal(A, group.ParentId);      // unchanged: the group keeps the shape it had
        Assert.Equal(C, group.ActiveId);
        Assert.Single(groups.All);            // one group, not two
    }

    [Fact]
    public void MergingAWormholeOntoItselfDoesNothing()
    {
        var groups = new WormholeGroups();

        Assert.Null(groups.Merge(dragged: A, target: A));
        Assert.Empty(groups.All);
    }

    [Fact]
    public void MergingATabOntoItsOwnGroupChangesNothing()
    {
        var groups = new WormholeGroups();
        groups.Merge(dragged: B, target: A);

        var again = groups.Merge(dragged: B, target: A);

        Assert.NotNull(again);
        Assert.Equal([A, B], again!.Members);   // no duplicate entry
        Assert.Single(groups.All);
    }

    [Fact]
    public void DraggingAGroupOntoAnotherMergesEveryTabIntoIt()
    {
        var groups = new WormholeGroups();
        groups.Merge(dragged: B, target: A);   // group 1: A, B
        groups.Merge(dragged: D, target: C);   // group 2: C, D

        var merged = groups.Merge(dragged: C, target: A);

        Assert.NotNull(merged);
        Assert.Equal([A, B, C, D], merged!.Members);
        Assert.Single(groups.All);             // the emptied group is gone, not left behind
        Assert.Equal(A, merged.ParentId);
    }

    [Fact]
    public void PullingATabOutLeavesItOnItsOwn()
    {
        var groups = new WormholeGroups();
        groups.Merge(dragged: B, target: A);
        groups.Merge(dragged: C, target: A);

        groups.Detach(C);

        Assert.False(groups.IsGrouped(C));
        Assert.Equal([A, B], groups.All.Single().Members);
    }

    [Fact]
    public void AGroupDownToOneTabStopsBeingAGroup()
    {
        var groups = new WormholeGroups();
        groups.Merge(dragged: B, target: A);

        groups.Detach(B);

        // A single tab is just a wormhole again, so nothing is left describing a group.
        Assert.Empty(groups.All);
        Assert.False(groups.IsGrouped(A));
        Assert.False(groups.IsGrouped(B));
    }

    [Fact]
    public void DetachingTheParentHandsTheGroupToWhoeverIsLeft()
    {
        var groups = new WormholeGroups();
        groups.Merge(dragged: B, target: A);
        groups.Merge(dragged: C, target: A);   // parent is A

        groups.Detach(A);

        var group = groups.All.Single();
        Assert.Equal([B, C], group.Members);
        Assert.Equal(B, group.ParentId);       // the group needs a shape to live at
        Assert.False(groups.IsGrouped(A));
    }

    [Fact]
    public void DetachingTheActiveTabPromotesAnotherOne()
    {
        var groups = new WormholeGroups();
        groups.Merge(dragged: B, target: A);
        groups.Merge(dragged: C, target: A);   // active is C

        groups.Detach(C);

        var group = groups.All.Single();
        Assert.Contains(group.ActiveId, group.Members);
    }

    [Fact]
    public void DeletingAWormholeRemovesItFromItsGroup()
    {
        var groups = new WormholeGroups();
        groups.Merge(dragged: B, target: A);
        groups.Merge(dragged: C, target: A);

        groups.Forget(B);

        Assert.Equal([A, C], groups.All.Single().Members);
        Assert.False(groups.IsGrouped(B));
    }

    [Fact]
    public void TheActiveTabIsWhicheverWasClickedLast()
    {
        var groups = new WormholeGroups();
        groups.Merge(dragged: B, target: A);

        groups.SetActive(A);

        Assert.Equal(A, groups.All.Single().ActiveId);
    }

    [Fact]
    public void ActivatingSomethingOutsideTheGroupIsIgnored()
    {
        var groups = new WormholeGroups();
        groups.Merge(dragged: B, target: A);

        groups.SetActive(D);

        Assert.Equal(B, groups.All.Single().ActiveId);
    }

    [Fact]
    public void AWormholeCanBeLookedUpByTheGroupItBelongsTo()
    {
        var groups = new WormholeGroups();
        groups.Merge(dragged: B, target: A);

        Assert.Equal(groups.All.Single(), groups.FindFor(B));
        Assert.Null(groups.FindFor(D));
    }

    [Fact]
    public void OnlyTheParentDrivesTheWindowItsTabsShare()
    {
        // The rule behind a bug that took wormholes off the desktop: restoring a layout preset
        // reconciles every record, and acting on a tab's own geometry moved the shared window to
        // whichever tab came last, while acting on a tab's Hidden flag closed a window the other
        // tabs were still living in — leaving records with no window, visible in Settings and
        // nowhere else.
        var groups = new WormholeGroups();

        Assert.True(groups.GovernsWindow(A));   // not grouped: it's its own window

        groups.Merge(dragged: B, target: A);

        Assert.True(groups.GovernsWindow(A));    // the parent
        Assert.False(groups.GovernsWindow(B));   // a tab of A's window
        Assert.True(groups.GovernsWindow(D));    // still ungrouped
    }

    [Fact]
    public void AfterTheParentLeavesTheNewParentDrivesTheWindow()
    {
        var groups = new WormholeGroups();
        groups.Merge(dragged: B, target: A);
        groups.Merge(dragged: C, target: A);

        groups.Detach(A);

        Assert.True(groups.GovernsWindow(A));    // on its own again
        Assert.True(groups.GovernsWindow(B));    // inherited the role
        Assert.False(groups.GovernsWindow(C));
    }

    [Fact]
    public void LoadingDropsGroupsWhoseWormholesNoLongerExist()
    {
        // groups.json is a separate file, so it can outlive the records it points at: a rollback
        // that deletes wormholes, or a hand-edited store. Anything dangling is pruned on load
        // rather than left to fail later.
        var groups = new WormholeGroups();
        groups.Merge(dragged: B, target: A);
        groups.Merge(dragged: C, target: A);

        groups.PruneTo([A, C]);   // B is gone from wormholes.json

        Assert.Equal([A, C], groups.All.Single().Members);

        groups.PruneTo([A]);      // now only A survives — no group left
        Assert.Empty(groups.All);
    }
}
