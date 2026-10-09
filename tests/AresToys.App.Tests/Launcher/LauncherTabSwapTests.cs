using AresToys.App.Services.Launcher;
using Xunit;

namespace AresToys.App.Tests.Launcher;

/// <summary>Dragging one tab header onto another in docked mode swaps the two pages (issue #25).
/// The number keys stay bound to their position: what moves is the content (cells and title).</summary>
public sealed class LauncherTabSwapTests
{
    private static LauncherCell Cell(string tab, string key, string label) =>
        new(tab, key, label, @"C:\Apps\" + label + ".exe", "--arg " + label);

    private static async Task<LauncherStore> StoreWithAsync(params LauncherCell[] cells)
    {
        var store = new LauncherStore(new InMemorySettingsStore());
        foreach (var c in cells) await store.UpdateCellAsync(c, CancellationToken.None);
        return store;
    }

    [Fact]
    public async Task SwappingTwoTabsMovesEveryCellToTheOtherTab()
    {
        var store = await StoreWithAsync(
            Cell("1", "Q", "One-Q"),
            Cell("1", "A", "One-A"),
            Cell("3", "Q", "Three-Q"),
            Cell("3", "Z", "Three-Z"));

        await store.SwapTabsAsync("1", "3", CancellationToken.None);
        var state = await store.LoadAsync(CancellationToken.None);

        Assert.Equal("Three-Q", state.Get("1", "Q").Label);
        Assert.Equal("Three-Z", state.Get("1", "Z").Label);
        Assert.Equal("One-Q", state.Get("3", "Q").Label);
        Assert.Equal("One-A", state.Get("3", "A").Label);
        // Slots that were filled only on the other side come out empty, not duplicated.
        Assert.False(state.Get("1", "A").IsConfigured);
        Assert.False(state.Get("3", "Z").IsConfigured);
    }

    [Fact]
    public async Task SwappedCellsKeepAllTheirSettings()
    {
        var rich = new LauncherCell("2", "W", "Rich", @"C:\Apps\rich.exe", "--x",
            RunAsAdmin: true, WindowMode: LauncherWindowMode.Maximized,
            WindowTitle: "Rich Window", ProcessName: "rich",
            IconPath: @"C:\Icons\rich.ico", IconIndex: 4, WorkflowId: "my.flow");
        var store = await StoreWithAsync(rich);

        await store.SwapTabsAsync("2", "7", CancellationToken.None);
        var moved = (await store.LoadAsync(CancellationToken.None)).Get("7", "W");

        Assert.Equal(rich with { TabKey = "7" }, moved);
    }

    [Fact]
    public async Task TitlesTravelWithTheirTabs()
    {
        var store = await StoreWithAsync();
        await store.UpdateTabTitleAsync("1", "Work", CancellationToken.None);
        await store.UpdateTabTitleAsync("4", "Games", CancellationToken.None);

        await store.SwapTabsAsync("1", "4", CancellationToken.None);
        var state = await store.LoadAsync(CancellationToken.None);

        Assert.Equal("Games", state.TabTitle("1"));
        Assert.Equal("Work", state.TabTitle("4"));
    }

    [Fact]
    public async Task ATitleSwappedWithAnUntitledTabLeavesTheSourceUntitled()
    {
        var store = await StoreWithAsync();
        await store.UpdateTabTitleAsync("1", "Work", CancellationToken.None);

        await store.SwapTabsAsync("1", "5", CancellationToken.None);
        var state = await store.LoadAsync(CancellationToken.None);

        Assert.Equal(string.Empty, state.TabTitle("1"));
        Assert.Equal("Work", state.TabTitle("5"));
    }

    [Fact]
    public async Task OtherTabsAndTheFunctionStripAreLeftAlone()
    {
        var store = await StoreWithAsync(
            Cell("1", "Q", "One"),
            Cell("2", "Q", "Two"),
            Cell(LauncherTabs.FunctionStrip, "F1", "Fn"));

        await store.SwapTabsAsync("1", "3", CancellationToken.None);
        var state = await store.LoadAsync(CancellationToken.None);

        Assert.Equal("Two", state.Get("2", "Q").Label);
        Assert.Equal("Fn", state.Get(LauncherTabs.FunctionStrip, "F1").Label);
    }

    [Fact]
    public async Task SwappingTwiceRestoresTheOriginalLayout()
    {
        var store = await StoreWithAsync(Cell("1", "Q", "One"), Cell("9", "P", "Nine"));
        var before = await store.LoadAsync(CancellationToken.None);

        await store.SwapTabsAsync("1", "9", CancellationToken.None);
        await store.SwapTabsAsync("9", "1", CancellationToken.None);
        var after = await store.LoadAsync(CancellationToken.None);

        Assert.Equal(before.Cells.Values.Where(c => c.IsConfigured).OrderBy(c => c.ComposedKey),
                     after.Cells.Values.Where(c => c.IsConfigured).OrderBy(c => c.ComposedKey));
    }

    [Theory]
    [InlineData("1", "1")]
    [InlineData("1", LauncherTabs.FunctionStrip)]
    [InlineData("1", "X")]
    public async Task InvalidSwapsChangeNothing(string a, string b)
    {
        var store = await StoreWithAsync(Cell("1", "Q", "One"), Cell(LauncherTabs.FunctionStrip, "F1", "Fn"));
        var version = store.StateVersion;

        await store.SwapTabsAsync(a, b, CancellationToken.None);
        var state = await store.LoadAsync(CancellationToken.None);

        Assert.Equal(version, store.StateVersion);
        Assert.Equal("One", state.Get("1", "Q").Label);
        Assert.Equal("Fn", state.Get(LauncherTabs.FunctionStrip, "F1").Label);
    }

    [Fact]
    public async Task ASwapBumpsTheStateVersionOnce()
    {
        // The launcher window skips a reload when the version hasn't moved; one save means one
        // bump, and a reload that sees the whole swap at once.
        var store = await StoreWithAsync(Cell("1", "Q", "One"));
        var version = store.StateVersion;

        await store.SwapTabsAsync("1", "2", CancellationToken.None);

        Assert.Equal(version + 1, store.StateVersion);
    }
}
