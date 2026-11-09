using AresToys.Pipeline.Profiles;
using Xunit;

namespace AresToys.Pipeline.Tests.Profiles;

public class DefaultPipelineProfilesTests
{
    [Fact]
    public void EveryHotkeyableBuiltIn_IsCategorised()
    {
        // Every hotkey-driven built-in workflow must have an entry in CategoriesById, otherwise
        // it falls through to the "Other" bucket and the user finds it orphaned at the bottom of
        // a tab labelled neither Capture / Clipboard / Upload / Tools. This test catches a new
        // built-in being added without remembering to categorise it. The category is decided by
        // CategoryOf (by content first, then the id map), so a built-in decided by its content,
        // like the resize presets, counts as categorised.
        var uncategorised = DefaultPipelineProfiles.All
            .Where(p => p.Trigger.StartsWith("hotkey:", StringComparison.Ordinal))
            .Where(p => DefaultPipelineProfiles.CategoryOf(p) == DefaultPipelineProfiles.DefaultCategory)
            .Select(p => p.Id)
            .ToArray();
        Assert.True(uncategorised.Length == 0,
            "Hotkey-able built-ins missing a CategoriesById entry: " + string.Join(", ", uncategorised));
    }

    [Fact]
    public void CategoriesById_OnlyReferencesKnownProfileIds()
    {
        // The opposite check: a stale CategoriesById entry pointing at an id that no longer
        // exists in DefaultPipelineProfiles.All is dead config — the test catches accidental
        // typos and rename misses.
        var validIds = DefaultPipelineProfiles.All.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        var orphaned = DefaultPipelineProfiles.CategoriesById.Keys
            .Where(k => !validIds.Contains(k))
            .ToArray();
        Assert.True(orphaned.Length == 0,
            "CategoriesById has entries for ids not in DefaultPipelineProfiles.All: " + string.Join(", ", orphaned));
    }

    [Fact]
    public void All_HasNoDuplicateIds()
    {
        // Trivially true today, but the catalog grows constantly — guards against a copy-paste
        // mistake that would silently overwrite a profile during seeding.
        var dupes = DefaultPipelineProfiles.All
            .GroupBy(p => p.Id, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToArray();
        Assert.True(dupes.Length == 0,
            "Duplicate profile ids in DefaultPipelineProfiles.All: " + string.Join(", ", dupes));
    }
}
