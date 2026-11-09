using Microsoft.Extensions.Logging.Abstractions;
using AresToys.Core.Pipeline;
using AresToys.Pipeline.Profiles;
using AresToys.Pipeline.Tests.Fixtures;
using Xunit;

namespace AresToys.Pipeline.Tests.Profiles;

public class PipelineProfileSeederTests
{
    private static (PipelineProfileSeeder Seeder, IPipelineProfileStore Store) Build(TempPipelineDatabaseFixture fx)
    {
        var store = new SqlitePipelineProfileStore(fx.Database);
        var seeder = new PipelineProfileSeeder(store, NullLogger<PipelineProfileSeeder>.Instance);
        return (seeder, store);
    }

    [Fact]
    public async Task SeedAsync_OnEmptyStore_AddsAllDefaultProfiles()
    {
        await using var fx = await new TempPipelineDatabaseFixture().InitializeAsync();
        var (seeder, store) = Build(fx);

        await seeder.SeedAsync(CancellationToken.None);

        var list = await store.ListAsync(CancellationToken.None);
        Assert.Equal(DefaultPipelineProfiles.All.Count, list.Count);
        foreach (var defaultProfile in DefaultPipelineProfiles.All)
        {
            Assert.Contains(list, loaded => loaded.Id == defaultProfile.Id);
        }
    }

    [Fact]
    public async Task SeedAsync_IsIdempotent()
    {
        await using var fx = await new TempPipelineDatabaseFixture().InitializeAsync();
        var (seeder, store) = Build(fx);

        await seeder.SeedAsync(CancellationToken.None);
        await seeder.SeedAsync(CancellationToken.None);

        var list = await store.ListAsync(CancellationToken.None);
        Assert.Equal(DefaultPipelineProfiles.All.Count, list.Count);
    }

    [Fact]
    public async Task SeedAsync_PreservesUserCustomizations()
    {
        // Now that the user can reorder pipeline steps from Settings, the profile in DB is the
        // source of truth. Seeder must not clobber existing entries on each run.
        await using var fx = await new TempPipelineDatabaseFixture().InitializeAsync();
        var (seeder, store) = Build(fx);
        var customised = new PipelineProfile(
            DefaultPipelineProfiles.OnClipboardId,
            "User-renamed",
            "event:clipboard",
            new[] { new PipelineStep("user.custom.task") });
        await store.UpsertAsync(customised, CancellationToken.None);

        await seeder.SeedAsync(CancellationToken.None);

        var loaded = await store.GetAsync(DefaultPipelineProfiles.OnClipboardId, CancellationToken.None);
        Assert.Equal("User-renamed", loaded!.DisplayName);
        Assert.Single(loaded.Steps);
        Assert.Equal("user.custom.task", loaded.Steps[0].TaskId);
    }

    [Fact]
    public async Task ResetToDefaultsAsync_ReplacesExistingProfile()
    {
        await using var fx = await new TempPipelineDatabaseFixture().InitializeAsync();
        var (seeder, store) = Build(fx);
        var customised = new PipelineProfile(
            DefaultPipelineProfiles.OnClipboardId, "Old", "event:clipboard",
            new[] { new PipelineStep("legacy") });
        await store.UpsertAsync(customised, CancellationToken.None);

        await seeder.ResetToDefaultsAsync(DefaultPipelineProfiles.OnClipboardId, CancellationToken.None);

        var loaded = await store.GetAsync(DefaultPipelineProfiles.OnClipboardId, CancellationToken.None);
        var expected = DefaultPipelineProfiles.All.Single(p => p.Id == DefaultPipelineProfiles.OnClipboardId);
        Assert.Equal(expected.DisplayName, loaded!.DisplayName);
        Assert.Equal(expected.Steps.Count, loaded.Steps.Count);
    }

    [Fact]
    public async Task SeedAsync_DemotesOrphanedBuiltInToCustom()
    {
        // A built-in that has been removed from DefaultPipelineProfiles.All (e.g. OCR was tried
        // then dropped) must be DEMOTED to custom rather than deleted, so a user who customised
        // its steps doesn't silently lose their work. After demotion the profile remains in DB
        // with IsBuiltIn=false — surfacing under the "Custom" tab where the user can keep or
        // remove it manually.
        await using var fx = await new TempPipelineDatabaseFixture().InitializeAsync();
        var (seeder, store) = Build(fx);
        var orphan = new PipelineProfile(
            Id: "ghost-builtin",
            DisplayName: "Ghost workflow",
            Trigger: "hotkey:ghost",
            Steps: new[] { new PipelineStep("user.modified.step") },
            IsBuiltIn: true);
        await store.UpsertAsync(orphan, CancellationToken.None);

        await seeder.SeedAsync(CancellationToken.None);

        var loaded = await store.GetAsync("ghost-builtin", CancellationToken.None);
        Assert.NotNull(loaded);
        Assert.False(loaded!.IsBuiltIn);
        // Steps preserved verbatim — we only flip the metadata flag, never touch the user's work.
        Assert.Single(loaded.Steps);
        Assert.Equal("user.modified.step", loaded.Steps[0].TaskId);
    }

    [Fact]
    public async Task SeedAsync_LeavesUserCustomProfilesUntouched()
    {
        // Real customs (IsBuiltIn=false) must never be demoted-again or otherwise modified by the
        // GC pass — they're already under user control. Sanity check guarding against an off-by-
        // one in the orphan filter.
        await using var fx = await new TempPipelineDatabaseFixture().InitializeAsync();
        var (seeder, store) = Build(fx);
        var customProfile = new PipelineProfile(
            Id: "custom-user-thing",
            DisplayName: "My workflow",
            Trigger: "hotkey:my-thing",
            Steps: new[] { new PipelineStep("anything") },
            IsBuiltIn: false);
        await store.UpsertAsync(customProfile, CancellationToken.None);

        await seeder.SeedAsync(CancellationToken.None);

        var loaded = await store.GetAsync("custom-user-thing", CancellationToken.None);
        Assert.NotNull(loaded);
        Assert.False(loaded!.IsBuiltIn);
        Assert.Equal("My workflow", loaded.DisplayName);
        Assert.Single(loaded.Steps);
    }
}
