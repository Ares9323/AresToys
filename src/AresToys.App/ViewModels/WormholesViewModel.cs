using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AresToys.App.Services.Wormholes;
using AresToys.App.Views;

namespace AresToys.App.ViewModels;

/// <summary>Backs the Settings → Wormholes tab. Owns the list of <see cref="WormholeRowViewModel"/>
/// rendered as a grid, plus the "New wormhole" command that pops the same dialog the tray entry
/// uses. <see cref="ReloadAsync"/> rehydrates from <see cref="IWormholeStore"/> — called on tab
/// activation (mirrors <c>UploadersViewModel.ReloadAsync</c> / <c>HotkeysViewModel.ReloadAsync</c>
/// pattern) so the grid doesn't pay any I/O cost until the user actually navigates there.</summary>
public sealed partial class WormholesViewModel : ObservableObject
{
    private readonly IWormholeStore _store;
    private readonly IWormholeWindowManager _manager;
    private readonly WormholeDefaultsService _defaults;
    private bool _suppressDefaultsPersist;

    public ObservableCollection<WormholeRowViewModel> Rows { get; } = new();

    [ObservableProperty] private bool _isEmpty = true;

    /// <summary>Id of the wormhole the user last interacted with (clicked / selected an item /
    /// dragged). Drives the row-highlight in the Settings panel via <see cref="WormholeRowViewModel.IsSelected"/>.
    /// Null on first run (no interaction yet). Fed by <see cref="IWormholeWindowManager.WormholeFocused"/>;
    /// resets to null when a row is removed or on reload.</summary>
    [ObservableProperty] private Guid? _selectedWormholeId;

    /// <summary>App-wide default icon-tile size. 0 means "use the user's Windows desktop icon
    /// size" (see <see cref="DesktopIconSize"/>). Bound to the numeric input in Settings →
    /// Wormholes; mutating the setter persists to <see cref="WormholeDefaultsService"/> which
    /// then notifies every open wormhole to refresh.</summary>
    [ObservableProperty] private int _defaultIconSizePx;

    /// <summary>App-wide default opacity expressed as a percentage 30–100 (rendering in WPF
    /// uses 0.30–1.00). Bound to the slider in Settings → Wormholes; the underlying service
    /// holds the double form.</summary>
    [ObservableProperty] private int _defaultOpacityPercent = 70;

    /// <summary>App-wide opacity for the OuterFrame's 1 px accent ring + the drop shadow.
    /// Independent from <see cref="DefaultOpacityPercent"/> (which fades the body/header backdrops).</summary>
    [ObservableProperty] private int _defaultBorderOpacityPercent = 100;

    /// <summary>App-wide default tile padding (extra pixels around the icon inside its tile).
    /// Smaller = denser grid; larger = airier. Bound to a slider in Settings → Wormholes.</summary>
    [ObservableProperty] private int _defaultTilePaddingPx = 4;

    /// <summary>App-wide line spacing applied as a "negative-margin"–like effect: applied as the
    /// tile's bottom Margin so adjacent tile rows OVERLAP (negative) or get extra gap (positive).
    /// Does NOT change the text area / number of label lines (those live on <see cref="DefaultLabelFontSizePx"/>
    /// and <see cref="DefaultLabelMaxLines"/>).</summary>
    [ObservableProperty] private int _defaultLineSpacingPx = -4;

    /// <summary>App-wide label font size (px). Drives the FontSize of the TextBlock under each
    /// icon AND, via the line-height heuristic, the TileHeight reserved for the label area.</summary>
    [ObservableProperty] private int _defaultLabelFontSizePx = 12;

    /// <summary>Max number of wrapped lines a label may use before being ellipsized. 1 = 1-line
    /// Explorer style, 2 = default (wrap up to 2 lines), 3 = generous wrap.</summary>
    [ObservableProperty] private int _defaultLabelMaxLines = 2;

    public WormholesViewModel(IWormholeStore store, IWormholeWindowManager manager, WormholeDefaultsService defaults)
    {
        _store = store;
        _manager = manager;
        _defaults = defaults;
        // Hydrate the VM properties from the already-loaded service (App.xaml.cs LoadAsync at
        // startup). Subsequent slider drags flow OUT through the partial-method setters below.
        _suppressDefaultsPersist = true;
        DefaultIconSizePx = _defaults.DefaultIconSizePx;
        DefaultOpacityPercent = (int)Math.Round(_defaults.DefaultOpacity * 100);
        DefaultBorderOpacityPercent = (int)Math.Round(_defaults.DefaultBorderOpacity * 100);
        DefaultTilePaddingPx = _defaults.DefaultTilePaddingPx;
        DefaultLineSpacingPx = _defaults.DefaultLineSpacingPx;
        DefaultLabelFontSizePx = _defaults.DefaultLabelFontSizePx;
        DefaultLabelMaxLines = _defaults.DefaultLabelMaxLines;
        _suppressDefaultsPersist = false;
        // Live grid refresh: when the manager persists a record (user drag/resize on the live
        // chrome, lock toggle from chrome, hamburger rename, etc.), the matching row updates
        // its displayed fields in place. The event fires on the UI dispatcher already.
        _manager.RecordChanged += OnManagerRecordChanged;
        _manager.WormholeFocused += OnManagerWormholeFocused;
    }

    /// <summary>The manager fires this when the user clicks on a wormhole (chrome or item). We
    /// propagate to <see cref="SelectedWormholeId"/> and refresh every row's
    /// <see cref="WormholeRowViewModel.IsSelected"/> so exactly one row reads as selected.</summary>
    private void OnManagerWormholeFocused(object? sender, Guid id)
    {
        if (SelectedWormholeId == id) return;
        SelectedWormholeId = id;
        foreach (var row in Rows) row.RefreshIsSelected();
    }

    partial void OnDefaultIconSizePxChanged(int value)
    {
        if (_suppressDefaultsPersist) return;
        _ = _defaults.SetDefaultIconSizeAsync(value, CancellationToken.None);
    }

    partial void OnDefaultOpacityPercentChanged(int value)
    {
        if (_suppressDefaultsPersist) return;
        _ = _defaults.SetDefaultOpacityAsync(value / 100.0, CancellationToken.None);
    }

    partial void OnDefaultBorderOpacityPercentChanged(int value)
    {
        if (_suppressDefaultsPersist) return;
        _ = _defaults.SetDefaultBorderOpacityAsync(value / 100.0, CancellationToken.None);
    }

    partial void OnDefaultTilePaddingPxChanged(int value)
    {
        if (_suppressDefaultsPersist) return;
        _ = _defaults.SetDefaultTilePaddingAsync(value, CancellationToken.None);
    }

    partial void OnDefaultLineSpacingPxChanged(int value)
    {
        if (_suppressDefaultsPersist) return;
        _ = _defaults.SetDefaultLineSpacingAsync(value, CancellationToken.None);
    }

    partial void OnDefaultLabelFontSizePxChanged(int value)
    {
        if (_suppressDefaultsPersist) return;
        _ = _defaults.SetDefaultLabelFontSizeAsync(value, CancellationToken.None);
    }

    partial void OnDefaultLabelMaxLinesChanged(int value)
    {
        if (_suppressDefaultsPersist) return;
        _ = _defaults.SetDefaultLabelMaxLinesAsync(value, CancellationToken.None);
    }

    private void OnManagerRecordChanged(object? sender, Guid id)
    {
        var row = Rows.FirstOrDefault(r => r.Id == id);
        row?.RefreshDisplay();
    }

    /// <summary>Pull the latest snapshot from the store and rebuild the rows. Idempotent —
    /// safe to call on every tab activation. Doesn't subscribe to store change events for v1
    /// (drag-induced LocationChanged would otherwise spam the grid with rebuilds); the user
    /// can re-click the sidebar entry to refresh after manipulating wormholes from chrome.
    ///
    /// Re-hydrates the default observables from <see cref="WormholeDefaultsService"/> too: this
    /// VM is built eagerly during DI (because <see cref="SettingsViewModel"/> takes it as a ctor
    /// dependency) BEFORE the async <c>LoadAsync</c> reads the persisted defaults from disk.
    /// The ctor's hydration therefore captures fallback values (95 % opacity etc.); re-hydrating
    /// here on every tab activation makes the panel always reflect what's actually on disk.</summary>
    public async Task ReloadAsync()
    {
        _suppressDefaultsPersist = true;
        try
        {
            DefaultIconSizePx       = _defaults.DefaultIconSizePx;
            DefaultOpacityPercent       = (int)Math.Round(_defaults.DefaultOpacity * 100);
            DefaultBorderOpacityPercent = (int)Math.Round(_defaults.DefaultBorderOpacity * 100);
            DefaultTilePaddingPx        = _defaults.DefaultTilePaddingPx;
            DefaultLineSpacingPx    = _defaults.DefaultLineSpacingPx;
            DefaultLabelFontSizePx  = _defaults.DefaultLabelFontSizePx;
            DefaultLabelMaxLines    = _defaults.DefaultLabelMaxLines;
        }
        finally { _suppressDefaultsPersist = false; }

        var records = await _store.LoadAllAsync(CancellationToken.None).ConfigureAwait(true);
        Rows.Clear();
        foreach (var r in records)
            Rows.Add(new WormholeRowViewModel(r, _store, _manager, this));
        IsEmpty = Rows.Count == 0;
    }

    /// <summary>Called by a row's Delete command after the manager has removed the record.
    /// Keeps the grid in sync without a full <see cref="ReloadAsync"/> round-trip.</summary>
    internal void Remove(WormholeRowViewModel row)
    {
        Rows.Remove(row);
        IsEmpty = Rows.Count == 0;
    }

    /// <summary>Mirror a successful <see cref="IWormholeStore.MoveAsync"/> result on the visible
    /// <see cref="Rows"/> collection: yank the row and re-insert at <paramref name="newIndex"/>.
    /// Called by <see cref="WormholeRowViewModel"/>'s MoveUp/MoveDown commands after the store
    /// has already persisted the new order — keeps the grid responsive without a full Reload.</summary>
    internal void MoveRow(WormholeRowViewModel row, int newIndex)
    {
        var oldIndex = Rows.IndexOf(row);
        if (oldIndex < 0 || oldIndex == newIndex) return;
        Rows.Move(oldIndex, newIndex);
    }

    [RelayCommand]
    private async Task NewWormholeAsync()
    {
        var dlg = new NewWormholeDialog();
        if (dlg.ShowDialog() != true || dlg.Result is null) return;
        var choice = dlg.Result;
        try
        {
            await _manager.CreateAsync(choice.Title, choice.SourceFolder, CancellationToken.None)
                .ConfigureAwait(true);
            await ReloadAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show("Couldn't create the wormhole:\n" + ex.Message,
                "AresToys", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
    }

    [RelayCommand]
    private async Task RefreshAsync() => await ReloadAsync().ConfigureAwait(true);
}
