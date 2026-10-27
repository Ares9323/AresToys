using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using AresToys.App.Resources;
using AresToys.App.Services.Zones;
using Wpf.Ui.Controls;
using static AresToys.App.Services.Zones.ZoneNative;
using ContextMenu = System.Windows.Controls.ContextMenu;
using TextBox = System.Windows.Controls.TextBox;

namespace AresToys.App.Views;

/// <summary>Window for editing the window snapping layouts: pick or create a layout, split and merge zones on a live
/// preview, drag boundaries with snapping, type exact numbers, undo and redo, assign the layout to a monitor.
/// The window works on one layout at a time and writes nothing until Save. Every layout change goes through
/// <see cref="ZoneGridEditor"/> and the <see cref="ZoneEditHistory"/>. Works with Show and ShowDialog.</summary>
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
    private ZoneEditHistory _history = null!;
    private GridLayout _loadedGrid = null!;
    private string _id = string.Empty;
    private string _name = string.Empty;
    private string _loadedName = string.Empty;
    private bool _isBuiltIn;
    private bool _isPending;
    private int? _quickKey;
    private int? _loadedQuickKey;
    private bool _wantAssign;
    private bool _suppress;
    private bool _closing;
    private bool _busy;
    private bool _applying;
    private ZoneAxis _splitAxis = ZoneAxis.Vertical;
    private readonly int[] _shownFields = new int[4];
    private MonitorIdentifier? _identifier;

    /// <param name="monitors">The monitor set; with spanning on, the "Use on" list holds one entry for all monitors.</param>
    /// <param name="launchMonitor">The real display the window opens centered on (and, when spanning is off, the
    /// monitor preselected in the "Use on" list). Null keeps the window centered on the screen.</param>
    internal ZoneEditorWindow(ZoneLayoutStore store, ZoneMonitors monitors, ZoneAppearance appearance, string? initialLayoutId,
        ZoneMonitor? launchMonitor = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(monitors);
        ArgumentNullException.ThrowIfNull(appearance);
        _store = store;
        _zoneMonitors = monitors;
        _launchMonitor = launchMonitor;
        InitializeComponent();
        Preview.Appearance = appearance;
        if (launchMonitor is not null) PositionOn(launchMonitor);

        var found = monitors.All();
        _monitors = found.Count > 0 ? found : [new ZoneMonitor(string.Empty, new PixelRect(0, 0, 1920, 1080), 1.0)];
        _spanned = _monitors.Count == 1 && _monitors[0].Id == ZoneMonitors.SpanId;
        RefreshMonitorBox();

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
        initial ??= (store.GetAssignment(_monitor.Id) is { } assigned ? store.Find(assigned) : null) ?? store.Layouts[0];

        _suppress = true;
        MonitorBox.SelectedIndex = monitorIndex;
        _suppress = false;
        Preview.SetWorkArea(_monitor.WorkArea.Width, _monitor.WorkArea.Height);
        Preview.SelectionChanged += (_, _) => RefreshSelectionPanel();
        Preview.EditCommitted += OnPreviewEditCommitted;
        Preview.Notice += (_, text) => SetNote(text);
        Preview.PreviewMouseLeftButtonDown += (_, _) => CommitPendingEdits();
        UpdateSplitAxisButton();
        LoadLayout(initial, false);
        RebuildLayoutBox();
    }

    /// <summary>True when the window closed because the user saved.</summary>
    public bool Saved { get; private set; }

    private sealed record LayoutItem(ZoneLayout Layout, bool IsPending)
    {
        public string Text => Layout.IsBuiltIn ? ZoneEditorWindow.Fmt(Strings.WindowSnapping_BuiltInFormat, Layout.Name) : Layout.Name;
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

    private sealed record MonitorItem(ZoneMonitor Monitor, string Text);

    private sealed record QuickKeyItem(int? Key, string Text);

    /// <summary>True when the layout being edited is already saved as the one used on this monitor.</summary>
    private bool UsesCurrentLayout(ZoneMonitor monitor) => !_isPending && _store.GetAssignment(monitor.Id) == _id;

    /// <summary>Entry text such as "1: 2560 x 1440 (primary) (uses this layout)"; the number is the one the
    /// identify windows show.</summary>
    private string MonitorText(ZoneMonitor monitor, int index)
    {
        var text = monitor.Id == ZoneMonitors.SpanId
            ? Fmt(Strings.WindowSnapping_MonitorSpannedFormat, monitor.WorkArea.Width, monitor.WorkArea.Height)
            : Fmt(Strings.WindowSnapping_MonitorEntryFormat, index + 1, monitor.WorkArea.Width, monitor.WorkArea.Height);
        if (monitor.IsPrimary && monitor.Id != ZoneMonitors.SpanId) text += " " + Strings.WindowSnapping_MonitorPrimary;
        if (UsesCurrentLayout(monitor)) text += " " + Strings.WindowSnapping_MonitorUsesLayout;
        return text;
    }

    /// <summary>Rebuilds the monitor entries (the marker depends on the layout being edited) and keeps the selection.</summary>
    private void RefreshMonitorBox()
    {
        int selected = MonitorBox.SelectedIndex;
        _suppress = true;
        MonitorBox.ItemsSource = _monitors.Select((m, i) => new MonitorItem(m, MonitorText(m, i))).ToList();
        MonitorBox.SelectedIndex = selected;
        _suppress = false;
    }

    // With spanning on every real monitor shows the layout of the one combined monitor, which is what is in force on it.
    private string LayoutNameFor(ZoneMonitor monitor)
    {
        var key = _spanned ? ZoneMonitors.SpanId : monitor.Id;
        if (!_isPending && _store.GetAssignment(key) == _id) return _name;
        var assigned = _store.GetAssignment(key);
        return assigned is not null && _store.Find(assigned) is { } layout ? layout.Name : Strings.WindowSnapping_NoLayoutAssigned;
    }

    private void OnIdentifyClicked(object sender, RoutedEventArgs e)
    {
        _identifier ??= new MonitorIdentifier();

        // The identify windows always number the real displays, also while zones span all of them.
        _identifier.Show(_zoneMonitors.Real().Select((m, i) => new MonitorIdentity(m, i + 1, LayoutNameFor(m))));
    }

    // ---- state ----

    private bool GridChanged => !ZoneGridEditor.SameLayout(_history.Current, _loadedGrid);

    private bool WantsAssignment => UseOnBox.IsChecked == true && UseOnBox.IsEnabled;

    private bool QuickKeyChanged => _quickKey != _loadedQuickKey;

    private bool IsDirty => _isPending || GridChanged || _name != _loadedName || WantsAssignment || QuickKeyChanged;

    private void LoadLayout(ZoneLayout layout, bool pending)
    {
        _id = layout.Id;
        _name = _loadedName = layout.Name;
        _isBuiltIn = layout.IsBuiltIn;
        _isPending = pending;

        // New and duplicated layouts start with no quick key; a saved one shows the key the store holds.
        _quickKey = _loadedQuickKey = pending ? null : _store.GetQuickKey(layout.Id);
        _loadedGrid = layout.Grid;
        _wantAssign = false;
        if (_history is not null) _history.Changed -= OnHistoryChanged;
        _history = new ZoneEditHistory(layout.Grid);
        _history.Changed += OnHistoryChanged;
        Preview.SetSelection([]);
        SetNote(null);
        RefreshAll();
    }

    private void OnHistoryChanged(object? sender, EventArgs e) => RefreshAll();

    private void RebuildLayoutBox()
    {
        var items = new List<LayoutItem>();
        foreach (var layout in _store.Layouts)
        {
            items.Add(new LayoutItem(!_isPending && layout.Id == _id ? layout with { Name = _name } : layout, false));
        }

        if (_isPending) items.Add(new LayoutItem(new ZoneLayout(_id, _name, _history.Current, false), true));
        _suppress = true;
        LayoutBox.ItemsSource = items;
        LayoutBox.SelectedItem = items.FirstOrDefault(i => i.Layout.Id == _id);
        _suppress = false;
        RefreshMonitorBox();
    }

    private void RefreshAll()
    {
        Preview.SetLayout(_history.Current);
        SpacingBox.Text = _history.Current.Spacing.ToString(CultureInfo.CurrentCulture);
        SensitivityBox.Text = _history.Current.SensitivityRadius.ToString(CultureInfo.CurrentCulture);
        UndoButton.IsEnabled = _history.CanUndo;
        RedoButton.IsEnabled = _history.CanRedo;
        UpdateAssignBox();
        RefreshQuickKeyBox();
        RefreshSelectionPanel();
        UpdateButtons();
    }

    /// <summary>Fills the quick key list: None and 0 to 9. A digit that another layout holds reads "3 (used by Two
    /// columns)"; choosing it moves the key to this layout when saved.</summary>
    private void RefreshQuickKeyBox()
    {
        var items = new List<QuickKeyItem> { new(null, Strings.WindowSnapping_QuickKeyNone) };
        for (int digit = ZoneLayoutStore.MinQuickKey; digit <= ZoneLayoutStore.MaxQuickKey; digit++)
        {
            var text = digit.ToString(CultureInfo.CurrentCulture);
            if (_store.FindByQuickKey(digit) is { } owner && owner != _id && _store.Find(owner) is { } other)
            {
                text = Fmt(Strings.WindowSnapping_QuickKeyUsedFormat, text, other.Name);
            }

            items.Add(new QuickKeyItem(digit, text));
        }

        var was = _suppress;
        _suppress = true;
        QuickKeyBox.ItemsSource = items;
        QuickKeyBox.SelectedItem = items.First(i => i.Key == _quickKey);
        _suppress = was;
    }

    private void OnQuickKeySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || QuickKeyBox.SelectedItem is not QuickKeyItem item) return;
        _quickKey = item.Key;
        UpdateButtons();
    }

    private void UpdateAssignBox()
    {
        bool assigned = !_isPending && _store.GetAssignment(_monitor.Id) == _id;
        UseOnBox.IsEnabled = !assigned;
        UseOnBox.IsChecked = assigned || _wantAssign;
    }

    private void UpdateButtons()
    {
        RenameButton.IsEnabled = !_isBuiltIn;
        DeleteButton.IsEnabled = !_isBuiltIn;
        SaveButton.IsEnabled = !_busy && (_isBuiltIn
            ? !GridChanged && (WantsAssignment || QuickKeyChanged)
            : IsDirty);
        SaveCopyButton.IsEnabled = !_busy;
    }

    private void SetNote(string? text) => NoteText.Text = text ?? string.Empty;

    private void ShowError(string message)
        => System.Windows.MessageBox.Show(this, message, "AresToys", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);

    private bool ConfirmDiscard()
        => System.Windows.MessageBox.Show(this, Strings.WindowSnapping_DiscardMessage, "AresToys",
            System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question) == System.Windows.MessageBoxResult.Yes;

    // ---- layout picker and layout commands ----

    private void OnLayoutSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || LayoutBox.SelectedItem is not LayoutItem item || item.Layout.Id == _id) return;
        CommitPendingEdits();
        if (IsDirty && !ConfirmDiscard())
        {
            RebuildLayoutBox();
            return;
        }

        LoadLayout(item.Layout, false);
        RebuildLayoutBox();
    }

    private void OnNewClicked(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = NewButton, Placement = PlacementMode.Bottom };
        var empty = new System.Windows.Controls.MenuItem { Header = Strings.WindowSnapping_NewEmpty };
        empty.Click += (_, _) => StartNew(Strings.WindowSnapping_NewLayoutName, ZoneLayoutPresets.Columns(1) with { Spacing = DefaultSpacing }, true);
        menu.Items.Add(empty);
        var custom = new System.Windows.Controls.MenuItem { Header = Strings.WindowSnapping_NewCustomGrid };
        custom.Click += (_, _) => OnCustomGridChosen();
        menu.Items.Add(custom);
        foreach (var preset in _store.Layouts.Where(l => l.IsBuiltIn))
        {
            var captured = preset;
            var item = new System.Windows.Controls.MenuItem { Header = preset.Name };
            item.Click += (_, _) => StartNew(captured.Name, captured.Grid, true);
            menu.Items.Add(item);
        }

        menu.IsOpen = true;
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
        StartNew(Fmt(Strings.WindowSnapping_CopyFormat, _name), _history.Current, false);
    }

    /// <summary>Starts an unsaved layout. A copy carries the current edits, so only a fresh start asks about discarding.</summary>
    private void StartNew(string name, GridLayout grid, bool confirmDiscard)
    {
        CommitPendingEdits();
        if (confirmDiscard && IsDirty && !ConfirmDiscard()) return;
        LoadLayout(new ZoneLayout(NewId(), name, grid, false), true);
        RebuildLayoutBox();
    }

    private static string NewId() => "user-" + Guid.NewGuid().ToString("N")[..8];

    private void OnRenameClicked(object sender, RoutedEventArgs e)
    {
        if (_isBuiltIn) return;
        var dialog = new TextPromptDialog(Strings.WindowSnapping_RenameTitle, Strings.WindowSnapping_NameLabel, _name) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Result is { } name)
        {
            _name = name;
            RebuildLayoutBox();
            UpdateButtons();
        }
    }

    private async void OnDeleteClicked(object sender, RoutedEventArgs e)
    {
        if (_isBuiltIn) return;
        var answer = System.Windows.MessageBox.Show(this,
            Fmt(Strings.WindowSnapping_DeleteConfirmFormat, _name), "AresToys",
            System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
        if (answer != System.Windows.MessageBoxResult.Yes) return;

        try
        {
            if (!_isPending) await _store.DeleteLayoutAsync(_id, CancellationToken.None);
        }
        catch (Exception ex)
        {
            ShowError(Fmt(Strings.WindowSnapping_DeleteErrorFormat, ex.Message));
            return;
        }

        LoadLayout(_store.Layouts[0], false);
        RebuildLayoutBox();
    }

    // ---- settings and monitor ----

    private void OnSettingKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ApplySettings();
            e.Handled = true;
        }
    }

    private void OnSettingLostFocus(object sender, RoutedEventArgs e) => ApplySettings();

    private void ApplySettings()
    {
        if (_applying) return;
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
            SpacingBox.Text = g.Spacing.ToString(CultureInfo.CurrentCulture);
            SensitivityBox.Text = g.SensitivityRadius.ToString(CultureInfo.CurrentCulture);
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

    private void OnMonitorSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || MonitorBox.SelectedItem is not MonitorItem item) return;
        CommitPendingEdits();
        _monitor = item.Monitor;
        Preview.SetWorkArea(_monitor.WorkArea.Width, _monitor.WorkArea.Height);
        UpdateAssignBox();
        RefreshSelectionPanel();
        UpdateButtons();
    }

    private void OnUseOnClicked(object sender, RoutedEventArgs e)
    {
        _wantAssign = UseOnBox.IsChecked == true;
        UpdateButtons();
    }

    // ---- tools ----

    private void OnSelectToolClicked(object sender, RoutedEventArgs e) => SetTool(ZoneEditTool.Select);

    private void OnSplitToolClicked(object sender, RoutedEventArgs e) => SetTool(ZoneEditTool.Split);

    private void SetTool(ZoneEditTool tool)
    {
        Preview.Tool = tool;
        SelectToolButton.IsChecked = tool == ZoneEditTool.Select;
        SplitToolButton.IsChecked = tool == ZoneEditTool.Split;
        SplitAxisButton.IsEnabled = tool == ZoneEditTool.Split;
        SetNote(null);
    }

    private void OnSplitAxisClicked(object sender, RoutedEventArgs e) => ToggleSplitAxis();

    private void ToggleSplitAxis()
    {
        _splitAxis = _splitAxis == ZoneAxis.Vertical ? ZoneAxis.Horizontal : ZoneAxis.Vertical;
        Preview.SplitAxis = _splitAxis;
        UpdateSplitAxisButton();
    }

    private void UpdateSplitAxisButton()
        => SplitAxisButton.Content = _splitAxis == ZoneAxis.Vertical ? Strings.WindowSnapping_SplitVertical : Strings.WindowSnapping_SplitHorizontal;

    private void OnMergeClicked(object sender, RoutedEventArgs e) => MergeZones(Preview.Selection.ToList());

    private void OnUndoClicked(object sender, RoutedEventArgs e) => Undo();

    private void OnRedoClicked(object sender, RoutedEventArgs e) => Redo();

    private void Undo()
    {
        if (!_history.CanUndo) return;
        _history.Undo();
        SetNote(null);
    }

    private void Redo()
    {
        if (!_history.CanRedo) return;
        _history.Redo();
        SetNote(null);
    }

    private void OnPreviewEditCommitted(object? sender, GridLayout layout)
    {
        _history.Push(layout);
        SetNote(null);
    }

    private void MergeZones(IReadOnlyCollection<int> zones)
    {
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

    private void RefreshSelectionPanel()
    {
        var selection = Preview.Selection;
        MergeButton.IsEnabled = selection.Count >= 2;
        var g = _history.Current;
        _applying = true;
        try
        {
            if (selection.Count == 1)
            {
                int zone = selection.First();
                var b = ZoneGridEditor.ZoneBounds(g, zone);
                int left = ToPixels(b.Left, WorkWidth);
                int top = ToPixels(b.Top, WorkHeight);
                int right = ToPixels(b.Right, WorkWidth);
                int bottom = ToPixels(b.Bottom, WorkHeight);
                ZoneHeaderText.Text = Fmt(Strings.WindowSnapping_ZoneOfFormat, zone + 1, g.ZoneCount);
                SetField(LeftBox, 0, left, b.Left > 0);
                SetField(TopBox, 1, top, b.Top > 0);
                SetField(WidthBox, 2, right - left, b.Left > 0 || b.Right < GridLayout.PercentTotal);
                SetField(HeightBox, 3, bottom - top, b.Top > 0 || b.Bottom < GridLayout.PercentTotal);
            }
            else
            {
                ZoneHeaderText.Text = selection.Count == 0
                    ? Strings.WindowSnapping_NoSelection
                    : Fmt(Strings.WindowSnapping_ZonesSelectedFormat, selection.Count);
                foreach (var box in new[] { LeftBox, TopBox, WidthBox, HeightBox })
                {
                    box.Text = string.Empty;
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
        box.Text = value.ToString(CultureInfo.CurrentCulture);
        box.IsEnabled = enabled;
    }

    private void OnFieldKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ApplyZoneFields();
            e.Handled = true;
        }
    }

    private void OnFieldLostFocus(object sender, RoutedEventArgs e) => ApplyZoneFields();

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
        var g = _history.Current;
        var b = ZoneGridEditor.ZoneBounds(g, zone);
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

        var result = ZoneGridEditor.SetZoneEdges(g, zone,
            l != curL ? ToPercent(l, WorkWidth) : null,
            t != curT ? ToPercent(t, WorkHeight) : null,
            r != curR ? ToPercent(r, WorkWidth) : null,
            bt != curB ? ToPercent(bt, WorkHeight) : null);

        var landed = ZoneGridEditor.ZoneBounds(result.Layout, zone);
        bool clamped = (l != curL && ToPixels(landed.Left, WorkWidth) != l)
            || (t != curT && ToPixels(landed.Top, WorkHeight) != t)
            || (r != curR && ToPixels(landed.Right, WorkWidth) != r)
            || (bt != curB && ToPixels(landed.Bottom, WorkHeight) != bt);
        SetNote(result.Reason ?? (clamped ? Strings.WindowSnapping_NoteClamped : null));

        if (result.Changed) _history.Push(result.Layout);
        else RefreshSelectionPanel();
    }

    /// <summary>Applies what is typed in the settings and number boxes, so a click elsewhere never loses it.</summary>
    private void CommitPendingEdits()
    {
        ApplySettings();
        ApplyZoneFields();
    }

    // ---- saving and closing ----

    private async void OnSaveClicked(object sender, RoutedEventArgs e) => await SaveAsync(false);

    private async void OnSaveCopyClicked(object sender, RoutedEventArgs e) => await SaveAsync(true);

    private async Task SaveAsync(bool asCopy)
    {
        if (_busy) return;
        CommitPendingEdits();

        string id = _id;
        string name = _name;
        bool writeLayout = true;
        if (asCopy)
        {
            if (AskName(Fmt(Strings.WindowSnapping_CopyFormat, _name)) is not { } copyName) return;
            id = NewId();
            name = copyName;
        }
        else if (_isBuiltIn)
        {
            // A built-in layout cannot be written; Save only applies the monitor assignment.
            writeLayout = false;
        }
        else if (_isPending)
        {
            if (AskName(_name) is not { } newName) return;
            name = newName;
        }

        _busy = true;
        UpdateButtons();
        try
        {
            var layout = new ZoneLayout(id, name, _history.Current, false);
            if (writeLayout) await _store.SaveLayoutAsync(layout, CancellationToken.None);
            if (WantsAssignment) await _store.SetAssignmentAsync(_monitor.Id, id, CancellationToken.None);

            // A copy starts with no key and gets one only when the user picked it in this session, so copying never
            // takes the original's key away. The layout is saved first: the store refuses a key for an unknown layout.
            if (QuickKeyChanged) await _store.SetQuickKeyAsync(id, _quickKey, CancellationToken.None);

            if (asCopy)
            {
                _busy = false;
                LoadLayout(layout, false);
                RebuildLayoutBox();
                return;
            }

            CloseWith(true);
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

    private string? AskName(string initial)
    {
        var dialog = new TextPromptDialog(Strings.WindowSnapping_NameTitle, Strings.WindowSnapping_NameLabel, initial) { Owner = this };
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }

    private void CloseWith(bool saved)
    {
        _closing = true;
        Saved = saved;
        try
        {
            DialogResult = saved;
            return;
        }
        catch (InvalidOperationException)
        {
            // Shown with Show(): there is no dialog result to set, just close.
        }

        Close();
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_closing) return;
        CommitPendingEdits();
        if (IsDirty && !ConfirmDiscard())
        {
            e.Cancel = true;
            return;
        }

        _closing = true;
    }

    protected override void OnClosed(EventArgs e)
    {
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

        var modifiers = Keyboard.Modifiers;
        bool ctrl = modifiers.HasFlag(ModifierKeys.Control);
        bool shift = modifiers.HasFlag(ModifierKeys.Shift);
        if (ctrl && key == Key.S)
        {
            bool copyInstead = _isBuiltIn && GridChanged;
            if (copyInstead || SaveButton.IsEnabled) _ = SaveAsync(copyInstead);
            e.Handled = true;
            return;
        }

        // Plain text editing keeps its own shortcuts while a box has the focus.
        if (Keyboard.FocusedElement is TextBox) return;

        if (ctrl && key == Key.Z && !shift) Undo();
        else if (ctrl && (key == Key.Y || (key == Key.Z && shift))) Redo();
        else if (ctrl && key == Key.M) MergeZones(Preview.Selection.ToList());
        else if (!ctrl && key == Key.Delete) MergeWithNeighbor();
        else if (!ctrl && !shift && key == Key.R) ToggleSplitAxis();
        else return;
        e.Handled = true;
    }

    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        base.OnPreviewKeyUp(e);
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftAlt or Key.RightAlt) Preview.RefreshHover();
    }
}
