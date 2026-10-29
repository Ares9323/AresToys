using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using AresToys.App.Resources;
using AresToys.App.Services.Zones;
using AresToys.Storage.Settings;
using Wpf.Ui.Controls;
using static AresToys.App.Services.Zones.ZoneNative;
using ContextMenu = System.Windows.Controls.ContextMenu;
using TextBox = System.Windows.Controls.TextBox;

namespace AresToys.App.Views;

/// <summary>Window for editing the window snapping layouts: pick or create a layout, split and merge zones on a live
/// preview, drag boundaries with snapping, type exact numbers, undo and redo, assign the layout to a monitor.
/// The window works on one layout at a time: Save writes the layout, a click on a layout card assigns it to the selected
/// monitor at once, and neither closes the window, so several monitors can be set up in one session. Every layout change goes through <see cref="ZoneGridEditor"/> (or
/// <see cref="ZoneCanvasEditor"/>) and the <see cref="ZoneEditHistory{T}"/>.</summary>
public sealed partial class ZoneEditorWindow : FluentWindow
{
    private const int DefaultSpacing = 16;
    private const int MaxSpacing = 100;
    private const int MaxSensitivity = 200;
    private const uint SwpNoSize = 0x0001;
    private const int CenterAttempts = 3;

    private readonly ZoneLayoutStore _store;
    private readonly ZoneMonitors _zoneMonitors;
    private readonly IReadOnlyList<ZoneMonitor> _monitors;
    private readonly ZoneMonitor? _launchMonitor;
    private readonly bool _spanned;
    private ZoneMonitor _monitor;
    // Grid layouts edit through _history. In canvas mode (free-form zones) _canvasHistory is set and _history only holds
    // a one-zone placeholder that nothing reads or pushes to: every read of the grid goes through IsCanvas first.
    private ZoneEditHistory<GridLayout> _history = null!;
    private GridLayout _loadedGrid = null!;
    private ZoneEditHistory<CanvasLayout>? _canvasHistory;
    private CanvasLayout? _loadedCanvas;
    private string _id = string.Empty;
    private string _name = string.Empty;
    private string _loadedName = string.Empty;
    private bool _isPending;
    private bool _suppress;
    private bool _closing;
    private bool _busy;
    private bool _applying;
    private readonly ISettingsStore? _settings;

    internal const string WidthKey = "windowsnapping.editor.width";
    internal const string HeightKey = "windowsnapping.editor.height";

    /// <summary>Reads the size the editor was last closed at; null when none is saved yet.</summary>
    internal static async Task<Size?> LoadSizeAsync(ISettingsStore settings, CancellationToken ct)
    {
        var w = await settings.GetAsync(WidthKey, ct);
        var h = await settings.GetAsync(HeightKey, ct);
        if (double.TryParse(w, NumberStyles.Float, CultureInfo.InvariantCulture, out var width)
            && double.TryParse(h, NumberStyles.Float, CultureInfo.InvariantCulture, out var height)
            && width > 0 && height > 0)
            return new Size(width, height);
        return null;
    }

    // Applies the saved size before PositionOn and CenterOn read Width and Height, kept between the minimum and the
    // work area of the monitor the window opens on.
    private void ApplySavedSize(Size? saved, ZoneMonitor? monitor)
    {
        if (saved is not { } size) return;
        double maxW = double.PositiveInfinity, maxH = double.PositiveInfinity;
        if (monitor is not null)
        {
            var scale = monitor.DpiScale > 0 ? monitor.DpiScale : 1.0;
            maxW = monitor.WorkArea.Width / scale;
            maxH = monitor.WorkArea.Height / scale;
        }

        Width = Math.Min(Math.Max(size.Width, MinWidth), Math.Max(maxW, MinWidth));
        Height = Math.Min(Math.Max(size.Height, MinHeight), Math.Max(maxH, MinHeight));
    }

    // Live preview: typing in a number box restarts a short timer; when it fires the typed value is applied once, so a
    // pause in typing is one undo step. _writingBox marks the code's own text writes, which must not start the timer.
    private const int LiveApplyDelayMs = 150;
    private readonly DispatcherTimer _liveTimer;
    private Action? _liveApply;
    private bool _liveApplying;
    private bool _writingBox;
    private bool _writingName;
    private readonly int[] _shownFields = new int[4];
    private MonitorIdentifier? _identifier;

    /// <param name="monitors">The monitor set; with spanning on, the "Use on" tiles hold one entry for all monitors.</param>
    /// <param name="launchMonitor">The real display the window opens centered on (and, when spanning is off, the
    /// monitor tile preselected in "Use on"). Null keeps the window centered on the screen.</param>
    internal ZoneEditorWindow(ZoneLayoutStore store, ZoneMonitors monitors, ZoneAppearance appearance, string? initialLayoutId,
        ISettingsStore? settings = null, Size? savedSize = null, ZoneMonitor? launchMonitor = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(monitors);
        ArgumentNullException.ThrowIfNull(appearance);
        _store = store;
        _zoneMonitors = monitors;
        _launchMonitor = launchMonitor;
        _liveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(LiveApplyDelayMs) };
        _liveTimer.Tick += OnLiveTimerTick;
        _settings = settings;
        InitializeComponent();
        ApplySavedSize(savedSize, launchMonitor);
        Preview.Appearance = appearance;
        LayoutStrip.SetAppearance(appearance);
        if (launchMonitor is not null) PositionOn(launchMonitor);

        var found = monitors.All();
        _monitors = found.Count > 0 ? found : [new ZoneMonitor(string.Empty, new PixelRect(0, 0, 1920, 1080), 1.0)];
        _spanned = _monitors.Count == 1 && _monitors[0].Id == ZoneMonitors.SpanId;
        MonitorStrip.SetMonitors(_monitors);
        var initial = initialLayoutId is null ? null : store.Find(initialLayoutId);
        int monitorIndex = 0;
        if (launchMonitor is not null && !_spanned)
        {
            var launched = _monitors.ToList().FindIndex(m => m.Id == launchMonitor.Id);
            if (launched >= 0) monitorIndex = launched;
        }
        else if (initial is not null)
        {
            for (int i = 0; i < _monitors.Count; i++)
            {
                if (store.GetAssignment(_monitors[i].Id) == initial.Id)
                {
                    monitorIndex = i;
                    break;
                }
            }
        }

        _monitor = _monitors[monitorIndex];
        initial ??= AssignedLayout(_monitor);
        MonitorStrip.SelectMonitor(_monitor.Id);
        Preview.SetWorkArea(_monitor.WorkArea.Width, _monitor.WorkArea.Height);
        LayoutStrip.SetMonitorAspect(CardAspect(_monitor));
        LayoutStrip.SelectionChanged += OnLayoutStripSelectionChanged;
        Preview.SelectionChanged += (_, _) => RefreshSelectionPanel();
        Preview.EditCommitted += OnPreviewEditCommitted;
        Preview.CanvasEditCommitted += OnPreviewCanvasEditCommitted;
        Preview.Notice += (_, text) => SetNote(text);
        Preview.PreviewMouseLeftButtonDown += (_, _) => CommitPendingEdits();
        LoadOrNew(initial);
        RebuildLayoutBox();
    }

    private static double MonitorAspect(ZoneMonitor monitor) => monitor.WorkArea.Width / (double)monitor.WorkArea.Height;

    /// <summary>The aspect the layout cards are drawn with: always landscape, so a portrait monitor keeps landscape miniatures.</summary>
    private static double CardAspect(ZoneMonitor monitor)
    {
        var aspect = MonitorAspect(monitor);
        return Math.Max(aspect, 1 / aspect);
    }

    // The templates come from resx files and are formatted rarely, so a cached CompositeFormat buys nothing.
    private static string Fmt(string template, params object[] args)
    {
#pragma warning disable CA1863
        return string.Format(CultureInfo.CurrentCulture, template, args);
#pragma warning restore CA1863
    }

    /// <summary>Sets the start position in WPF units from the monitor's work area and DPI scale, centered and kept on
    /// the monitor. WPF converts those units with a DPI that may not be the monitor's, so <see cref="OnSourceInitialized"/>
    /// then corrects the position in device pixels.</summary>
    private void PositionOn(ZoneMonitor monitor)
    {
        var scale = monitor.DpiScale > 0 ? monitor.DpiScale : 1.0;
        var area = monitor.WorkArea;
        double left = area.Left / scale;
        double top = area.Top / scale;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = left + Math.Max(0, (area.Width / scale - Width) / 2);
        Top = top + Math.Max(0, (area.Height / scale - Height) / 2);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (_launchMonitor is { } monitor) CenterOn(monitor.WorkArea);
    }

    /// <summary>Centers the window on an area given in device pixels (kept inside it when the window is smaller).
    /// A move to a monitor with another DPI makes WPF resize the window, so the position is checked and applied again.</summary>
    private void CenterOn(PixelRect area)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        for (int attempt = 0; attempt < CenterAttempts; attempt++)
        {
            if (!GetWindowRect(hwnd, out var rect)) return;
            int left = area.Left + Math.Max(0, (area.Width - (rect.Right - rect.Left)) / 2);
            int top = area.Top + Math.Max(0, (area.Height - (rect.Bottom - rect.Top)) / 2);
            if (rect.Left == left && rect.Top == top) return;
            SetWindowPos(hwnd, 0, left, top, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
        }
    }

    /// <summary>The layout a monitor uses in the store, or the first saved layout when it has none (the engine's own
    /// rule); null when no layout is saved at all.</summary>
    private ZoneLayout? AssignedLayout(ZoneMonitor monitor) => ZoneDragService.ResolveLayout(_store, monitor.Id);

    /// <summary>The one zone grid an empty new layout starts from (the Empty menu item and the empty store share it).</summary>
    private static GridLayout OneZoneGrid() => ZoneLayoutPresets.Columns(1) with { Spacing = DefaultSpacing };

    /// <summary>The unsaved layout the editor opens on when nothing is saved: one empty zone under the default name.</summary>
    private static ZoneLayout NewEmptyLayout()
        => new(NewId(), Strings.WindowSnapping_NewLayoutName, OneZoneGrid());

    /// <summary>Shows a saved layout, or (for null, when the store holds none) a pending new one.</summary>
    private void LoadOrNew(ZoneLayout? saved)
    {
        if (saved is null) LoadLayout(NewEmptyLayout(), true);
        else LoadLayout(saved, false);
    }

    // With spanning on every real monitor shows the layout of the one combined monitor, which is what is in force on it.
    // The layout being edited shows its edited name.
    private string LayoutNameFor(ZoneMonitor monitor)
    {
        var key = _spanned ? ZoneMonitors.SpanId : monitor.Id;
        if (_store.GetAssignment(key) is not { } assigned) return Strings.WindowSnapping_NoLayoutAssigned;
        if (!_isPending && assigned == _id) return CardName;
        return _store.Find(assigned) is { } layout ? layout.Name : Strings.WindowSnapping_NoLayoutAssigned;
    }

    private void OnIdentifyClicked(object sender, RoutedEventArgs e)
    {
        _identifier ??= new MonitorIdentifier();

        // The identify windows always number the real displays, also while zones span all of them.
        _identifier.Show(_zoneMonitors.Real().Select((m, i) => new MonitorIdentity(m, i + 1, LayoutNameFor(m))));
    }

    // ---- state ----

    /// <summary>True when the layout is free-form (canvas) instead of a grid.</summary>
    private bool IsCanvas => _canvasHistory is not null;

    private bool GridChanged => !IsCanvas && !ZoneGridEditor.SameLayout(_history.Current, _loadedGrid);

    /// <summary>True when the free-form zones differ from the saved ones; always true for a grid layout converted in place
    /// (nothing saved to compare with).</summary>
    private bool CanvasChanged => _canvasHistory is { } history && !ZoneCanvasEditor.SameLayout(history.Current, _loadedCanvas);

    /// <summary>True when the zones or the sensitivity of the layout differ from what was loaded, grid or canvas.</summary>
    private bool GeometryChanged => IsCanvas ? CanvasChanged : GridChanged;

    /// <summary>True when the layout in the editor has unsaved changes (a new layout, zones or a name).</summary>
    private bool LayoutEditsPending => _isPending || GeometryChanged || _name != _loadedName;

    /// <summary>True when the name field holds nothing but white space: a layout needs a name to be saved or applied.</summary>
    private bool NameBlank => string.IsNullOrWhiteSpace(_name);

    /// <summary>The name the cards and the monitor tiles show: the typed one, or the default while the field is blank.</summary>
    private string CardName => NameBlank ? Strings.WindowSnapping_NewLayoutName : _name;

    private void LoadLayout(ZoneLayout layout, bool pending)
    {
        _id = layout.Id;
        _name = _loadedName = layout.Name;
        _isPending = pending;
        StopLiveApply();

        // A canvas layout never exposes its Grid (it is the invalid NoGrid placeholder), so the grid history gets a
        // one-zone stand-in that stays unused while the canvas history is set.
        _loadedGrid = layout.Canvas is null ? layout.Grid : ZoneLayoutPresets.Columns(1);
        _loadedCanvas = layout.Canvas;
        AttachHistories(_loadedGrid, layout.Canvas);
        Preview.SetSelection([]);

        // Split needs a grid and Draw needs free-form zones: a tool the loaded kind cannot use falls back to Select.
        if (Preview.Tool == (IsCanvas ? ZoneEditTool.Split : ZoneEditTool.Draw)) SetTool(ZoneEditTool.Select);
        SetNote(null);
        RefreshAll();
    }

    /// <summary>Replaces the edit histories: the grid one always, the canvas one only for a free-form layout.</summary>
    private void AttachHistories(GridLayout grid, CanvasLayout? canvas)
    {
        if (_history is not null) _history.Changed -= OnHistoryChanged;
        _history = new ZoneEditHistory<GridLayout>(grid, ZoneGridEditor.SameLayout);
        _history.Changed += OnHistoryChanged;
        AttachCanvasHistory(canvas);
    }

    private void AttachCanvasHistory(CanvasLayout? canvas)
    {
        if (_canvasHistory is not null) _canvasHistory.Changed -= OnHistoryChanged;
        _canvasHistory = canvas is null ? null : new ZoneEditHistory<CanvasLayout>(canvas, ZoneCanvasEditor.SameLayout);
        if (_canvasHistory is not null) _canvasHistory.Changed += OnHistoryChanged;
    }

    private void OnHistoryChanged(object? sender, EventArgs e) => RefreshAll();

    /// <summary>The layout in the editor as a store layout: free-form when canvas mode is on, a grid otherwise.</summary>
    private ZoneLayout CurrentLayout(string id, string name)
        => _canvasHistory is { } canvas
            ? ZoneLayout.ForCanvas(id, name, canvas.Current)
            : new ZoneLayout(id, name, _history.Current);

    private void RebuildLayoutBox()
    {
        // The layout being edited shows its edited name and zones; a pending one is not in the store, so it goes last.
        var layouts = new List<ZoneLayout>();
        foreach (var layout in _store.Layouts)
        {
            layouts.Add(!_isPending && layout.Id == _id ? CurrentLayout(_id, CardName) : layout);
        }

        if (_isPending) layouts.Add(CurrentLayout(_id, CardName));
        _suppress = true;
        LayoutStrip.SetLayouts(layouts, _id);
        _suppress = false;
    }

    private void RefreshAll()
    {
        // Canvas mode: the preview must hold the new layout before this returns (it selects a drawn zone right after).
        if (_canvasHistory is { } canvas)
        {
            Preview.SetCanvasLayout(canvas.Current);
            ShowText(SpacingBox, string.Empty);
            ShowText(SensitivityBox, canvas.Current.SensitivityRadius.ToString(CultureInfo.CurrentCulture));
            UndoButton.IsEnabled = canvas.CanUndo;
            RedoButton.IsEnabled = canvas.CanRedo;
        }
        else
        {
            Preview.SetLayout(_history.Current);
            ShowText(SpacingBox, _history.Current.Spacing.ToString(CultureInfo.CurrentCulture));
            ShowText(SensitivityBox, _history.Current.SensitivityRadius.ToString(CultureInfo.CurrentCulture));
            UndoButton.IsEnabled = _history.CanUndo;
            RedoButton.IsEnabled = _history.CanRedo;
        }

        // Free-form layouts have no spacing and cannot be split or merged.
        SpacingBox.IsEnabled = !IsCanvas;
        SplitToolButton.IsEnabled = !IsCanvas;

        // The card of the layout being edited follows the edits (a no-op until the strip holds that layout).
        if (_id.Length > 0) LayoutStrip.UpdateLayout(CurrentLayout(_id, CardName));
        ShowName();
        RefreshSelectionPanel();
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        // A pending layout is not in the store yet: it is only saved (and named) by Save.
        DeleteButton.IsEnabled = !_isPending;
        // A free-form layout with no zone cannot be saved (the store validates it): Save stays off until one is drawn.
        // A blank name keeps it off too, a layout needs a name.
        bool canWrite = !_busy && !NameBlank && CurrentZoneCount > 0;
        SaveButton.IsEnabled = canWrite && LayoutEditsPending;
    }

    /// <summary>Shows the layout name in the field when it differs (a no-op while the user types it).</summary>
    private void ShowName()
    {
        if (NameBox.Text == _name) return;
        _writingName = true;
        try
        {
            NameBox.Text = _name;
        }
        finally
        {
            _writingName = false;
        }
    }

    /// <summary>The name field changed by typing: the card follows live and the buttons follow the edit.</summary>
    private void OnNameTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_writingName) return;
        _name = NameBox.Text;
        if (_id.Length > 0) LayoutStrip.UpdateLayout(CurrentLayout(_id, CardName));
        UpdateButtons();
    }

    private void OnNameKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        e.Handled = true;
        System.Windows.Input.Keyboard.ClearFocus();
    }

    private void SetNote(string? text) => NoteText.Text = text ?? string.Empty;

    private void ShowError(string message)
        => System.Windows.MessageBox.Show(this, message, "AresToys", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);

    private bool ConfirmDiscard()
        => System.Windows.MessageBox.Show(this, Strings.WindowSnapping_DiscardMessage, "AresToys",
            System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question) == System.Windows.MessageBoxResult.Yes;

    // ---- layout picker and layout commands ----

    /// <summary>A card was clicked: the layout is shown and, at once, becomes the one the selected monitor uses.</summary>
    private async void OnLayoutStripSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || _busy || LayoutStrip.SelectedLayoutId is not { } id || id == _id) return;
        CommitPendingEdits();
        if (LayoutEditsPending && !ConfirmDiscard())
        {
            LayoutStrip.SelectLayout(_id);
            return;
        }

        if (_store.Find(id) is not { } layout) return;
        LoadLayout(layout, false);
        RebuildLayoutBox();
        UpdateButtons();
        await RunBusyAsync(() => _store.SetAssignmentAsync(_monitor.Id, layout.Id, CancellationToken.None));
    }

    private void OnNewClicked(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = NewButton, Placement = PlacementMode.Bottom };
        var empty = new System.Windows.Controls.MenuItem { Header = Strings.WindowSnapping_NewEmpty };
        empty.Click += (_, _) => StartNew(Strings.WindowSnapping_NewLayoutName, OneZoneGrid(), true);
        menu.Items.Add(empty);
        var custom = new System.Windows.Controls.MenuItem { Header = Strings.WindowSnapping_NewCustomGrid };
        custom.Click += (_, _) => OnCustomGridChosen();
        menu.Items.Add(custom);
        var freeForm = new System.Windows.Controls.MenuItem { Header = Strings.WindowSnapping_NewFreeForm };
        freeForm.Click += (_, _) => OnFreeFormChosen();
        menu.Items.Add(freeForm);

        menu.IsOpen = true;
    }

    /// <summary>Starts an unsaved free-form layout with no zone and the Draw tool on, so the user can draw at once.</summary>
    private void OnFreeFormChosen()
    {
        CommitPendingEdits();
        if (LayoutEditsPending && !ConfirmDiscard()) return;
        StartNewLayout(ZoneLayout.ForCanvas(NewId(), Strings.WindowSnapping_NewLayoutName, new CanvasLayout([])), false);
        SetTool(ZoneEditTool.Draw);
        SetNote(Strings.WindowSnapping_NoteDrawFirst);
    }

    private void OnCustomGridChosen()
    {
        var dialog = new ZoneGridSizeDialog { Owner = this };
        if (dialog.ShowDialog() != true) return;
        StartNew(Fmt(Strings.WindowSnapping_GridNameFormat, dialog.Rows, dialog.Columns),
            ZoneLayoutPresets.GridOf(dialog.Rows, dialog.Columns), true);
    }

    private void OnDuplicateClicked(object sender, RoutedEventArgs e)
    {
        CommitPendingEdits();
        StartNewLayout(CurrentLayout(NewId(), Fmt(Strings.WindowSnapping_CopyFormat, CardName)), false);
    }

    private void StartNew(string name, GridLayout grid, bool confirmDiscard)
        => StartNewLayout(new ZoneLayout(NewId(), name, grid), confirmDiscard);

    /// <summary>Starts an unsaved layout. A copy carries the current edits, so only a fresh start asks about discarding.</summary>
    private void StartNewLayout(ZoneLayout layout, bool confirmDiscard)
    {
        CommitPendingEdits();
        if (confirmDiscard && LayoutEditsPending && !ConfirmDiscard()) return;
        LoadLayout(layout, true);
        RebuildLayoutBox();
    }

    private static string NewId() => "user-" + Guid.NewGuid().ToString("N")[..8];

    private async void OnDeleteClicked(object sender, RoutedEventArgs e)
    {
        if (_isPending) return;
        var answer = System.Windows.MessageBox.Show(this,
            Fmt(Strings.WindowSnapping_DeleteConfirmFormat, _name), "AresToys",
            System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
        if (answer != System.Windows.MessageBoxResult.Yes) return;

        try
        {
            await _store.DeleteLayoutAsync(_id, CancellationToken.None);
        }
        catch (Exception ex)
        {
            ShowError(Fmt(Strings.WindowSnapping_DeleteErrorFormat, ex.Message));
            return;
        }

        // The store drops the assignments of the deleted layout itself.
        LoadOrNew(AssignedLayout(_monitor));
        RebuildLayoutBox();
    }

    // ---- settings and monitor ----

    private void OnSettingKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitPendingEdits();
            e.Handled = true;
        }
    }

    private void OnSettingLostFocus(object sender, RoutedEventArgs e) => CommitPendingEdits();

    private void ApplySettings()
    {
        if (_applying) return;
        if (_canvasHistory is { } canvasHistory)
        {
            // Free-form layouts have no spacing: only the sensitivity radius is a setting.
            var c = canvasHistory.Current;
            if (!TryReadNumber(SensitivityBox, c.SensitivityRadius, 0, MaxSensitivity, out int canvasRadius)) canvasRadius = c.SensitivityRadius;
            if (canvasRadius != c.SensitivityRadius) canvasHistory.Push(c with { SensitivityRadius = canvasRadius });
            else ShowText(SensitivityBox, c.SensitivityRadius.ToString(CultureInfo.CurrentCulture));
            return;
        }

        var g = _history.Current;
        if (!TryReadNumber(SpacingBox, g.Spacing, 0, MaxSpacing, out int spacing)) spacing = g.Spacing;
        if (!TryReadNumber(SensitivityBox, g.SensitivityRadius, 0, MaxSensitivity, out int radius)) radius = g.SensitivityRadius;
        if (spacing != g.Spacing || radius != g.SensitivityRadius)
        {
            // Spacing and radius are plain numbers of the layout, not geometry, so ZoneGridEditor has no operation
            // for them; the history still records the change so it can be undone.
            _history.Push(g with { Spacing = spacing, SensitivityRadius = radius });
        }
        else
        {
            ShowText(SpacingBox, g.Spacing.ToString(CultureInfo.CurrentCulture));
            ShowText(SensitivityBox, g.SensitivityRadius.ToString(CultureInfo.CurrentCulture));
        }
    }

    /// <summary>Parses a whole number and clamps it. Says so in the note when the text was not a number or had to be limited.</summary>
    private bool TryReadNumber(TextBox box, int current, int min, int max, out int value)
    {
        value = current;
        var text = box.Text.Trim();
        if (text.Length == 0 || text == current.ToString(CultureInfo.CurrentCulture)) return true;
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out int parsed))
        {
            SetNote(Strings.WindowSnapping_NoteInvalidNumber);
            return false;
        }

        value = Math.Clamp(parsed, min, max);
        SetNote(value != parsed ? Fmt(Strings.WindowSnapping_NoteRangeFormat, min, max) : null);
        return true;
    }

    /// <summary>A monitor tile was chosen: the editor shows the layout that monitor is set to use. Edits to the layout
    /// in progress are asked about first.</summary>
    private void OnMonitorSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MonitorStrip.SelectedMonitor is not { } monitor || monitor.Id == _monitor.Id) return;
        CommitPendingEdits();
        if (LayoutEditsPending && !ConfirmDiscard())
        {
            MonitorStrip.SelectMonitor(_monitor.Id);
            return;
        }

        _monitor = monitor;
        Preview.SetWorkArea(_monitor.WorkArea.Width, _monitor.WorkArea.Height);
        LayoutStrip.SetMonitorAspect(CardAspect(_monitor));
        LoadOrNew(AssignedLayout(_monitor));
        RebuildLayoutBox();
    }

    // ---- tools ----

    private void OnSelectToolClicked(object sender, RoutedEventArgs e) => SetTool(ZoneEditTool.Select);

    private void OnSplitToolClicked(object sender, RoutedEventArgs e) => SetTool(ZoneEditTool.Split);

    private void OnDrawToolClicked(object sender, RoutedEventArgs e) => SetTool(ZoneEditTool.Draw);

    private void SetTool(ZoneEditTool tool)
    {
        // Draw needs free-form zones: a grid layout is converted first, and when the user declines the tool stays as it was.
        if (tool == ZoneEditTool.Draw && !IsCanvas && !ConvertToCanvas()) tool = Preview.Tool;
        Preview.Tool = tool;
        SelectToolButton.IsChecked = tool == ZoneEditTool.Select;
        SplitToolButton.IsChecked = tool == ZoneEditTool.Split;
        DrawToolButton.IsChecked = tool == ZoneEditTool.Draw;
        HintText.Text = tool switch
        {
            ZoneEditTool.Split => Strings.WindowSnapping_HintSplit,
            ZoneEditTool.Draw => Strings.WindowSnapping_HintDraw,
            _ => string.Empty,
        };
        SetNote(null);
    }

    /// <summary>Asks, then turns the grid layout in the editor into free-form zones (one rectangle per grid zone), in
    /// place. The conversion is not part of the undo history. Returns false when the user declined.</summary>
    private bool ConvertToCanvas()
    {
        if (System.Windows.MessageBox.Show(this, Strings.WindowSnapping_ConvertMessage, "AresToys",
                System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question) != System.Windows.MessageBoxResult.Yes)
        {
            return false;
        }

        CommitPendingEdits();
        var canvas = ZoneCanvasEditor.FromGrid(_history.Current);
        // Nothing saved to compare with, so the layout counts as changed until it is saved as free-form.
        _loadedCanvas = null;
        AttachCanvasHistory(canvas);
        Preview.SetSelection([]);
        SetNote(null);
        RefreshAll();
        RebuildLayoutBox();
        return true;
    }

    private void OnMergeClicked(object sender, RoutedEventArgs e) => MergeZones(Preview.Selection.ToList());

    private void OnUndoClicked(object sender, RoutedEventArgs e) => Undo();

    private void OnRedoClicked(object sender, RoutedEventArgs e) => Redo();

    private void Undo()
    {
        if (_canvasHistory is { } canvas)
        {
            if (!canvas.CanUndo) return;
            canvas.Undo();
        }
        else
        {
            if (!_history.CanUndo) return;
            _history.Undo();
        }

        SetNote(null);
    }

    private void Redo()
    {
        if (_canvasHistory is { } canvas)
        {
            if (!canvas.CanRedo) return;
            canvas.Redo();
        }
        else
        {
            if (!_history.CanRedo) return;
            _history.Redo();
        }

        SetNote(null);
    }

    private void OnPreviewEditCommitted(object? sender, GridLayout layout)
    {
        _history.Push(layout);
        SetNote(null);
    }

    // The history's Changed handler (RefreshAll) hands the new layout back to the preview before this returns.
    private void OnPreviewCanvasEditCommitted(object? sender, CanvasLayout layout)
    {
        _canvasHistory?.Push(layout);
        SetNote(null);
    }

    /// <summary>Delete key in canvas mode: removes the selected zone.</summary>
    private void RemoveSelectedCanvasZone(ZoneEditHistory<CanvasLayout> history)
    {
        if (Preview.Selection.Count != 1) return;
        var result = ZoneCanvasEditor.RemoveZone(history.Current, Preview.Selection.First());
        if (!result.Changed)
        {
            SetNote(result.Reason);
            return;
        }

        history.Push(result.Layout);
        SetNote(null);
    }

    private void MergeZones(IReadOnlyCollection<int> zones)
    {
        if (IsCanvas) return;
        CommitPendingEdits();
        if (zones.Count < 2) return;
        var g = _history.Current;
        var first = ZoneGridEditor.ZoneBounds(g, zones.First());
        var result = ZoneGridEditor.Merge(g, zones);
        if (!result.Changed)
        {
            SetNote(result.Reason);
            return;
        }

        _history.Push(result.Layout);
        SetNote(null);
        int merged = ZonePreviewCanvas.ZoneAt(result.Layout, (first.Left + first.Right) / 2.0, (first.Top + first.Bottom) / 2.0);
        Preview.SetSelection(merged >= 0 ? [merged] : []);
        RefreshSelectionPanel();
    }

    /// <summary>Delete key: merges the only selected zone with its neighbor on the right, else below.</summary>
    private void MergeWithNeighbor()
    {
        if (Preview.Selection.Count != 1) return;
        int zone = Preview.Selection.First();
        var g = _history.Current;
        int? neighbor = FindNeighbor(g, zone, ZoneAxis.Vertical) ?? FindNeighbor(g, zone, ZoneAxis.Horizontal);
        if (neighbor is null)
        {
            SetNote(Strings.WindowSnapping_NoteNoNeighbor);
            return;
        }

        MergeZones([zone, neighbor.Value]);
    }

    /// <summary>The zone touching the right (Vertical) or bottom (Horizontal) edge of a zone; one with the same
    /// extent is preferred because merging it keeps the shape rectangular.</summary>
    private static int? FindNeighbor(GridLayout g, int zone, ZoneAxis side)
    {
        var a = ZoneGridEditor.ZoneBounds(g, zone);
        int? any = null;
        for (int z = 0; z < g.ZoneCount; z++)
        {
            if (z == zone) continue;
            var b = ZoneGridEditor.ZoneBounds(g, z);
            bool touches;
            bool exact;
            if (side == ZoneAxis.Vertical)
            {
                touches = b.Left == a.Right && b.Top < a.Bottom && b.Bottom > a.Top;
                exact = b.Top == a.Top && b.Bottom == a.Bottom;
            }
            else
            {
                touches = b.Top == a.Bottom && b.Left < a.Right && b.Right > a.Left;
                exact = b.Left == a.Left && b.Right == a.Right;
            }

            if (!touches) continue;
            if (exact) return z;
            any ??= z;
        }

        return any;
    }

    // ---- selected zone numbers ----

    private int WorkWidth => Math.Max(1, _monitor.WorkArea.Width);

    private int WorkHeight => Math.Max(1, _monitor.WorkArea.Height);

    private static int ToPixels(int percent, int size) => (int)Math.Round(percent * (double)size / GridLayout.PercentTotal);

    private static int ToPercent(int pixels, int size) => Math.Clamp((int)Math.Round(pixels * (double)GridLayout.PercentTotal / size), 0, GridLayout.PercentTotal);

    private int CurrentZoneCount => _canvasHistory?.Current.ZoneCount ?? _history.Current.ZoneCount;

    /// <summary>Bounds of a zone of the layout in the editor, in 1/10000 of the work area, grid or canvas.</summary>
    private (int Left, int Top, int Right, int Bottom) CurrentZoneBounds(int zone)
    {
        if (_canvasHistory is { } canvas)
        {
            var z = canvas.Current.Zones[zone];
            return (z.Left, z.Top, z.Right, z.Bottom);
        }

        return ZoneGridEditor.ZoneBounds(_history.Current, zone);
    }

    private void RefreshSelectionPanel()
    {
        var selection = Preview.Selection;
        MergeButton.IsEnabled = !IsCanvas && selection.Count >= 2;
        _applying = true;
        try
        {
            if (selection.Count == 1)
            {
                int zone = selection.First();
                var b = CurrentZoneBounds(zone);
                int left = ToPixels(b.Left, WorkWidth);
                int top = ToPixels(b.Top, WorkHeight);
                int right = ToPixels(b.Right, WorkWidth);
                int bottom = ToPixels(b.Bottom, WorkHeight);

                // A grid keeps its outer edges fixed; a free-form zone can be typed anywhere.
                SetField(LeftBox, 0, left, IsCanvas || b.Left > 0);
                SetField(TopBox, 1, top, IsCanvas || b.Top > 0);
                SetField(WidthBox, 2, right - left, IsCanvas || b.Left > 0 || b.Right < GridLayout.PercentTotal);
                SetField(HeightBox, 3, bottom - top, IsCanvas || b.Top > 0 || b.Bottom < GridLayout.PercentTotal);
            }
            else
            {
                foreach (var box in new[] { LeftBox, TopBox, WidthBox, HeightBox })
                {
                    ShowText(box, string.Empty);
                    box.IsEnabled = false;
                }
            }
        }
        finally
        {
            _applying = false;
        }
    }

    private void SetField(TextBox box, int slot, int value, bool enabled)
    {
        _shownFields[slot] = value;
        ShowText(box, value.ToString(CultureInfo.CurrentCulture));
        box.IsEnabled = enabled;
    }

    private void OnFieldKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitPendingEdits();
            e.Handled = true;
        }
    }

    private void OnFieldLostFocus(object sender, RoutedEventArgs e) => CommitPendingEdits();

    private void ApplyZoneFields()
    {
        if (_applying || Preview.Selection.Count != 1) return;
        var boxes = new[] { LeftBox, TopBox, WidthBox, HeightBox };
        var typed = new int[4];
        var changed = new bool[4];
        for (int i = 0; i < 4; i++)
        {
            var text = boxes[i].Text.Trim();
            if (!boxes[i].IsEnabled || text == _shownFields[i].ToString(CultureInfo.CurrentCulture))
            {
                typed[i] = _shownFields[i];
                continue;
            }

            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out typed[i]))
            {
                SetNote(Strings.WindowSnapping_NoteInvalidNumber);
                RefreshSelectionPanel();
                return;
            }

            changed[i] = true;
        }

        if (!changed.Any(c => c)) return;

        int zone = Preview.Selection.First();
        var b = CurrentZoneBounds(zone);
        int curL = ToPixels(b.Left, WorkWidth), curT = ToPixels(b.Top, WorkHeight);
        int curR = ToPixels(b.Right, WorkWidth), curB = ToPixels(b.Bottom, WorkHeight);
        int l = curL, t = curT, r = curR, bt = curB;
        if (changed[0]) l = typed[0];
        if (changed[1]) t = typed[1];
        if (changed[2])
        {
            if (b.Right < GridLayout.PercentTotal) r = l + typed[2];
            else l = r - typed[2];
        }

        if (changed[3])
        {
            if (b.Bottom < GridLayout.PercentTotal) bt = t + typed[3];
            else t = bt - typed[3];
        }

        int? newL = l != curL ? ToPercent(l, WorkWidth) : null;
        int? newT = t != curT ? ToPercent(t, WorkHeight) : null;
        int? newR = r != curR ? ToPercent(r, WorkWidth) : null;
        int? newB = bt != curB ? ToPercent(bt, WorkHeight) : null;

        // Both kinds report the same outcome: whether it changed, the refusal reason, where the zone landed, and the push.
        bool changedLayout;
        string? reason;
        (int Left, int Top, int Right, int Bottom) landed;
        Action push;
        if (_canvasHistory is { } canvasHistory)
        {
            var result = ZoneCanvasEditor.SetZoneEdges(canvasHistory.Current, zone, newL, newT, newR, newB);
            var z = result.Layout.Zones[zone];
            changedLayout = result.Changed;
            reason = result.Reason;
            landed = (z.Left, z.Top, z.Right, z.Bottom);
            push = () => canvasHistory.Push(result.Layout);
        }
        else
        {
            var result = ZoneGridEditor.SetZoneEdges(_history.Current, zone, newL, newT, newR, newB);
            changedLayout = result.Changed;
            reason = result.Reason;
            landed = ZoneGridEditor.ZoneBounds(result.Layout, zone);
            push = () => _history.Push(result.Layout);
        }

        bool clamped = (l != curL && ToPixels(landed.Left, WorkWidth) != l)
            || (t != curT && ToPixels(landed.Top, WorkHeight) != t)
            || (r != curR && ToPixels(landed.Right, WorkWidth) != r)
            || (bt != curB && ToPixels(landed.Bottom, WorkHeight) != bt);
        SetNote(reason ?? (clamped ? Strings.WindowSnapping_NoteClamped : null));

        if (changedLayout) push();
        else RefreshSelectionPanel();
    }

    /// <summary>Applies what is typed in the settings and number boxes, so a click elsewhere never loses it. The boxes
    /// then show the corrected values, also the one that has the focus.</summary>
    private void CommitPendingEdits()
    {
        StopLiveApply();
        ApplySettings();
        ApplyZoneFields();
    }

    // ---- live preview while typing ----

    /// <summary>A number box changed by typing: after a short pause the typed value is applied to the layout, so the preview
    /// follows without leaving the box. Text the code wrote itself, and text that is not a whole number yet, are ignored.</summary>
    private void OnLiveTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_writingBox || _applying || sender is not TextBox box) return;
        if (!int.TryParse(box.Text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out _)) return;

        _liveApply = ReferenceEquals(box, SpacingBox) || ReferenceEquals(box, SensitivityBox) ? ApplySettings : ApplyZoneFields;
        _liveTimer.Stop();
        _liveTimer.Start();
    }

    private void OnLiveTimerTick(object? sender, EventArgs e)
    {
        var apply = _liveApply;
        StopLiveApply();
        if (apply is null) return;

        _liveApplying = true;
        try
        {
            apply();
        }
        finally
        {
            _liveApplying = false;
        }
    }

    private void StopLiveApply()
    {
        _liveTimer.Stop();
        _liveApply = null;
    }

    /// <summary>The one place the number boxes are written by code. While a live apply runs, the box the user is typing in
    /// is left alone (rewriting it would move the caret and fight the typing); its corrected text is written when the
    /// edit is committed. An unchanged text is not rewritten.</summary>
    private void ShowText(TextBox box, string text)
    {
        if (box.Text == text || (_liveApplying && box.IsKeyboardFocusWithin)) return;
        _writingBox = true;
        try
        {
            box.Text = text;
        }
        finally
        {
            _writingBox = false;
        }
    }

    // ---- saving and closing ----

    private async void OnSaveClicked(object sender, RoutedEventArgs e) => await SaveAsync();

    /// <summary>Saves the layout in the editor and keeps the window open. A new layout is also assigned to the selected
    /// monitor on its first save; saving an existing layout leaves the assignments alone.</summary>
    private async Task SaveAsync()
    {
        if (_busy) return;
        CommitPendingEdits();
        if (!LayoutEditsPending || NameToSave() is not { } name) return;

        bool wasPending = _isPending;
        await RunBusyAsync(async () =>
        {
            await WriteLayoutAsync(name);
            if (wasPending) await _store.SetAssignmentAsync(_monitor.Id, _id, CancellationToken.None);
            SetNote(Strings.WindowSnapping_NoteSaved);
        });
    }

    /// <summary>The name a layout is saved under: the trimmed one in the name field. Null when it is blank.</summary>
    private string? NameToSave() => NameBlank ? null : _name.Trim();

    /// <summary>Writes the layout in the editor and reloads it as the saved baseline (no edits pending, history reset).</summary>
    private async Task WriteLayoutAsync(string name)
    {
        var layout = CurrentLayout(_id, name);
        await _store.SaveLayoutAsync(layout, CancellationToken.None);
        LoadLayout(layout, false);
        RebuildLayoutBox();
    }

    /// <summary>Runs a store write with the buttons disabled and the failure shown in a message box.</summary>
    private async Task RunBusyAsync(Func<Task> work)
    {
        _busy = true;
        UpdateButtons();
        try
        {
            await work();
        }
        catch (Exception ex)
        {
            ShowError(Fmt(Strings.WindowSnapping_SaveErrorFormat, ex.Message));
        }
        finally
        {
            _busy = false;
            if (!_closing) UpdateButtons();
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_closing) return;
        CommitPendingEdits();
        if (LayoutEditsPending && !ConfirmDiscard())
        {
            e.Cancel = true;
            return;
        }

        _closing = true;
        if (_settings is not null && WindowState == WindowState.Normal)
        {
            var width = Width.ToString("0.##", CultureInfo.InvariantCulture);
            var height = Height.ToString("0.##", CultureInfo.InvariantCulture);
            _ = _settings.SetAsync(WidthKey, width, false, CancellationToken.None);
            _ = _settings.SetAsync(HeightKey, height, false, CancellationToken.None);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _liveTimer.Stop();
        _identifier?.Dispose();
        _identifier = null;
        base.OnClosed(e);
    }

    // ---- keyboard ----

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftAlt or Key.RightAlt)
        {
            Preview.RefreshHover();
            return;
        }

        // Shift moves the split cut to the centre of the zone, so the preview re-evaluates; the other shortcuts still run.
        if (key is Key.LeftShift or Key.RightShift) Preview.RefreshHover();

        var modifiers = Keyboard.Modifiers;
        bool ctrl = modifiers.HasFlag(ModifierKeys.Control);
        bool shift = modifiers.HasFlag(ModifierKeys.Shift);
        if (ctrl && key == Key.S)
        {
            _ = SaveAsync();
            e.Handled = true;
            return;
        }

        // Plain text editing keeps its own shortcuts while a box has the focus.
        if (Keyboard.FocusedElement is TextBox) return;

        if (ctrl && key == Key.Z && !shift) Undo();
        else if (ctrl && (key == Key.Y || (key == Key.Z && shift))) Redo();
        else if (ctrl && key == Key.M) MergeZones(Preview.Selection.ToList());
        else if (!ctrl && key == Key.Delete)
        {
            if (_canvasHistory is { } canvas) RemoveSelectedCanvasZone(canvas);
            else MergeWithNeighbor();
        }
        else return;
        e.Handled = true;
    }

    // Esc closes the window (it used to be the Close button's cancel key). It runs on the bubbling pass, so a control
    // that uses Esc itself (a menu, a drag in the preview) keeps it; OnClosing asks about unsaved edits.
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key != Key.Escape || e.Handled) return;
        e.Handled = true;
        Close();
    }

    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        base.OnPreviewKeyUp(e);
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift) Preview.RefreshHover();
    }
}
