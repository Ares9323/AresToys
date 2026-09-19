using AresToys.App.Services.Wormholes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AresToys.App.Tests.Wormholes;

/// <summary>The two wormhole preferences added alongside the service-file filter: one-click open
/// and whether the OS's bookkeeping files get a tile. Both live in
/// <see cref="WormholeDefaultsService"/>, so what matters here is that their defaults are the ones
/// intended, that they survive a round-trip through the settings store, and that flipping them
/// announces itself so open wormholes can follow.</summary>
public sealed class WormholeClickAndFilterOptionsTests
{
    private static WormholeDefaultsService Build(FakeSettingsStore store) =>
        new(store, NullLogger<WormholeDefaultsService>.Instance);

    [Fact]
    public void OneClickOpenIsOffByDefaultSoOpeningKeepsTakingADoubleClick()
    {
        Assert.False(Build(new FakeSettingsStore()).OpenWithOneClick);
    }

    [Fact]
    public void ServiceFilesAreHiddenByDefault()
    {
        Assert.True(Build(new FakeSettingsStore()).HideServiceFiles);
    }

    [Fact]
    public void ExpandingCollapsedWormholesOnHoverIsOffByDefault()
    {
        Assert.False(Build(new FakeSettingsStore()).ExpandCollapsedOnHover);
    }

    [Fact]
    public async Task ExpandOnHoverSurvivesAReloadAndAnnouncesItself()
    {
        var store = new FakeSettingsStore();
        var defaults = Build(store);
        var announced = 0;
        defaults.ExpandCollapsedOnHoverChanged += (_, _) => announced++;

        await defaults.SetExpandCollapsedOnHoverAsync(true, CancellationToken.None);
        await defaults.SetExpandCollapsedOnHoverAsync(true, CancellationToken.None);   // no-op

        Assert.Equal(1, announced);

        var reloaded = Build(store);
        await reloaded.LoadAsync(CancellationToken.None);
        Assert.True(reloaded.ExpandCollapsedOnHover);
    }

    [Fact]
    public async Task OneClickOpenSurvivesAReload()
    {
        var store = new FakeSettingsStore();
        await Build(store).SetOpenWithOneClickAsync(true, CancellationToken.None);

        var reloaded = Build(store);
        await reloaded.LoadAsync(CancellationToken.None);

        Assert.True(reloaded.OpenWithOneClick);
    }

    [Fact]
    public async Task ShowingServiceFilesSurvivesAReload()
    {
        var store = new FakeSettingsStore();
        await Build(store).SetHideServiceFilesAsync(false, CancellationToken.None);

        var reloaded = Build(store);
        await reloaded.LoadAsync(CancellationToken.None);

        // The interesting direction: the non-default value has to stick, or the filter would
        // silently switch itself back on at every launch.
        Assert.False(reloaded.HideServiceFiles);
    }

    [Fact]
    public async Task FlippingEitherOptionAnnouncesItselfSoOpenWormholesCanFollow()
    {
        var defaults = Build(new FakeSettingsStore());
        var cursorEvents = 0;
        var itemEvents = 0;
        defaults.OpenWithOneClickChanged += (_, _) => cursorEvents++;
        defaults.ServiceFilesVisibilityChanged += (_, _) => itemEvents++;

        await defaults.SetOpenWithOneClickAsync(true, CancellationToken.None);
        await defaults.SetHideServiceFilesAsync(false, CancellationToken.None);

        Assert.Equal(1, cursorEvents);
        Assert.Equal(1, itemEvents);

        // Setting the same value again is a no-op: no write, no event, no wormhole re-enumeration.
        await defaults.SetOpenWithOneClickAsync(true, CancellationToken.None);
        await defaults.SetHideServiceFilesAsync(false, CancellationToken.None);

        Assert.Equal(1, cursorEvents);
        Assert.Equal(1, itemEvents);
    }

    [Theory]
    [InlineData("Wormhole_OpenWithOneClick")]
    [InlineData("Wormhole_OpenWithOneClickTooltip")]
    [InlineData("Wormhole_HideServiceFiles")]
    [InlineData("Wormhole_HideServiceFilesTooltip")]
    [InlineData("Wormhole_ExpandCollapsedOnHover")]
    [InlineData("Wormhole_ExpandCollapsedOnHoverTooltip")]
    public void BothCheckboxesReadInEnglishAndItalian(string key)
    {
        foreach (var culture in new[] { "en", "it" })
        {
            var value = AresToys.App.Resources.Strings.ResourceManager.GetString(
                key, new System.Globalization.CultureInfo(culture));
            Assert.False(string.IsNullOrWhiteSpace(value), $"{key} missing for '{culture}'");
        }
    }
}
