using System.Runtime.CompilerServices;
using AresToys.App.Services.Notifications;
using AresToys.Storage.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AresToys.App.Tests.Notifications;

public sealed class ToastLifetimeServiceTests
{
    private static ToastLifetimeService Create(out FakeSettingsStore store)
    {
        store = new FakeSettingsStore();
        return new ToastLifetimeService(store, NullLogger<ToastLifetimeService>.Instance);
    }

    /// <summary>Untouched settings must reproduce what Windows does on its own: a normal popup and
    /// an entry that stays until the user clears it.</summary>
    [Fact]
    public async Task DefaultsToStockWindowsBehaviour()
    {
        var service = Create(out _);
        await service.LoadAsync(CancellationToken.None);

        Assert.Equal(ToastLifetimeService.PopupMaxSeconds, service.PopupSeconds);
        Assert.Equal(ToastLifetimeService.CenterUnlimited, service.CenterSeconds);
        Assert.False(service.NotificationsEffectivelyInvisible);
        Assert.False(service.ClosesPopupEarly);
    }

    [Fact]
    public async Task ValuesRoundTripThroughTheStore()
    {
        var service = Create(out var store);
        await service.SetPopupSecondsAsync(4, CancellationToken.None);
        await service.SetCenterSecondsAsync(0, CancellationToken.None);

        var reloaded = new ToastLifetimeService(store, NullLogger<ToastLifetimeService>.Instance);
        await reloaded.LoadAsync(CancellationToken.None);

        Assert.Equal(4, reloaded.PopupSeconds);
        Assert.Equal(ToastLifetimeService.CenterNone, reloaded.CenterSeconds);
    }

    [Theory]
    [InlineData(0, 0)]        // no popup
    [InlineData(1, 1)]
    [InlineData(7, 7)]
    [InlineData(8, 7)]        // Windows can't be pushed past its own duration
    [InlineData(600, 7)]
    [InlineData(-1, 0)]       // negatives have no meaning on this setting any more
    [InlineData(-9999, 0)]
    public async Task PopupSecondsIsClampedToZeroThroughSeven(int input, int expected)
    {
        var service = Create(out _);
        await service.SetPopupSecondsAsync(input, CancellationToken.None);
        Assert.Equal(expected, service.PopupSeconds);
    }

    [Theory]
    [InlineData(-1, -1)]                   // unlimited
    [InlineData(-42, -1)]                  // anything below the sentinel collapses onto it
    [InlineData(0, 0)]                     // never kept
    [InlineData(30, 30)]
    [InlineData(86400, 86400)]
    [InlineData(31_536_000, 31_536_000)]   // a year is the user's business: no ceiling
    public async Task CenterSecondsHasNoUpperBound(int input, int expected)
    {
        var service = Create(out _);
        await service.SetCenterSecondsAsync(input, CancellationToken.None);
        Assert.Equal(expected, service.CenterSeconds);
    }

    /// <summary>We only take over the popup timing when the user wants it SHORTER than Windows'
    /// own duration. At the ceiling the OS keeps control, which is what preserves a longer
    /// "show notifications for" accessibility setting.</summary>
    [Theory]
    [InlineData(0, false)]   // no popup at all — nothing to close
    [InlineData(1, true)]
    [InlineData(6, true)]
    [InlineData(7, false)]   // ceiling = leave it to Windows
    public async Task PopupIsClosedEarlyOnlyBelowTheCeiling(int seconds, bool expected)
    {
        var service = Create(out _);
        await service.SetPopupSecondsAsync(seconds, CancellationToken.None);
        Assert.Equal(expected, service.ClosesPopupEarly);
    }

    [Fact]
    public async Task InvisibleOnlyWhenThereIsNeitherAPopupNorAnEntry()
    {
        var service = Create(out _);

        await service.SetPopupSecondsAsync(0, CancellationToken.None);
        Assert.False(service.NotificationsEffectivelyInvisible); // still kept in the Center

        await service.SetCenterSecondsAsync(0, CancellationToken.None);
        Assert.True(service.NotificationsEffectivelyInvisible);

        await service.SetPopupSecondsAsync(3, CancellationToken.None);
        Assert.False(service.NotificationsEffectivelyInvisible);
    }

    [Fact]
    public async Task GarbageInTheStoreFallsBackToTheDefaults()
    {
        var service = Create(out var store);
        await store.SetAsync(ToastLifetimeService.PopupSecondsKey, "not-a-number", false, CancellationToken.None);
        await store.SetAsync(ToastLifetimeService.CenterSecondsKey, "", false, CancellationToken.None);

        await service.LoadAsync(CancellationToken.None);

        Assert.Equal(ToastLifetimeService.PopupMaxSeconds, service.PopupSeconds);
        Assert.Equal(ToastLifetimeService.CenterUnlimited, service.CenterSeconds);
    }

    [Fact]
    public async Task OutOfRangeValuesAlreadyInTheStoreAreClampedOnLoad()
    {
        var service = Create(out var store);
        await store.SetAsync(ToastLifetimeService.PopupSecondsKey, "100000", false, CancellationToken.None);
        await store.SetAsync(ToastLifetimeService.CenterSecondsKey, "-3", false, CancellationToken.None);

        await service.LoadAsync(CancellationToken.None);

        Assert.Equal(ToastLifetimeService.PopupMaxSeconds, service.PopupSeconds);
        Assert.Equal(ToastLifetimeService.CenterUnlimited, service.CenterSeconds);
    }

    /// <summary>The first iteration used the opposite Center sentinels (0 = forever, -1 = never).
    /// Reading those keys under the current meaning would turn a saved "stock behaviour" into
    /// "no notification at all", so the settings moved to new keys and the stale ones must be
    /// ignored entirely.</summary>
    [Fact]
    public async Task SettingsSavedUnderTheOldKeysAreIgnored()
    {
        var service = Create(out var store);
        await store.SetAsync("app.notifications.popup_seconds", "0", false, CancellationToken.None);
        await store.SetAsync("app.notifications.center_seconds", "0", false, CancellationToken.None);

        await service.LoadAsync(CancellationToken.None);

        Assert.Equal(ToastLifetimeService.PopupMaxSeconds, service.PopupSeconds);
        Assert.Equal(ToastLifetimeService.CenterUnlimited, service.CenterSeconds);
        Assert.False(service.NotificationsEffectivelyInvisible);
    }

    private sealed class FakeSettingsStore : ISettingsStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public Task<string?> GetAsync(string key, CancellationToken cancellationToken)
            => Task.FromResult(_values.TryGetValue(key, out var v) ? v : null);

        public Task SetAsync(string key, string value, bool sensitive, CancellationToken cancellationToken)
        {
            _values[key] = value;
            return Task.CompletedTask;
        }

        public Task<bool> RemoveAsync(string key, CancellationToken cancellationToken)
            => Task.FromResult(_values.Remove(key));

        public async IAsyncEnumerable<SettingEntry> EnumerateAsync(
            bool includeSensitive = false,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var (k, v) in _values) yield return new SettingEntry(k, v, false);
            await Task.CompletedTask;
        }
    }
}
