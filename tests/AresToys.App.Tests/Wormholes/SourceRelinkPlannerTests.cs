using AresToys.App.Services.Wormholes;
using Xunit;

namespace AresToys.App.Tests.Wormholes;

public sealed class SourceRelinkPlannerTests
{
    private static readonly Guid A = Guid.NewGuid();
    private static readonly Guid B = Guid.NewGuid();
    private static readonly Guid C = Guid.NewGuid();

    private static Func<string, bool> Existing(params string[] paths)
    {
        var set = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        return p => set.Contains(p.TrimEnd('\\'));
    }

    /// <summary>The case this was written for: every wormhole source lived under
    /// Documents\Fences and the user renamed that folder to Documents\Wormholes.</summary>
    [Fact]
    public void Plan_RemapsEveryWormholeWhenTheirSharedParentIsRenamed()
    {
        var missing = new[]
        {
            (A, @"C:\Users\Ares\Documents\Fences\Dev"),
            (B, @"C:\Users\Ares\Documents\Fences\Games"),
            (C, @"C:\Users\Ares\Documents\Fences\3D"),
        };
        var exists = Existing(
            @"C:\Users\Ares\Documents\Wormholes\Dev",
            @"C:\Users\Ares\Documents\Wormholes\Games",
            @"C:\Users\Ares\Documents\Wormholes\3D");

        var plan = SourceRelinkPlanner.Plan(missing, @"C:\Users\Ares\Documents\Wormholes", exists);

        Assert.Equal(3, plan.Count);
        Assert.Equal(@"C:\Users\Ares\Documents\Wormholes\Dev", plan.Single(p => p.Id == A).NewPath);
        Assert.Equal(@"C:\Users\Ares\Documents\Wormholes\Games", plan.Single(p => p.Id == B).NewPath);
    }

    [Fact]
    public void Plan_KeepsTheStructureBelowTheCommonParent()
    {
        var missing = new[]
        {
            (A, @"D:\Old\group1\Dev"),
            (B, @"D:\Old\group2\Games"),
        };
        var exists = Existing(@"E:\New\group1\Dev", @"E:\New\group2\Games");

        var plan = SourceRelinkPlanner.Plan(missing, @"E:\New", exists);

        Assert.Equal(@"E:\New\group1\Dev", plan.Single(p => p.Id == A).NewPath);
        Assert.Equal(@"E:\New\group2\Games", plan.Single(p => p.Id == B).NewPath);
    }

    [Fact]
    public void Plan_FallsBackToTheLeafNameWhenTheStructureIsGone()
    {
        var missing = new[] { (A, @"D:\Old\group1\Dev") };
        var exists = Existing(@"E:\New\Dev"); // flattened: only the leaf survived

        var plan = SourceRelinkPlanner.Plan(missing, @"E:\New", exists);

        Assert.Equal(@"E:\New\Dev", Assert.Single(plan).NewPath);
    }

    [Fact]
    public void Plan_SkipsEntriesWhoseTargetDoesNotExist()
    {
        var missing = new[]
        {
            (A, @"C:\Fences\Dev"),
            (B, @"C:\Fences\Deleted"),
        };
        var exists = Existing(@"C:\Wormholes\Dev");

        var plan = SourceRelinkPlanner.Plan(missing, @"C:\Wormholes", exists);

        Assert.Equal(A, Assert.Single(plan).Id);
    }

    [Fact]
    public void Plan_NeverProposesThePathItAlreadyHas()
    {
        var missing = new[] { (A, @"C:\Fences\Dev") };
        var exists = Existing(@"C:\Fences\Dev");

        Assert.Empty(SourceRelinkPlanner.Plan(missing, @"C:\Fences", exists));
    }

    [Fact]
    public void Plan_HandlesEmptyInput()
    {
        Assert.Empty(SourceRelinkPlanner.Plan([], @"C:\Wormholes", _ => true));
        Assert.Empty(SourceRelinkPlanner.Plan([(A, @"C:\Fences\Dev")], "", _ => true));
    }

    [Fact]
    public void Plan_WorksWhenThePathsShareNothingButTheDrive()
    {
        var missing = new[]
        {
            (A, @"C:\one\Dev"),
            (B, @"C:\two\Games"),
        };
        var exists = Existing(@"C:\New\one\Dev", @"C:\New\two\Games");

        var plan = SourceRelinkPlanner.Plan(missing, @"C:\New", exists);

        Assert.Equal(2, plan.Count);
    }

    // ── Suggest: single missing source, guided by the healthy ones ────────────────────────

    [Fact]
    public void Suggest_PointsAtTheParentSharedByTheHealthySources()
    {
        var healthy = new[]
        {
            @"C:\Users\Ares\Documents\Wormholes\Dev",
            @"C:\Users\Ares\Documents\Wormholes\Games",
        };
        var exists = Existing(@"C:\Users\Ares\Documents\Wormholes\Tools");

        var suggestion = SourceRelinkPlanner.Suggest(
            @"C:\Users\Ares\Documents\Fences\Tools", healthy, exists);

        Assert.Equal(@"C:\Users\Ares\Documents\Wormholes\Tools", suggestion);
    }

    [Fact]
    public void Suggest_PrefersTheParentMostOfTheHealthySourcesUse()
    {
        var healthy = new[] { @"C:\Many\Dev", @"C:\Many\Games", @"C:\Few\Other" };
        var exists = Existing(@"C:\Many\Tools", @"C:\Few\Tools");

        Assert.Equal(@"C:\Many\Tools", SourceRelinkPlanner.Suggest(@"C:\Gone\Tools", healthy, exists));
    }

    [Fact]
    public void Suggest_ReturnsNullWhenNothingPlausibleExists()
    {
        var healthy = new[] { @"C:\Wormholes\Dev" };
        Assert.Null(SourceRelinkPlanner.Suggest(@"C:\Fences\Tools", healthy, Existing()));
        Assert.Null(SourceRelinkPlanner.Suggest(@"C:\Fences\Tools", [], Existing(@"C:\Wormholes\Tools")));
        Assert.Null(SourceRelinkPlanner.Suggest("", healthy, Existing(@"C:\Wormholes\Tools")));
    }

    [Fact]
    public void CommonParent_IsTheDeepestSharedDirectory()
    {
        Assert.Equal(@"C:\a\b", SourceRelinkPlanner.CommonParent([@"C:\a\b\x", @"C:\a\b\y"]));
        Assert.Equal(@"C:\a", SourceRelinkPlanner.CommonParent([@"C:\a\b\x", @"C:\a\c\y"]));
        Assert.Equal(@"C:\", SourceRelinkPlanner.CommonParent([@"C:\a\x", @"C:\b\y"]));
    }
}
