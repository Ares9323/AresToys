using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using ShareQ.App.ViewModels;
using ShareQ.Editor.Model;
using ShareQ.Editor.Views;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using MenuItem = System.Windows.Controls.MenuItem;

namespace ShareQ.App;

public partial class MainWindow : FluentWindow
{
    private const string StepDragFormat = "ShareQ.WorkflowStep";
    private Point _dragStartPoint;
    private WorkflowStepViewModel? _dragSourceStep;
    private readonly ShareQ.App.Services.ScreenColorPickerService _screenSampler;
    private readonly ShareQ.Editor.Persistence.ColorRecentsStore _colorRecents;
    private readonly ShareQ.App.Services.SettingsBackupService _settingsBackup;
    private readonly ShareQ.PluginContracts.IPluginConfigStoreFactory _pluginConfigFactory;
    private readonly ShareQ.Uploaders.OAuth.OAuthFlowService _oauthFlowService;
    private readonly ShareQ.Pipeline.Profiles.IPipelineProfileStore _profileStore;
    private readonly ShareQ.Storage.Settings.ISettingsStore _settingsStore;
    private readonly ShareQ.Storage.ImageEffects.IImageEffectPresetStore _imageEffectPresetStore;
    // Set true during the initial Loaded sync so SelectionChanged doesn't immediately overwrite
    // the persisted value with the default-selected item.
    private bool _suppressContextMenuWorkflowChange;

    // Persistence keys for window placement. Loaded once in the Loaded handler, written on
    // every move / resize via SizeChanged + LocationChanged. Stored as plain string columns in
    // settings (not sensitive — the popup already does the same dance).
    private const string MainWindowXKey = "mainwindow.x";
    private const string MainWindowYKey = "mainwindow.y";
    private const string MainWindowWidthKey = "mainwindow.width";
    private const string MainWindowHeightKey = "mainwindow.height";
    private const string MainWindowMaximizedKey = "mainwindow.maximized";
    private bool _placementLoaded;

    public MainWindow(SettingsViewModel viewModel,
        ShareQ.App.Services.ScreenColorPickerService screenSampler,
        ShareQ.Editor.Persistence.ColorRecentsStore colorRecents,
        ShareQ.App.Services.SettingsBackupService settingsBackup,
        ShareQ.PluginContracts.IPluginConfigStoreFactory pluginConfigFactory,
        ShareQ.Uploaders.OAuth.OAuthFlowService oauthFlowService,
        ShareQ.Pipeline.Profiles.IPipelineProfileStore profileStore,
        ShareQ.Storage.Settings.ISettingsStore settingsStore,
        ShareQ.Storage.ImageEffects.IImageEffectPresetStore imageEffectPresetStore)
    {
        InitializeComponent();
        DataContext = viewModel;
        _screenSampler = screenSampler;
        _colorRecents = colorRecents;
        _settingsBackup = settingsBackup;
        _pluginConfigFactory = pluginConfigFactory;
        _oauthFlowService = oauthFlowService;
        _profileStore = profileStore;
        _settingsStore = settingsStore;
        _imageEffectPresetStore = imageEffectPresetStore;
        // Newly-added workflow: focus the inline name field and select its text so the user can
        // type the real name straight away. Defer to a low-priority dispatcher tick because the
        // edit-view's TextBox isn't realised until the visibility binding flips.
        viewModel.Hotkeys.EditNameFocusRequested += (_, _) =>
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                WorkflowNameInline.Focus();
                WorkflowNameInline.SelectAll();
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        };

        // Auto-scroll the Debug log to the latest entry. We listen at the collection level so
        // every Append (ILogger callback → DebugLogService.Append → ObservableCollection.Add)
        // ends up at the bottom of the visible list. Gated on Debug.AutoScroll so the user can
        // turn it off and read older entries without the view yanking back.
        ((INotifyCollectionChanged)viewModel.Debug.Entries).CollectionChanged += (_, e) =>
        {
            if (!viewModel.Debug.AutoScroll) return;
            if (e.Action != NotifyCollectionChangedAction.Add) return;
            // Skip when the Debug tab isn't currently realised — the ListBox lives inside a
            // collapsed Grid until the user selects the tab, and a virtualized ListBox throws
            // when ScrollIntoView is called before its ItemsPanel exists. Once the tab is
            // visited, IsLoaded + IsVisible become true and auto-scroll kicks in normally.
            if (DebugLogList is null || !DebugLogList.IsLoaded || !DebugLogList.IsVisible) return;
            var last = viewModel.Debug.Entries.Count > 0 ? viewModel.Debug.Entries[^1] : null;
            if (last is null) return;
            try { DebugLogList.ScrollIntoView(last); }
            catch { /* virtualization race during heavy log bursts — drop a single tick */ }
        };

        // Window placement persistence. Load saved size/position on first show; save on every
        // resize / move. Same pattern the clipboard popup uses for its own bounds.
        Loaded += async (_, _) => await LoadWindowPlacementAsync();
        SizeChanged += (_, _) => _ = SaveWindowPlacementAsync();
        LocationChanged += (_, _) => _ = SaveWindowPlacementAsync();
        StateChanged += (_, _) => _ = SaveWindowPlacementAsync();
    }

    /// <summary>Manual drag-resize from the bottom-right thumb. Mirrors the Launcher window's
    /// handler so the corner grab feels identical across the app. The system already permits
    /// edge resize via FluentWindow chrome; this thumb is the affordance for users who hunt
    /// for a corner handle.</summary>
    private void OnMainResizeThumbDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        var newW = Math.Max(MinWidth,  ActualWidth  + e.HorizontalChange);
        var newH = Math.Max(MinHeight, ActualHeight + e.VerticalChange);
        Width = newW;
        Height = newH;
    }

    // ── Tray click action ComboBoxes ─────────────────────────────────────────────────
    // Tray clicks now route through arbitrary pipeline profiles — same picker shape as the
    // Explorer-context-menu workflow ComboBox above. The list comes from IPipelineProfileStore
    // so any workflow (built-in or user-created) can be wired to a click. A sentinel
    // "(do nothing)" option maps to NoneMarker so the user can disable a click entirely.
    private sealed record TrayClickProfileOption(string Id, string DisplayName);
    private bool _suppressTrayClickPersist;

    private async void OnTrayClickComboLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.ComboBox combo) return;
        var (key, def) = ResolveTrayClickKey(combo);
        if (string.IsNullOrEmpty(key)) return;
        try
        {
            var profiles = await _profileStore.ListAsync(System.Threading.CancellationToken.None);
            var options = new List<TrayClickProfileOption>
            {
                new(Services.TrayIconService.NoneMarker, "(do nothing)"),
            };
            foreach (var p in profiles.OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase))
                options.Add(new TrayClickProfileOption(p.Id, p.DisplayName));

            var raw = await _settingsStore.GetAsync(key, System.Threading.CancellationToken.None);
            _suppressTrayClickPersist = true;
            combo.DisplayMemberPath = nameof(TrayClickProfileOption.DisplayName);
            combo.SelectedValuePath = nameof(TrayClickProfileOption.Id);
            combo.ItemsSource = options;
            combo.SelectedValue = string.IsNullOrEmpty(raw) ? def : raw;
            if (combo.SelectedValue is null) combo.SelectedValue = def;
        }
        finally { _suppressTrayClickPersist = false; }
    }

    private (string Key, string Default) ResolveTrayClickKey(System.Windows.Controls.ComboBox combo) => combo.Name switch
    {
        nameof(TrayLeftClickCombo) =>   (Services.TrayIconService.LeftClickKey,   ShareQ.Pipeline.Profiles.DefaultPipelineProfiles.OpenSettingsId),
        nameof(TrayDoubleClickCombo) => (Services.TrayIconService.DoubleClickKey, ShareQ.Pipeline.Profiles.DefaultPipelineProfiles.ShowPopupId),
        nameof(TrayMiddleClickCombo) => (Services.TrayIconService.MiddleClickKey, Services.TrayIconService.NoneMarker),
        _ => (string.Empty, Services.TrayIconService.NoneMarker),
    };

    private async void OnTrayLeftClickChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        => await PersistTrayClickAsync(sender, Services.TrayIconService.LeftClickKey);
    private async void OnTrayDoubleClickChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        => await PersistTrayClickAsync(sender, Services.TrayIconService.DoubleClickKey);
    private async void OnTrayMiddleClickChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        => await PersistTrayClickAsync(sender, Services.TrayIconService.MiddleClickKey);

    private async Task PersistTrayClickAsync(object sender, string key)
    {
        if (_suppressTrayClickPersist) return;
        if (sender is not System.Windows.Controls.ComboBox combo) return;
        if (combo.SelectedValue is not string value || string.IsNullOrEmpty(value)) return;
        try { await _settingsStore.SetAsync(key, value, sensitive: false, System.Threading.CancellationToken.None); }
        catch { /* persistence is best-effort; the next run reads the previous value */ }
    }

    private async Task LoadWindowPlacementAsync()
    {
        try
        {
            var x = await _settingsStore.GetAsync(MainWindowXKey, System.Threading.CancellationToken.None);
            var y = await _settingsStore.GetAsync(MainWindowYKey, System.Threading.CancellationToken.None);
            var w = await _settingsStore.GetAsync(MainWindowWidthKey, System.Threading.CancellationToken.None);
            var h = await _settingsStore.GetAsync(MainWindowHeightKey, System.Threading.CancellationToken.None);
            var max = await _settingsStore.GetAsync(MainWindowMaximizedKey, System.Threading.CancellationToken.None);

            // Order matters: apply size + position BEFORE flipping to maximised — otherwise
            // restoring from maximised gives the user a 900×600 default snap instead of their
            // pre-maximise dimensions.
            if (double.TryParse(w, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var width)
                && double.TryParse(h, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var height)
                && width >= MinWidth && height >= MinHeight)
            {
                Width = width;
                Height = height;
            }
            if (double.TryParse(x, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var left)
                && double.TryParse(y, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var top))
            {
                // Sanity check: reject coordinates that would land us off any monitor (e.g. user
                // unplugged a second screen between sessions). A future startup with the screen
                // back will pick up the stored values fine.
                var virtualLeft = SystemParameters.VirtualScreenLeft;
                var virtualTop = SystemParameters.VirtualScreenTop;
                var virtualRight = virtualLeft + SystemParameters.VirtualScreenWidth;
                var virtualBottom = virtualTop + SystemParameters.VirtualScreenHeight;
                if (left + 50 < virtualRight && top + 50 < virtualBottom
                    && left + Width - 50 > virtualLeft && top + Height - 50 > virtualTop)
                {
                    WindowStartupLocation = WindowStartupLocation.Manual;
                    Left = left;
                    Top = top;
                }
            }
            if (string.Equals(max, "1", StringComparison.Ordinal))
                WindowState = WindowState.Maximized;
        }
        catch { /* placement persistence is cosmetic — never fail startup over a missing row */ }
        finally
        {
            _placementLoaded = true;
        }
    }

    private async Task SaveWindowPlacementAsync()
    {
        // Skip writes during the initial load — Width/Height/Left/Top all fire SizeChanged /
        // LocationChanged as we apply the persisted values, which would echo them back to the
        // store as the freshly-applied values (harmless but noisy).
        if (!_placementLoaded) return;
        // Don't capture the maximised geometry as the "preferred" size — RestoreBounds holds
        // what the user actually set before maximising, which is what we want to restore on
        // next launch.
        var bounds = WindowState == WindowState.Maximized ? RestoreBounds : new Rect(Left, Top, Width, Height);
        if (bounds.Width < MinWidth || bounds.Height < MinHeight) return;
        try
        {
            var ct = System.Threading.CancellationToken.None;
            await _settingsStore.SetAsync(MainWindowXKey, bounds.X.ToString(System.Globalization.CultureInfo.InvariantCulture), false, ct);
            await _settingsStore.SetAsync(MainWindowYKey, bounds.Y.ToString(System.Globalization.CultureInfo.InvariantCulture), false, ct);
            await _settingsStore.SetAsync(MainWindowWidthKey, bounds.Width.ToString(System.Globalization.CultureInfo.InvariantCulture), false, ct);
            await _settingsStore.SetAsync(MainWindowHeightKey, bounds.Height.ToString(System.Globalization.CultureInfo.InvariantCulture), false, ct);
            await _settingsStore.SetAsync(MainWindowMaximizedKey, WindowState == WindowState.Maximized ? "1" : "0", false, ct);
        }
        catch { /* same — failure is not user-visible */ }
    }

    /// <summary>Builds the "+ Add step" categorized context menu on demand. Doing this in
    /// code-behind keeps the XAML clean and avoids the WPF HierarchicalDataTemplate gotchas
    /// around binding Command on dynamically-templated leaf MenuItems.</summary>
    private void OnAddStepButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        if (DataContext is not SettingsViewModel vm) return;

        var menu = new ContextMenu
        {
            PlacementTarget = btn,
            Placement = PlacementMode.Bottom,
        };
        foreach (var group in vm.Workflows.Editor.AddableActions)
        {
            var groupItem = new MenuItem { Header = group.Category };
            foreach (var action in group.Actions)
            {
                var leaf = new MenuItem
                {
                    Header = action.DisplayName,
                    ToolTip = action.Description,
                };
                var capturedDescriptor = action;
                leaf.Click += (_, _) => vm.Workflows.Editor.AddStepCommand.Execute(capturedDescriptor);
                groupItem.Items.Add(leaf);
            }
            menu.Items.Add(groupItem);
        }
        btn.ContextMenu = menu;
        menu.IsOpen = true;
    }

    // ── Workflow step drag-and-drop reordering ──────────────────────────────────────────────
    // Dragging is initiated only from the ⋮⋮ handle (so the row's other interactive elements —
    // toggle, parameter buttons, remove — keep working without drag interference). MouseDown
    // captures the source row + start point; MouseMove starts DoDragDrop once the system drag
    // threshold is exceeded. The drop target is any Border in the same ItemsControl that has
    // AllowDrop=True (i.e. another step row).

    private void OnStepHandleMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        if (fe.DataContext is not WorkflowStepViewModel step) return;
        _dragStartPoint = e.GetPosition(null);
        _dragSourceStep = step;
    }

    private void OnStepHandleMouseUp(object sender, MouseButtonEventArgs e)
    {
        // Cancel a pending-but-not-started drag if the user just clicked without moving.
        _dragSourceStep = null;
    }

    private void OnStepHandleMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragSourceStep is null) return;
        if (e.LeftButton != MouseButtonState.Pressed) return;
        var current = e.GetPosition(null);
        var dx = Math.Abs(current.X - _dragStartPoint.X);
        var dy = Math.Abs(current.Y - _dragStartPoint.Y);
        if (dx < SystemParameters.MinimumHorizontalDragDistance &&
            dy < SystemParameters.MinimumVerticalDragDistance) return;
        if (DataContext is not SettingsViewModel vm) return;

        var source = _dragSourceStep;
        _dragSourceStep = null; // consumed

        // Visual feedback: dim the source while the drag is in flight. Stays set until the editor
        // clears it after the drop / cancel below.
        source.IsDragSource = true;

        var data = new DataObject(StepDragFormat, source);
        DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Move);

        // DoDragDrop blocks until the user drops or cancels. Either way we wipe the visuals so no
        // stale highlight or insertion line lingers.
        vm.Workflows.Editor.ClearDragVisuals();
    }

    private void OnStepRowDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(StepDragFormat))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }
        if (DataContext is not SettingsViewModel vm)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }
        if (sender is not FrameworkElement fe || fe.DataContext is not WorkflowStepViewModel hovered)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        // Cursor in the upper half of this row → drop above this row (= activate this row's
        // top-gutter indicator). Cursor in the lower half → drop below this row, which we render
        // as "above the next row" so every gap is covered by exactly one indicator. If this is
        // the last row and cursor is in the lower half, the footer gutter is the target instead.
        var pos = e.GetPosition(fe);
        var insertAbove = pos.Y < fe.ActualHeight / 2.0;
        var items = vm.Workflows.Editor.Items;
        var idx = items.IndexOf(hovered);

        WorkflowStepViewModel? indicatorRow = null;
        var atEnd = false;
        if (insertAbove)
        {
            indicatorRow = hovered;
        }
        else if (idx >= 0 && idx + 1 < items.Count)
        {
            indicatorRow = items[idx + 1];
        }
        else
        {
            atEnd = true;
        }

        foreach (var item in items)
            item.IsDropTargetAbove = ReferenceEquals(item, indicatorRow);
        vm.Workflows.Editor.IsDropTargetAtEnd = atEnd;

        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void OnStepRowDragLeave(object sender, DragEventArgs e)
    {
        // Don't clear here: with a single-indicator model, cursor moving from row N's lower half
        // into row N+1's top gutter should keep showing the line in that gap. DragOver on the
        // new element fires the same tick and sets the right indicator. Clearing on leave makes
        // the line flicker. Stale indicators are cleaned up when the drag completes.
    }

    private void OnStepRowDrop(object sender, DragEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm) return;
        if (e.Data.GetData(StepDragFormat) is not WorkflowStepViewModel source) return;
        e.Handled = true;
        DispatchDrop(vm, source);
    }

    private void OnStepFooterDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(StepDragFormat) || DataContext is not SettingsViewModel vm)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }
        // Footer hover always means "drop at end". Clear per-row indicators so only the footer
        // line shows.
        foreach (var item in vm.Workflows.Editor.Items) item.IsDropTargetAbove = false;
        vm.Workflows.Editor.IsDropTargetAtEnd = true;
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void OnStepFooterDragLeave(object sender, DragEventArgs e)
    {
        // Same reasoning as the row leave: don't clear, let DragOver on the next element take
        // over. Drag completion handles final cleanup.
    }

    private void OnStepFooterDrop(object sender, DragEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm) return;
        if (e.Data.GetData(StepDragFormat) is not WorkflowStepViewModel source) return;
        e.Handled = true;
        DispatchDrop(vm, source);
    }

    // ── Inline workflow rename ──────────────────────────────────────────────────────────────
    // The TextBox in edit-view is bound TwoWay to WorkflowsViewModel.EditingDisplayName with
    // UpdateSourceTrigger=LostFocus, so the property mirrors what the user typed when focus
    // leaves the box. We then call SaveDisplayNameAsync to persist (or Enter from the keyboard
    // shortcut handler — also commits the focused TextBox first by moving focus to the parent).

    private void OnWorkflowNameLostFocus(object sender, RoutedEventArgs e)
    {
        // Single point where the inline rename gets persisted. Forces the binding to push the
        // typed text into EditingDisplayName right now (UpdateSource is a no-op when the binding
        // already committed via UpdateSourceTrigger=LostFocus, but we run it explicitly so the
        // ordering doesn't matter), then kicks off the save.
        if (sender is System.Windows.Controls.TextBox tb)
        {
            var be = tb.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty);
            be?.UpdateSource();
        }
        if (DataContext is not SettingsViewModel vm) return;
        _ = vm.Hotkeys.Workflows.SaveDisplayNameAsync();
    }

    private void OnWorkflowNameKeyDown(object sender, KeyEventArgs e)
    {
        // Enter or first Esc on the inline rename: shift focus to the next focusable element.
        // That triggers the TextBox's natural LostFocus → OnWorkflowNameLostFocus runs and
        // commits the rename. After the focus move there's a real focused element receiving
        // keystrokes, so a subsequent Esc bubbles to the Window's KeyBinding and navigates back.
        // Marking e.Handled is what stops THIS Esc from also triggering the Window KeyBinding.
        if (e.Key is not (Key.Enter or Key.Escape)) return;
        if (sender is not System.Windows.Controls.TextBox tb) return;
        tb.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        e.Handled = true;
    }

    /// <summary>Common drop dispatcher: reads the indicator state set by the most recent DragOver
    /// to know where the drop should land (above some row, or at the end), then calls the
    /// matching editor command.</summary>
    private static void DispatchDrop(SettingsViewModel vm, WorkflowStepViewModel source)
    {
        var editor = vm.Workflows.Editor;
        if (editor.IsDropTargetAtEnd)
        {
            _ = editor.MoveToEndAsync(source);
            return;
        }
        var aboveTarget = editor.Items.FirstOrDefault(i => i.IsDropTargetAbove);
        if (aboveTarget is not null)
            _ = editor.MoveToAsync(source, aboveTarget, insertAfter: false);
    }

    /// <summary>Theme tab: open the existing editor color picker on the accent-background swatch
    /// and write the chosen RGB back into the Theme view-model's hex string. Handlers live in
    /// code-behind (rather than the VM) so we don't drag <see cref="ColorPickerWindow"/> — a UI
    /// type — into the view-model layer. The hex assignment fires the existing TryApply path
    /// which propagates colors live across the app.</summary>
    private enum AccentChannel { Background, Foreground, Dark, ForegroundDark, Delete, Surface1, Surface2, Surface3 }

    private void OnAccentBgSwatchClick(object sender, MouseButtonEventArgs e)
        => PickAccentColor(AccentChannel.Background);

    private void OnAccentFgSwatchClick(object sender, MouseButtonEventArgs e)
        => PickAccentColor(AccentChannel.Foreground);

    private void OnAccentDarkSwatchClick(object sender, MouseButtonEventArgs e)
        => PickAccentColor(AccentChannel.Dark);

    private void OnAccentForegroundDarkSwatchClick(object sender, MouseButtonEventArgs e)
        => PickAccentColor(AccentChannel.ForegroundDark);

    private void OnAccentDeleteSwatchClick(object sender, MouseButtonEventArgs e)
        => PickAccentColor(AccentChannel.Delete);

    private void OnSurface1SwatchClick(object sender, MouseButtonEventArgs e)
        => PickAccentColor(AccentChannel.Surface1);

    private void OnSurface2SwatchClick(object sender, MouseButtonEventArgs e)
        => PickAccentColor(AccentChannel.Surface2);

    private void OnSurface3SwatchClick(object sender, MouseButtonEventArgs e)
        => PickAccentColor(AccentChannel.Surface3);

    private void PickAccentColor(AccentChannel channel)
    {
        if (DataContext is not SettingsViewModel vm) return;
        var currentHex = channel switch
        {
            AccentChannel.Background     => vm.Theme.AccentBackgroundLightHex,
            AccentChannel.Foreground     => vm.Theme.AccentForegroundLightHex,
            AccentChannel.Dark           => vm.Theme.AccentBackgroundDarkHex,
            AccentChannel.ForegroundDark => vm.Theme.AccentForegroundDarkHex,
            AccentChannel.Delete         => vm.Theme.AccentDangerHex,
            AccentChannel.Surface1       => vm.Theme.Surface1Hex,
            AccentChannel.Surface2       => vm.Theme.Surface2Hex,
            AccentChannel.Surface3       => vm.Theme.Surface3Hex,
            _ => vm.Theme.AccentBackgroundLightHex,
        };
        var fallback = channel == AccentChannel.Foreground
            ? new ShapeColor(255, 255, 255, 255)
            : channel == AccentChannel.ForegroundDark
                ? new ShapeColor(255, 0x87, 0x87, 0x87)
                : ShapeColor.Black;
        var current = TryParseShapeColor(currentHex) ?? fallback;
        var dialog = new ColorPickerWindow(current) { Owner = this };

        // Wire 🔍 button → screen sampler. SampleAtCursor returns hex (no clipboard side-effect)
        // which we parse and push into the dialog via ApplySampledColor.
        dialog.EyedropperRequested += (_, _) =>
        {
            var hex = _screenSampler.SampleAtCursor();
            if (hex is null) return;
            if (TryParseShapeColor(hex) is { } sampled) dialog.ApplySampledColor(sampled);
        };

        // OK button live preview: as the user picks inside the dialog, override the OK button's
        // matching channel so the user sees the *future* accent landed in a real button shape —
        // without the rest of the app re-painting until they commit with OK. Dark channel
        // previews on the Background slot too (it's still a "background-ish" colour).
        EventHandler<ShapeColor> previewHandler = (_, c) =>
        {
            var brush = new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B));
            brush.Freeze();
            if (channel == AccentChannel.Foreground) dialog.OkButton.Foreground = brush;
            else                                      dialog.OkButton.Background = brush;
        };
        dialog.ColorChanged += previewHandler;

        // Pre-populate the Recent palette inside the picker so the user sees their previous
        // accent picks. Without this each Theme picker session starts with an empty Recent grid.
        ColorSwatchButton.CurrentRecents = _colorRecents.LoadAsync(System.Threading.CancellationToken.None).GetAwaiter().GetResult();

        if (dialog.ShowDialog() != true) return;
        var picked = dialog.PickedColor;
        var hex2 = $"#{picked.R:X2}{picked.G:X2}{picked.B:X2}";
        switch (channel)
        {
            case AccentChannel.Background:     vm.Theme.AccentBackgroundLightHex = hex2; break;
            case AccentChannel.Foreground:     vm.Theme.AccentForegroundLightHex = hex2; break;
            case AccentChannel.Dark:           vm.Theme.AccentBackgroundDarkHex = hex2; break;
            case AccentChannel.ForegroundDark: vm.Theme.AccentForegroundDarkHex = hex2; break;
            case AccentChannel.Delete:         vm.Theme.AccentDangerHex = hex2; break;
            case AccentChannel.Surface1:       vm.Theme.Surface1Hex = hex2; break;
            case AccentChannel.Surface2:       vm.Theme.Surface2Hex = hex2; break;
            case AccentChannel.Surface3:       vm.Theme.Surface3Hex = hex2; break;
        }
        // Push to recents so the colour shows up next time the user opens any picker.
        _ = _colorRecents.PushAsync(picked, System.Threading.CancellationToken.None);
    }

    private void OnSxcuAssociationCheckBoxLoaded(object sender, RoutedEventArgs e)
    {
        // Read live registry state every time the checkbox renders — the user might have
        // changed the association from another tool (or another ShareQ install) since we last
        // looked. Suppress the Click handler during this initial sync via the IsLoaded check.
        if (sender is System.Windows.Controls.CheckBox cb)
            cb.IsChecked = ShareQ.App.Services.SxcuFileAssociation.IsRegistered();
    }

    /// <summary>"Pick…" button on the Add-category row → open the IconPickerDialog and write
    /// back into <see cref="CategoriesViewModel.NewCategoryIcon"/>. Clear is treated as "remove
    /// icon" (empty string).</summary>
    private void OnPickCategoryIconClicked(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm) return;
        var dialog = new ShareQ.App.Views.IconPickerDialog(vm.Categories.NewCategoryIcon) { Owner = this };
        if (dialog.ShowDialog() == true)
            vm.Categories.NewCategoryIcon = dialog.PickedGlyph;
    }

    /// <summary>"Pick…" button on a category row → open the dialog and write back into the row
    /// VM's Icon. The row VM is passed via Button.Tag (set in the DataTemplate). The Icon setter
    /// itself triggers SaveAsync via the VM's OnIconChanged partial, so no extra plumbing here.</summary>
    private void OnPickRowIconClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button btn) return;
        if (btn.Tag is not ShareQ.App.ViewModels.CategoryRowViewModel row) return;
        var dialog = new ShareQ.App.Views.IconPickerDialog(row.Icon) { Owner = this };
        if (dialog.ShowDialog() == true)
            row.Icon = dialog.PickedGlyph;
    }

    /// <summary>Autosave hook for category-row text / number fields. Fires on focus loss (after
    /// the binding has already pushed the new value to the VM via UpdateSourceTrigger=LostFocus)
    /// and runs the row's Save command. Default-row fields are disabled so this only ever fires
    /// for user-modifiable rows.</summary>
    private void OnCategoryFieldCommitted(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement el) return;
        if (el.DataContext is not ShareQ.App.ViewModels.CategoryRowViewModel row) return;
        if (!row.CanModify) return;
        _ = row.SaveAsync();
    }

    /// <summary>Enter inside the Name TextBox: push the binding + run Save without waiting for
    /// LostFocus. UX expectation is "I typed the new name, hit Enter, it's saved" — without
    /// this the user has to tab out for the LostFocus path to fire.</summary>
    private void OnCategoryFieldKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        if (sender is not Wpf.Ui.Controls.TextBox tb) return;
        var binding = tb.GetBindingExpression(Wpf.Ui.Controls.TextBox.TextProperty);
        binding?.UpdateSource();
        OnCategoryFieldCommitted(sender, e);
        e.Handled = true;
    }

    /// <summary>Handle clicks on the +/- stepper buttons in the category rows. The Tag carries
    /// the field name + sign (e.g. "MaxItems+", "AutoCleanupAfter-") so a single handler routes
    /// for both columns. After mutating the VM property the row autosaves through the standard
    /// Save command — same path field-commit takes, so behaviour stays identical.</summary>
    private void OnCategoryStepperClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button btn) return;
        if (btn.DataContext is not ShareQ.App.ViewModels.CategoryRowViewModel row) return;
        if (!row.CanConfigure) return;
        if (btn.Tag is not string tag) return;

        // Tag format: "<PropertyName><+|->". Split on the trailing sign character.
        var sign = tag[^1];
        var prop = tag[..^1];
        var step = sign == '+' ? +1 : -1;

        switch (prop)
        {
            case "MaxItems":
                row.MaxItems = Math.Clamp(row.MaxItems + step, 0, 100000);
                break;
            case "AutoCleanupAfter":
                row.AutoCleanupAfter = Math.Clamp(row.AutoCleanupAfter + step, 0, 525600); // 525600 min = 365 days
                break;
            default:
                return;
        }
        _ = row.SaveAsync();
    }

    private void OnSxcuAssociationToggled(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.CheckBox cb) return;
        try
        {
            if (cb.IsChecked == true)
                ShareQ.App.Services.SxcuFileAssociation.Register();
            else
                ShareQ.App.Services.SxcuFileAssociation.Unregister();
        }
        catch (Exception ex)
        {
            // Roll the visual state back if the registry write failed (UAC-restricted user
            // hive scenarios, antivirus interference) so the checkbox doesn't lie about state.
            cb.IsChecked = ShareQ.App.Services.SxcuFileAssociation.IsRegistered();
            System.Windows.MessageBox.Show(this,
                $"Couldn't update file association:\n{ex.Message}",
                "ShareQ", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    private void OnExplorerContextMenuCheckBoxLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.CheckBox cb)
            cb.IsChecked = ShareQ.App.Services.ExplorerContextMenuRegistration.IsRegistered();
    }

    private void OnExplorerContextMenuToggled(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.CheckBox cb) return;
        try
        {
            if (cb.IsChecked == true)
                ShareQ.App.Services.ExplorerContextMenuRegistration.Register();
            else
                ShareQ.App.Services.ExplorerContextMenuRegistration.Unregister();
        }
        catch (Exception ex)
        {
            cb.IsChecked = ShareQ.App.Services.ExplorerContextMenuRegistration.IsRegistered();
            System.Windows.MessageBox.Show(this,
                $"Couldn't update Explorer menu entry:\n{ex.Message}",
                "ShareQ", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    /// <summary>Populate the workflow combo with every pipeline profile (built-in + user-defined)
    /// then select the one currently persisted in settings. We list everything because the user
    /// might genuinely want a "Save to disk" or "Pin to screen" workflow as their context-menu
    /// action — it's their machine, no need to second-guess which profiles are upload-shaped.</summary>
    private async void OnExplorerContextMenuWorkflowComboLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.ComboBox combo) return;
        try
        {
            var profiles = await _profileStore.ListAsync(System.Threading.CancellationToken.None);
            var stored = await _settingsStore.GetAsync(App.ExplorerContextMenuWorkflowKey, System.Threading.CancellationToken.None);
            _suppressContextMenuWorkflowChange = true;
            combo.ItemsSource = profiles;
            combo.SelectedValue = string.IsNullOrEmpty(stored)
                ? ShareQ.Pipeline.Profiles.DefaultPipelineProfiles.ManualUploadId
                : stored;
            // If the stored id no longer exists (profile deleted), fall back to manual-upload so
            // the combo always shows a real selection rather than blank.
            if (combo.SelectedValue is null)
                combo.SelectedValue = ShareQ.Pipeline.Profiles.DefaultPipelineProfiles.ManualUploadId;
        }
        finally
        {
            _suppressContextMenuWorkflowChange = false;
        }
    }

    private async void OnExplorerContextMenuWorkflowChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_suppressContextMenuWorkflowChange) return;
        if (sender is not System.Windows.Controls.ComboBox combo) return;
        if (combo.SelectedValue is not string id || string.IsNullOrEmpty(id)) return;
        try { await _settingsStore.SetAsync(App.ExplorerContextMenuWorkflowKey, id, sensitive: false, System.Threading.CancellationToken.None); }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this,
                $"Couldn't save workflow choice:\n{ex.Message}",
                "ShareQ", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    /// <summary>WPF Hyperlink → default browser. Used by all the About-tab links (repo +
    /// inspiration credits). UseShellExecute=true so http(s) is resolved by the user's default
    /// browser rather than trying to launch the URL as an executable.</summary>
    private void OnHyperlinkClicked(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = e.Uri.AbsoluteUri,
                UseShellExecute = true,
            });
        }
        catch { /* best-effort: a missing default browser shouldn't crash Settings */ }
        e.Handled = true;
    }

    private async void OnCheckForUpdatesClicked(object sender, RoutedEventArgs e)
    {
        // Resolve via Application.Current — avoids dragging another constructor parameter for a
        // single-button handler. The service is a singleton so always the same instance.
        var app = (App)System.Windows.Application.Current;
        var updater = (ShareQ.Updater.UpdaterService?)app.Services?.GetService(typeof(ShareQ.Updater.UpdaterService));
        if (updater is null) return;

        CheckForUpdatesButton.IsEnabled = false;
        UpdateStatusText.Text = "Checking…";
        UpdateStatusText.Visibility = System.Windows.Visibility.Visible;
        try
        {
            var outcome = await updater.CheckInteractivelyAsync(System.Threading.CancellationToken.None);
            UpdateStatusText.Text = outcome.Kind switch
            {
                ShareQ.Updater.CheckOutcomeKind.NotInstalled => "Updates only work from an installed copy (current build is dev / portable-extracted).",
                ShareQ.Updater.CheckOutcomeKind.AlreadyLatest => "You're on the latest version.",
                ShareQ.Updater.CheckOutcomeKind.UpdateFound => $"Version {outcome.Version} available — opening confirmation…",
                ShareQ.Updater.CheckOutcomeKind.Failed => $"Check failed: {outcome.ErrorMessage}",
                _ => "Unknown result.",
            };
            // The UpdateAvailable event already fired and queued the toast; surface the install
            // dialog here too so the user who just clicked "Check" doesn't have to chase the toast.
            if (outcome.Kind == ShareQ.Updater.CheckOutcomeKind.UpdateFound && outcome.Info is not null)
            {
                await App.PromptInstallUpdateAsync(updater, outcome.Info);
            }
        }
        finally
        {
            CheckForUpdatesButton.IsEnabled = true;
        }
    }

    private void OnConfigureUploaderClicked(object sender, RoutedEventArgs e)
    {
        // The Tag carries the row's VM (set by the data-template binding); from there we get the
        // backing IUploader and pair it with a per-id IPluginConfigStore. The dialog handles its
        // own load + save, so all we do here is open it and ignore the result.
        if (sender is not System.Windows.Controls.Control { Tag: UploaderSelectionItemViewModel row })
            return;
        var store = _pluginConfigFactory.Create(row.Id);
        var dlg = new ShareQ.App.Views.UploaderConfigDialog(row.Uploader, store, _oauthFlowService) { Owner = this };
        dlg.ShowDialog();
    }

    private void OnImportCustomUploaderClicked(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import .sxcu file",
            Filter = "ShareX custom uploader|*.sxcu|JSON|*.json|All files|*.*",
            CheckFileExists = true,
            Multiselect = true,
        };
        if (dlg.ShowDialog(this) != true) return;
        var folder = ShareQ.CustomUploaders.CustomUploaderRegistry.DefaultFolder;
        try
        {
            System.IO.Directory.CreateDirectory(folder);
            var imported = 0;
            foreach (var src in dlg.FileNames)
            {
                // Validate first so we don't litter the folder with garbage. Empty / malformed
                // files surface a friendly error in the same dialog the user came from.
                var json = System.IO.File.ReadAllText(src);
                var cfg = ShareQ.CustomUploaders.CustomUploaderConfigLoader.Parse(json);
                if (!ShareQ.CustomUploaders.CustomUploaderConfigLoader.IsValid(cfg))
                {
                    System.Windows.MessageBox.Show(this,
                        $"'{System.IO.Path.GetFileName(src)}' is missing Name or RequestURL — skipping.",
                        "ShareQ", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                    continue;
                }
                var dest = System.IO.Path.Combine(folder, System.IO.Path.GetFileName(src));
                // Avoid clobbering an existing import: append a numeric suffix until the path is free.
                var n = 1;
                while (System.IO.File.Exists(dest))
                {
                    var name = System.IO.Path.GetFileNameWithoutExtension(src);
                    var ext  = System.IO.Path.GetExtension(src);
                    dest = System.IO.Path.Combine(folder, $"{name} ({n}){ext}");
                    n++;
                }
                System.IO.File.Copy(src, dest);
                imported++;
            }
            if (DataContext is SettingsViewModel vm) vm.Uploaders.LoadCustomUploaders();
            if (imported > 0)
            {
                System.Windows.MessageBox.Show(this,
                    $"Imported {imported} file(s). Restart ShareQ to make the new uploaders selectable above.",
                    "ShareQ", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, $"Import failed: {ex.Message}",
                "ShareQ", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    private void OnDeleteCustomUploaderClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Control { Tag: string filePath }) return;
        if (!System.IO.File.Exists(filePath)) return;
        var name = System.IO.Path.GetFileName(filePath);
        var result = System.Windows.MessageBox.Show(this,
            $"Delete '{name}'? Already-running uploads finish; this uploader stops being available after the next restart.",
            "ShareQ", System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Warning,
            System.Windows.MessageBoxResult.Cancel);
        if (result != System.Windows.MessageBoxResult.OK) return;
        try
        {
            System.IO.File.Delete(filePath);
            if (DataContext is SettingsViewModel vm) vm.Uploaders.LoadCustomUploaders();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, $"Delete failed: {ex.Message}",
                "ShareQ", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    private void OnOpenCustomUploadersFolderClicked(object sender, RoutedEventArgs e)
    {
        var folder = ShareQ.CustomUploaders.CustomUploaderRegistry.DefaultFolder;
        try
        {
            System.IO.Directory.CreateDirectory(folder); // first run — create on demand
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = folder,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, $"Couldn't open folder: {ex.Message}",
                "ShareQ", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    private void OnOpenImageEffectsClicked(object sender, RoutedEventArgs e)
    {
        // Wire the SQLite-backed preset store so changes survive across window opens. The
        // viewmodel auto-loads on construction and persists each mutation through a 600 ms
        // debounce, so a slider sweep doesn't hammer the database. _settingsStore goes in too
        // so the window restores its last position / size between sessions, same as MainWindow.
        var vm = new ShareQ.App.ViewModels.ImageEffects.ImageEffectsViewModel(
            ShareQ.ImageEffects.ImageEffectRegistry.Default,
            _imageEffectPresetStore);
        var window = new ShareQ.App.Views.ImageEffectsWindow(vm, _settingsStore) { Owner = this };
        window.Show();
    }

    private async void OnExportSettingsClicked(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export ShareQ settings",
            Filter = "JSON|*.json|All files|*.*",
            FileName = $"shareq-settings-{DateTime.Now:yyyy-MM-dd-HHmm}.json",
            DefaultExt = ".json",
            AddExtension = true,
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            await _settingsBackup.ExportAsync(dlg.FileName).ConfigureAwait(true);
            System.Windows.MessageBox.Show(this, $"Settings exported to:\n{dlg.FileName}",
                "ShareQ", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, $"Export failed: {ex.Message}",
                "ShareQ", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    private async void OnImportSettingsClicked(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import ShareQ settings",
            Filter = "JSON|*.json|All files|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };
        if (dlg.ShowDialog(this) != true) return;
        // Confirm before overwriting — import isn't destructive (it merges, doesn't wipe), but
        // it does silently rewrite existing keys, which the user should know.
        var ok = System.Windows.MessageBox.Show(this,
            $"Import settings from:\n{dlg.FileName}\n\nExisting settings and categories with the same name will be overwritten. Pinned items already present (matching content) are skipped. Settings not in the file stay untouched. Continue?",
            "ShareQ — Import settings", System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Question);
        if (ok != System.Windows.MessageBoxResult.OK) return;
        try
        {
            var result = await _settingsBackup.ImportAsync(dlg.FileName).ConfigureAwait(true);
            var skippedNote = result.PinnedSkipped > 0 ? $" ({result.PinnedSkipped} duplicate pinned skipped)" : string.Empty;
            System.Windows.MessageBox.Show(this,
                $"Imported {result.Settings} setting(s), {result.Categories} categor{(result.Categories == 1 ? "y" : "ies")}, {result.PinnedItems} pinned item(s){skippedNote}.\n\nSome changes (theme, hotkeys) apply immediately; others may require restarting ShareQ to take effect.",
                "ShareQ", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, $"Import failed: {ex.Message}",
                "ShareQ", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    private static ShapeColor? TryParseShapeColor(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        var s = hex.Trim().TrimStart('#');
        if (s.Length is not (6 or 8)) return null;
        try
        {
            byte a = 255, r, g, b;
            if (s.Length == 8)
            {
                a = Convert.ToByte(s[..2], 16); r = Convert.ToByte(s[2..4], 16);
                g = Convert.ToByte(s[4..6], 16); b = Convert.ToByte(s[6..8], 16);
            }
            else
            {
                r = Convert.ToByte(s[..2], 16); g = Convert.ToByte(s[2..4], 16); b = Convert.ToByte(s[4..6], 16);
            }
            return new ShapeColor(a, r, g, b);
        }
        catch { return null; }
    }
}
