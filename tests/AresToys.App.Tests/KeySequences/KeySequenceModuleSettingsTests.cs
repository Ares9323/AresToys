using AresToys.App.Services.KeySequences;
using AresToys.App.Tests.KeySequences.Fakes;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AresToys.App.Tests.KeySequences;

public class KeySequenceModuleSettingsTests
{
    private static (KeySequenceModuleSettings settings, FakeSettingsStore store, TestLogger<KeySequenceModuleSettings> logger) Build()
    {
        var store = new FakeSettingsStore();
        var logger = new TestLogger<KeySequenceModuleSettings>();
        var settings = new KeySequenceModuleSettings(store, logger);
        return (settings, store, logger);
    }

    [Fact]
    public async Task LoadAsync_AllKeysUnset_AppliesDefaults()
    {
        var (settings, _, _) = Build();

        await settings.LoadAsync(CancellationToken.None);

        Assert.Equal(OverlayPositionMode.FixedCenter, settings.Position);
        Assert.Equal(KeySequenceModuleSettings.DefaultConfirmVk, settings.ConfirmVk);
        Assert.Equal(0x0Du, settings.ConfirmVk);
        Assert.Same(KeySequenceModuleSettings.DefaultBlacklist, settings.Blacklist);
        Assert.Null(settings.LastX);
        Assert.Null(settings.LastY);
    }

    [Fact]
    public async Task LoadAsync_CorruptBlacklistJson_FallsBackToDefault_WarningLogged()
    {
        var (settings, store, logger) = Build();
        store.Backing[KeySequenceModuleSettings.KeyBlacklist] = "{ not valid json ]";

        await settings.LoadAsync(CancellationToken.None);

        Assert.Same(KeySequenceModuleSettings.DefaultBlacklist, settings.Blacklist);
        Assert.True(logger.HasLevel(LogLevel.Warning), "Expected a warning for corrupt blacklist JSON.");
    }

    [Fact]
    public async Task LoadAsync_LastPositionStored_Hydrated()
    {
        var (settings, store, _) = Build();
        store.Backing[KeySequenceModuleSettings.KeyLastX] = "1234";
        store.Backing[KeySequenceModuleSettings.KeyLastY] = "567";

        await settings.LoadAsync(CancellationToken.None);

        Assert.Equal(1234, settings.LastX);
        Assert.Equal(567, settings.LastY);
    }

    [Fact]
    public async Task ApplyAsync_PersistsAllKeysAndRaisesChanged()
    {
        var (settings, store, _) = Build();
        var raised = 0;
        settings.Changed += (_, _) => raised++;

        var newBlacklist = new[] { "explorer.exe", "notepad.exe" };
        await settings.ApplyAsync(
            OverlayPositionMode.MouseCursor,
            confirmVk: 0x09u, // Tab
            blacklist: newBlacklist,
            CancellationToken.None);

        Assert.Equal(1, raised);
        Assert.Equal("MouseCursor", store.Backing[KeySequenceModuleSettings.KeyPosition]);
        Assert.Equal("9", store.Backing[KeySequenceModuleSettings.KeyConfirmVk]);
        Assert.Contains("explorer.exe", store.Backing[KeySequenceModuleSettings.KeyBlacklist]);
        Assert.Contains("notepad.exe", store.Backing[KeySequenceModuleSettings.KeyBlacklist]);

        Assert.Equal(OverlayPositionMode.MouseCursor, settings.Position);
        Assert.Equal(0x09u, settings.ConfirmVk);
        Assert.Equal(newBlacklist, settings.Blacklist);
    }

    [Fact]
    public async Task ApplyAsync_ZeroConfirmVk_CoercedToDefault()
    {
        var (settings, store, _) = Build();
        await settings.ApplyAsync(
            OverlayPositionMode.FixedCenter,
            confirmVk: 0u,
            blacklist: Array.Empty<string>(),
            CancellationToken.None);

        Assert.Equal(KeySequenceModuleSettings.DefaultConfirmVk, settings.ConfirmVk);
        Assert.Equal(
            KeySequenceModuleSettings.DefaultConfirmVk.ToString(System.Globalization.CultureInfo.InvariantCulture),
            store.Backing[KeySequenceModuleSettings.KeyConfirmVk]);
    }

    [Fact]
    public async Task SaveLastPositionAsync_PersistsCoordinates()
    {
        var (settings, store, _) = Build();

        await settings.SaveLastPositionAsync(123, 456, CancellationToken.None);

        Assert.Equal(123, settings.LastX);
        Assert.Equal(456, settings.LastY);
        Assert.Equal("123", store.Backing[KeySequenceModuleSettings.KeyLastX]);
        Assert.Equal("456", store.Backing[KeySequenceModuleSettings.KeyLastY]);
    }
}
