using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AresToys.App.Services;
using AresToys.App.ViewModels;
using AresToys.Editor.Model;
using AresToys.Editor.Views;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using MenuItem = System.Windows.Controls.MenuItem;

namespace AresToys.App;

public partial class MainWindow : FluentWindow
{
    private const string StepDragFormat = "AresToys.WorkflowStep";
    private Point _dragStartPoint;
    private WorkflowStepViewModel? _dragSourceStep;
    private readonly AresToys.App.Services.ScreenColorPickerService _screenSampler;
    private readonly AresToys.Editor.Persistence.ColorRecentsStore _colorRecents;
    private readonly AresToys.App.Services.SettingsBackupService _settingsBackup;
    private readonly AresToys.PluginContracts.IPluginConfigStoreFactory _pluginConfigFactory;
    private readonly AresToys.Uploaders.OAuth.OAuthFlowService _oauthFlowService;
    private readonly AresToys.Pipeline.Profiles.IPipelineProfileStore _profileStore;
    private readonly AresToys.Storage.Settings.ISettingsStore _settingsStore;
    private readonly AresToys.Storage.ImageEffects.IImageEffectPresetStore _imageEffectPresetStore;
    private readonly AresToys.App.Services.LocalizationService _localization;
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
        AresToys.App.Services.ScreenColorPickerService screenSampler,
        AresToys.Editor.Persistence.ColorRecentsStore colorRecents,
        AresToys.App.Services.SettingsBackupService settingsBackup,
        AresToys.PluginContracts.IPluginConfigStoreFactory pluginConfigFactory,
        AresToys.Uploaders.OAuth.OAuthFlowService oauthFlowService,
        AresToys.Pipeline.Profiles.IPipelineProfileStore profileStore,
        AresToys.Storage.Settings.ISettingsStore settingsStore,
        AresToys.Storage.ImageEffects.IImageEffectPresetStore imageEffectPresetStore,
        AresToys.App.Services.LocalizationService localization)
    {
        InitializeComponent();
        AresToys.App.Services.DarkTitleBar.SuppressResizeFlicker(this);
        AresToys.App.Services.DarkTitleBar.EnlargeResizeHitZones(this);
        DataContext = viewModel;
        _screenSampler = screenSampler;
        _colorRecents = colorRecents;
        _settingsBackup = settingsBackup;
        _pluginConfigFactory = pluginConfigFactory;
        _oauthFlowService = oauthFlowService;
        _profileStore = profileStore;
        _settingsStore = settingsStore;
        _imageEffectPresetStore = imageEffectPresetStore;
        _localization = localization;
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
                new(Services.TrayIconService.NoneMarker, AresToys.App.Resources.Strings.Settings_TrayDoNothing),
            };
            // Localize built-in workflow display names so the picker matches what the user sees
            // elsewhere; custom profiles (renamed by the user) keep their stored DisplayName.
            foreach (var p in profiles
                .Select(p => (Profile: p, Display: AresToys.App.Services.WorkflowDisplayNameLocalizer.Localize(p.Id, p.DisplayName)))
                .OrderBy(t => t.Display, StringComparer.OrdinalIgnoreCase))
                options.Add(new TrayClickProfileOption(p.Profile.Id, p.Display));

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
        nameof(TrayLeftClickCombo) =>   (Services.TrayIconService.LeftClickKey,   AresToys.Pipeline.Profiles.DefaultPipelineProfiles.OpenSettingsId),
        nameof(TrayDoubleClickCombo) => (Services.TrayIconService.DoubleClickKey, AresToys.Pipeline.Profiles.DefaultPipelineProfiles.ShowPopupId),
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

    // ── Language picker (Settings → App settings) ───────────────────────────────────
    // ItemsSource = LocalizationService.AvailableLanguages, SelectedValuePath = Tag (the
    // resx culture suffix or "" for "system default"). Persistence + thread-culture switch
    // happen inside LocalizationService.CurrentTag setter; we just bridge SelectionChanged.
    private bool _suppressLanguagePersist;

    private sealed record LanguageOption(string Tag, string DisplayName);

    private void OnLanguageComboLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.ComboBox combo) return;
        var options = AresToys.App.Services.LocalizationService.AvailableLanguages
            .Select(l => new LanguageOption(l.Tag,
                l.Tag == AresToys.App.Services.LocalizationService.SystemDefaultMarker
                    ? AresToys.App.Resources.Strings.Settings_LanguageSystemDefault
                    : l.DisplayName))
            .ToList();
        _suppressLanguagePersist = true;
        try
        {
            combo.DisplayMemberPath = nameof(LanguageOption.DisplayName);
            combo.SelectedValuePath = nameof(LanguageOption.Tag);
            combo.ItemsSource = options;
            combo.SelectedValue = _localization.CurrentTag ?? string.Empty;
        }
        finally { _suppressLanguagePersist = false; }
    }

    private void OnLanguageComboChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_suppressLanguagePersist) return;
        if (sender is not System.Windows.Controls.ComboBox combo) return;
        if (combo.SelectedValue is not string tag) return;
        // CurrentTag setter persists + applies to thread + raises CultureChanged. Subscribers
        // (TrayIconService, LocalizedStrings singleton) refresh their bindings live.
        _localization.CurrentTag = tag;
    }

    /// <summary>Enter inside a Wormholes-tab geometry TextBox commits the current text via the
    /// binding's UpdateSource and tabs to the next field. Without this, Enter would just stay
    /// inside the TextBox and the user would have to click elsewhere to push the value through.</summary>
    private void OnGeometryKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter || sender is not System.Windows.Controls.TextBox tb) return;
        var binding = tb.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty);
        binding?.UpdateSource();
        tb.MoveFocus(new System.Windows.Input.TraversalRequest(System.Windows.Input.FocusNavigationDirection.Next));
        e.Handled = true;
    }

    /// <summary>Select-all on focus for the Wormholes-tab numeric inputs — usual text editor
    /// gesture so retyping a value doesn't require dragging the selection. WPF doesn't do this
    /// by default; <see cref="OnGeometryPreviewMouseDown"/> below handles the mouse-click case
    /// (which otherwise places the caret at the click position before SelectAll runs).</summary>
    private void OnGeometryGotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.TextBox tb) tb.SelectAll();
    }

    private void OnGeometryPreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox tb) return;
        if (tb.IsKeyboardFocusWithin) return; // already focused — let the user position the caret normally
        e.Handled = true;
        tb.Focus(); // triggers GotKeyboardFocus → OnGeometryGotFocus → SelectAll
    }

    /// <summary>Re-spawn the app process and tear down the current one. Used by the language
    /// picker's "Restart" button: most surfaces re-translate live via {Markup:Loc} bindings, but
    /// already-rendered tray submenus and the few hard-coded labels we haven't migrated yet only
    /// pick up the new culture on a fresh boot. Process restart is the cheap, robust way to
    /// guarantee the whole UI flips, regardless of which surface the user lands on next.</summary>
    private void OnRestartAppClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = false,
                });
            }
        }
        catch { /* best-effort relaunch — Shutdown still runs so the user can re-open manually */ }
        Application.Current.Shutdown();
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

    /// <summary>Block keystrokes that produce filesystem-illegal characters in a folder
    /// path. Combines Windows' invalid-filename-char list with the path separators (\, /)
    /// and the colon — none of those make sense inside a sub-folder pattern token. Multi-
    /// character text input (IME composition, drag-drop) goes through here too, so we check
    /// every char in the pending Text. The <c>%</c> needed for tokens IS allowed (not on the
    /// invalid list).</summary>
    private void OnSubFolderPatternPreviewTextInput(object sender, System.Windows.Input.TextCompositionEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text)) return;
        if (e.Text.AsSpan().IndexOfAny(InvalidPathChars()) >= 0) e.Handled = true;
    }

    /// <summary>Sanitize text on paste (Ctrl+V / right-click Paste). Strip invalid chars
    /// rather than reject outright — pasting "foo:bar/baz" should land as "foobarbaz" so the
    /// user gets at least the salvageable parts (consistent with Explorer's rename dialog).</summary>
    private void OnSubFolderPatternPasting(object sender, System.Windows.DataObjectPastingEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox tb) return;
        if (!e.SourceDataObject.GetDataPresent(System.Windows.DataFormats.UnicodeText)) return;
        var raw = e.SourceDataObject.GetData(System.Windows.DataFormats.UnicodeText) as string ?? string.Empty;
        var bad = InvalidPathChars();
        var clean = new System.Text.StringBuilder(raw.Length);
        foreach (var c in raw)
            if (Array.IndexOf(bad, c) < 0) clean.Append(c);
        if (clean.Length == raw.Length) return; // nothing to clean — let the paste through
        e.CancelCommand();                       // suppress the original paste
        var caret = tb.SelectionStart;
        var text = tb.Text ?? string.Empty;
        var selected = tb.SelectionLength;
        if (selected > 0) text = text.Remove(caret, selected);
        tb.Text = text.Insert(caret, clean.ToString());
        tb.SelectionStart = caret + clean.Length;
        tb.SelectionLength = 0;
    }

    /// <summary>Filesystem-illegal chars for a single path segment. <c>Path.GetInvalidFileNameChars</c>
    /// covers the Windows-reserved set (&lt;&gt;:"/\|?* + control chars); <c>:</c> is already in
    /// there for filenames but we re-include path separators just in case the .NET runtime
    /// changes the list. Static cached so each keystroke / paste doesn't re-allocate.</summary>
    private static char[] _invalidPathChars = BuildInvalidPathChars();
    private static char[] InvalidPathChars() => _invalidPathChars;
    private static char[] BuildInvalidPathChars()
    {
        var fileSet = System.IO.Path.GetInvalidFileNameChars();
        var combined = new System.Collections.Generic.HashSet<char>(fileSet);
        combined.Add('\\');
        combined.Add('/');
        combined.Add(':');
        return combined.ToArray();
    }

    /// <summary>Token chip click on the Capture-Settings sub-folder pattern editor: insert
    /// the chip's <c>Tag</c> string (e.g. <c>%y</c>) at the textbox's caret position and
    /// move the caret to right after the inserted token. Each chip has Focusable=False so
    /// the click doesn't move focus off the textbox; if focus had drifted elsewhere we
    /// re-Focus the textbox here so subsequent typing lands in the right field.</summary>
    private void OnPatternTokenClick(object sender, RoutedEventArgs e)
    {
        // Sender is ui:Button (Wpf.Ui.Controls.Button) which derives from
        // System.Windows.Controls.Primitives.ButtonBase, NOT from System.Windows.Controls.Button.
        // Casting to Button missed the chips entirely and the handler returned silently —
        // ButtonBase covers both vanilla Button and any wpf-ui themed variant.
        if (sender is not System.Windows.Controls.Primitives.ButtonBase btn || btn.Tag is not string token) return;
        if (SubFolderPatternBox is null) return;
        var caret = SubFolderPatternBox.SelectionStart;
        var text = SubFolderPatternBox.Text ?? string.Empty;
        SubFolderPatternBox.Text = text.Insert(caret, token);
        SubFolderPatternBox.SelectionStart = caret + token.Length;
        SubFolderPatternBox.SelectionLength = 0;
        SubFolderPatternBox.Focus();
    }

    /// <summary>Builds the "+ Add step" categorized context menu on demand. Doing this in
    /// code-behind keeps the XAML clean and avoids the WPF HierarchicalDataTemplate gotchas
    /// around binding Command on dynamically-templated leaf MenuItems.
    /// <para>
    /// A search box sits at the top of the menu — typing in it hides the per-category sub-menus
    /// and replaces them with a flat filtered list of every matching task across categories.
    /// Empty search restores the categorised view. Implementation detail: the TextBox lives
    /// inside a MenuItem so ContextMenu's hit-testing / keyboard navigation still treats it as
    /// a row; we mark the wrapper <c>StaysOpenOnClick</c> so clicking into the box doesn't
    /// dismiss the menu, and focus the box on opened so the user can just start typing.
    /// </para></summary>
    private void OnAddStepButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        if (DataContext is not SettingsViewModel vm) return;

        var menu = new ContextMenu
        {
            PlacementTarget = btn,
            Placement = PlacementMode.Bottom,
        };

        // Snapshot the categorised model + a flat list of every action with its localised name.
        // The flat list drives the filtered results; the categorised list drives the default view.
        // Building both up-front avoids re-allocating on every keystroke — the WorkflowActionCatalog
        // result is stable for the lifetime of this menu.
        var groups = vm.Workflows.Editor.AddableActions;
        var allActions = groups
            .SelectMany(g => g.Actions.Select(a => (
                Action: a,
                Title: Services.WorkflowActionLocalizer.LocalizeTitle(a.TaskId, a.DisplayName, a.LocalizationKey),
                Description: Services.WorkflowActionLocalizer.LocalizeDescription(a.TaskId, a.Description, a.LocalizationKey),
                Category: Services.WorkflowActionLocalizer.LocalizeCategory(g.Category, g.Category))))
            .ToList();

        // Search-box row. Sits as the first MenuItem; the TextBox itself is the MenuItem.Header
        // so it inherits the menu's row chrome (padding, hover) without us re-styling. We
        // disable WPF's "auto-collapse on click" by handling PreviewMouseDown to keep the menu
        // open while the user types.
        var searchBox = new Wpf.Ui.Controls.TextBox
        {
            Margin = new Thickness(0),
            MinWidth = 220,
            VerticalAlignment = VerticalAlignment.Center,
            PlaceholderText = AresToys.App.Resources.Strings.WorkflowEditor_AddStepSearchPlaceholder,
        };
        var searchItem = new MenuItem
        {
            Header = searchBox,
            StaysOpenOnClick = true,
        };
        // Without this the MenuItem swallows the click and the TextBox never gets focus.
        searchItem.PreviewMouseLeftButtonDown += (s, args) =>
        {
            if (!searchBox.IsKeyboardFocusWithin)
            {
                searchBox.Focus();
                args.Handled = true;
            }
        };
        menu.Items.Add(searchItem);
        menu.Items.Add(new Separator());

        // Build the categorised view once — we toggle Visibility on these items based on the
        // search box. Keeping them around (rather than rebuilding) preserves any user state
        // (highlighted sub-menu, expanded category) if they clear the search and come back.
        var categoryItems = new List<MenuItem>();
        foreach (var group in groups)
        {
            var groupItem = new MenuItem
            {
                Header = Services.WorkflowActionLocalizer.LocalizeCategory(group.Category, group.Category),
            };
            foreach (var action in group.Actions)
            {
                var title = Services.WorkflowActionLocalizer.LocalizeTitle(action.TaskId, action.DisplayName, action.LocalizationKey);
                var leaf = new MenuItem
                {
                    Header = BuildAddStepItemHeader(title, action.Inputs, action.Outputs),
                    ToolTip = Services.WorkflowActionLocalizer.LocalizeDescription(action.TaskId, action.Description, action.LocalizationKey),
                };
                var capturedDescriptor = action;
                leaf.Click += (_, _) => vm.Workflows.Editor.AddStepCommand.Execute(capturedDescriptor);
                groupItem.Items.Add(leaf);
            }
            categoryItems.Add(groupItem);
            menu.Items.Add(groupItem);
        }

        // Index of the first slot that flat-search items get inserted into. Anything we add to
        // the menu during search lives here; we wipe this range when the search text changes
        // so we don't accumulate stale matches.
        var flatItems = new List<MenuItem>();

        searchBox.TextChanged += (_, _) =>
        {
            // Drop any existing flat results.
            foreach (var item in flatItems) menu.Items.Remove(item);
            flatItems.Clear();

            var query = searchBox.Text?.Trim() ?? string.Empty;
            if (query.Length == 0)
            {
                // Empty search → restore the categorised view.
                foreach (var item in categoryItems) item.Visibility = Visibility.Visible;
                return;
            }

            // Filtered view: hide categories, show a flat list under the search box. Match is
            // case-insensitive substring against the localised title, description, OR localised
            // category name (so typing "io" still finds Save-as-Image when the I/O category is
            // what the user remembers).
            foreach (var item in categoryItems) item.Visibility = Visibility.Collapsed;
            var matches = allActions
                .Where(a =>
                    a.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    a.Description.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    a.Category.Contains(query, StringComparison.OrdinalIgnoreCase))
                .OrderBy(a => a.Title)
                .Take(40);  // cap so a wide match doesn't drop a 200-row wall on the user
            foreach (var (action, title, description, category) in matches)
            {
                var leaf = new MenuItem
                {
                    // "Title — Category" suffix so the user sees where a match came from
                    // without having to dismiss + reopen the menu to confirm.
                    Header = BuildAddStepItemHeader($"{title}  ·  {category}", action.Inputs, action.Outputs),
                    ToolTip = description,
                };
                var captured = action;
                leaf.Click += (_, _) => vm.Workflows.Editor.AddStepCommand.Execute(captured);
                menu.Items.Add(leaf);
                flatItems.Add(leaf);
            }
        };

        // Auto-focus the search box on open so the user can just start typing — the Loaded
        // callback runs after the menu's popup has materialised, otherwise the focus call is a
        // no-op (TextBox doesn't have a visual tree yet).
        menu.Opened += (_, _) => Dispatcher.BeginInvoke(new Action(() => searchBox.Focus()), DispatcherPriority.Input);

        btn.ContextMenu = menu;
        menu.IsOpen = true;
    }

    /// <summary>Compose a MenuItem header that surfaces a task's input + output port types
    /// alongside its title. Layout is a 3-column Grid: input stripes hugging the left edge,
    /// label in the middle, output stripes hugging the right edge — same colour codes the
    /// workflow editor uses for the step's own port strips (azzurro = Payload, rosa = Text,
    /// giallo = Color). When a task has 0 inputs or 0 outputs the corresponding gutter
    /// stays empty rather than collapsing — visual alignment across rows reads cleaner. Multi-
    /// port tasks (rare — the only common case is "two outputs") stack stripes adjacent within
    /// the same gutter, narrowest first.</summary>
    private static FrameworkElement BuildAddStepItemHeader(
        string label,
        IReadOnlyList<AresToys.App.ViewModels.WorkflowPort>? inputs,
        IReadOnlyList<AresToys.App.ViewModels.WorkflowPort>? outputs)
    {
        const double StripeWidth = 4;
        const double StripeGap = 1;
        const double GutterMargin = 6;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var inputStrip = new StackPanel { Orientation = Orientation.Horizontal };
        if (inputs is { Count: > 0 })
        {
            foreach (var port in inputs)
            {
                inputStrip.Children.Add(BuildPortStripe(port, StripeWidth, StripeGap));
            }
            inputStrip.Margin = new Thickness(0, 0, GutterMargin, 0);
        }
        Grid.SetColumn(inputStrip, 0);

        var text = new System.Windows.Controls.TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(text, 1);

        var outputStrip = new StackPanel { Orientation = Orientation.Horizontal };
        if (outputs is { Count: > 0 })
        {
            foreach (var port in outputs)
            {
                outputStrip.Children.Add(BuildPortStripe(port, StripeWidth, StripeGap));
            }
            outputStrip.Margin = new Thickness(GutterMargin, 0, 0, 0);
        }
        Grid.SetColumn(outputStrip, 2);

        grid.Children.Add(inputStrip);
        grid.Children.Add(text);
        grid.Children.Add(outputStrip);
        return grid;
    }

    private static FrameworkElement BuildPortStripe(AresToys.App.ViewModels.WorkflowPort port, double width, double gap)
    {
        // Reuse WorkflowPortEntry's frozen brushes so the colour palette stays in one place —
        // any future palette tweak in the workflow editor propagates here automatically.
        var entry = new AresToys.App.ViewModels.WorkflowPortEntry(port, isInput: true);
        return new System.Windows.Shapes.Rectangle
        {
            Width = width,
            Fill = entry.Background,
            VerticalAlignment = VerticalAlignment.Stretch,
            Margin = new Thickness(0, 0, gap, 0),
            ToolTip = port.ToString(),
        };
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
    private enum AccentChannel { Background, Foreground, Dark, ForegroundDark, Delete, Surface1, Surface2, Surface3, OuterBorder, InnerBorder }

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

    private void OnOuterBorderSwatchClick(object sender, MouseButtonEventArgs e)
        => PickAccentColor(AccentChannel.OuterBorder);

    private void OnInnerBorderSwatchClick(object sender, MouseButtonEventArgs e)
        => PickAccentColor(AccentChannel.InnerBorder);

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
            AccentChannel.OuterBorder    => vm.Theme.OuterBorderHex,
            AccentChannel.InnerBorder    => vm.Theme.InnerBorderHex,
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
            case AccentChannel.OuterBorder:    vm.Theme.OuterBorderHex = hex2; break;
            case AccentChannel.InnerBorder:    vm.Theme.InnerBorderHex = hex2; break;
        }
        // Push to recents so the colour shows up next time the user opens any picker.
        _ = _colorRecents.PushAsync(picked, System.Threading.CancellationToken.None);
    }

    private void OnSxcuAssociationCheckBoxLoaded(object sender, RoutedEventArgs e)
    {
        // Read live registry state every time the checkbox renders — the user might have
        // changed the association from another tool (or another AresToys install) since we last
        // looked. Suppress the Click handler during this initial sync via the IsLoaded check.
        if (sender is System.Windows.Controls.CheckBox cb)
            cb.IsChecked = AresToys.App.Services.SxcuFileAssociation.IsRegistered();
    }

    private void OnSxieAssociationCheckBoxLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.CheckBox cb)
            cb.IsChecked = AresToys.App.Services.SxieFileAssociation.IsRegistered();
    }

    private void OnSxieAssociationToggled(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.CheckBox cb) return;
        try
        {
            if (cb.IsChecked == true)
                AresToys.App.Services.SxieFileAssociation.Register();
            else
                AresToys.App.Services.SxieFileAssociation.Unregister();
        }
        catch (Exception ex)
        {
            cb.IsChecked = AresToys.App.Services.SxieFileAssociation.IsRegistered();
            System.Windows.MessageBox.Show(this,
                $"Couldn't update the .sxie association:\n{ex.Message}",
                "AresToys", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
    }

    /// <summary>"Pick…" button on the Add-category row → open the IconPickerDialog and write
    /// back into <see cref="CategoriesViewModel.NewCategoryIcon"/>. Clear is treated as "remove
    /// icon" (empty string).</summary>
    private void OnPickCategoryIconClicked(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm) return;
        var dialog = new AresToys.App.Views.IconPickerDialog(vm.Categories.NewCategoryIcon) { Owner = this };
        if (dialog.ShowOwnerScopedDialog() == true)
            vm.Categories.NewCategoryIcon = dialog.PickedGlyph;
    }

    /// <summary>"Pick…" button on a category row → open the dialog and write back into the row
    /// VM's Icon. The row VM is passed via Button.Tag (set in the DataTemplate). The Icon setter
    /// itself triggers SaveAsync via the VM's OnIconChanged partial, so no extra plumbing here.</summary>
    private void OnPickRowIconClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button btn) return;
        if (btn.Tag is not AresToys.App.ViewModels.CategoryRowViewModel row) return;
        var dialog = new AresToys.App.Views.IconPickerDialog(row.Icon) { Owner = this };
        if (dialog.ShowOwnerScopedDialog() == true)
            row.Icon = dialog.PickedGlyph;
    }

    /// <summary>Autosave hook for category-row text / number fields. Fires on focus loss (after
    /// the binding has already pushed the new value to the VM via UpdateSourceTrigger=LostFocus)
    /// and runs the row's Save command. Default-row fields are disabled so this only ever fires
    /// for user-modifiable rows.</summary>
    private void OnCategoryFieldCommitted(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement el) return;
        if (el.DataContext is not AresToys.App.ViewModels.CategoryRowViewModel row) return;
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
        if (btn.DataContext is not AresToys.App.ViewModels.CategoryRowViewModel row) return;
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
                AresToys.App.Services.SxcuFileAssociation.Register();
            else
                AresToys.App.Services.SxcuFileAssociation.Unregister();
        }
        catch (Exception ex)
        {
            // Roll the visual state back if the registry write failed (UAC-restricted user
            // hive scenarios, antivirus interference) so the checkbox doesn't lie about state.
            cb.IsChecked = AresToys.App.Services.SxcuFileAssociation.IsRegistered();
            System.Windows.MessageBox.Show(this,
                $"Couldn't update file association:\n{ex.Message}",
                "AresToys", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    private void OnExplorerContextMenuCheckBoxLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.CheckBox cb)
            cb.IsChecked = AresToys.App.Services.ExplorerContextMenuRegistration.IsRegistered();
    }

    private void OnExplorerContextMenuToggled(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.CheckBox cb) return;
        try
        {
            if (cb.IsChecked == true)
                AresToys.App.Services.ExplorerContextMenuRegistration.Register();
            else
                AresToys.App.Services.ExplorerContextMenuRegistration.Unregister();
        }
        catch (Exception ex)
        {
            cb.IsChecked = AresToys.App.Services.ExplorerContextMenuRegistration.IsRegistered();
            System.Windows.MessageBox.Show(this,
                $"Couldn't update Explorer menu entry:\n{ex.Message}",
                "AresToys", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
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
                ? AresToys.Pipeline.Profiles.DefaultPipelineProfiles.ManualUploadId
                : stored;
            // If the stored id no longer exists (profile deleted), fall back to manual-upload so
            // the combo always shows a real selection rather than blank.
            if (combo.SelectedValue is null)
                combo.SelectedValue = AresToys.Pipeline.Profiles.DefaultPipelineProfiles.ManualUploadId;
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
                "AresToys", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
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
        var updater = (AresToys.Updater.UpdaterService?)app.Services?.GetService(typeof(AresToys.Updater.UpdaterService));
        if (updater is null) return;

        CheckForUpdatesButton.IsEnabled = false;
        UpdateStatusText.Text = "Checking…";
        UpdateStatusText.Visibility = System.Windows.Visibility.Visible;
        try
        {
            var outcome = await updater.CheckInteractivelyAsync(System.Threading.CancellationToken.None);
            UpdateStatusText.Text = outcome.Kind switch
            {
                AresToys.Updater.CheckOutcomeKind.NotInstalled => Loc("Update_StatusNotInstalled"),
                AresToys.Updater.CheckOutcomeKind.AlreadyLatest => Loc("Update_StatusLatest"),
                AresToys.Updater.CheckOutcomeKind.UpdateFound => LocFormat("Update_StatusFoundFormat", outcome.Version ?? string.Empty),
                AresToys.Updater.CheckOutcomeKind.Failed => LocFormat("Update_StatusFailedFormat", outcome.ErrorMessage ?? string.Empty),
                _ => Loc("Update_StatusUnknown"),
            };

            static string Loc(string key)
            {
                var culture = AresToys.App.Markup.LocalizedStrings.Instance.Culture
                              ?? System.Globalization.CultureInfo.CurrentUICulture;
                return AresToys.App.Resources.Strings.ResourceManager.GetString(key, culture) ?? key;
            }

            static string LocFormat(string key, params object[] args)
            {
                var culture = AresToys.App.Markup.LocalizedStrings.Instance.Culture
                              ?? System.Globalization.CultureInfo.CurrentUICulture;
                var template = AresToys.App.Resources.Strings.ResourceManager.GetString(key, culture) ?? key;
                // CA1863 wants a cached CompositeFormat — fires once per update-check click on
                // a runtime-loaded template; caching per (key, culture) costs more than it saves.
#pragma warning disable CA1863
                return string.Format(culture, template, args);
#pragma warning restore CA1863
            }
            // The UpdateAvailable event already fired and queued the toast; surface the install
            // dialog here too so the user who just clicked "Check" doesn't have to chase the toast.
            if (outcome.Kind == AresToys.Updater.CheckOutcomeKind.UpdateFound && outcome.Info is not null)
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
        var dlg = new AresToys.App.Views.UploaderConfigDialog(row.Uploader, store, _oauthFlowService) { Owner = this };
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
        var folder = AresToys.CustomUploaders.CustomUploaderRegistry.DefaultFolder;
        try
        {
            System.IO.Directory.CreateDirectory(folder);
            var imported = 0;
            foreach (var src in dlg.FileNames)
            {
                // Validate first so we don't litter the folder with garbage. Empty / malformed
                // files surface a friendly error in the same dialog the user came from.
                var json = System.IO.File.ReadAllText(src);
                var cfg = AresToys.CustomUploaders.CustomUploaderConfigLoader.Parse(json);
                if (!AresToys.CustomUploaders.CustomUploaderConfigLoader.IsValid(cfg))
                {
                    System.Windows.MessageBox.Show(this,
                        $"'{System.IO.Path.GetFileName(src)}' is missing Name or RequestURL — skipping.",
                        "AresToys", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
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
                    $"Imported {imported} file(s). Restart AresToys to make the new uploaders selectable above.",
                    "AresToys", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, $"Import failed: {ex.Message}",
                "AresToys", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    private void OnDeleteCustomUploaderClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Control { Tag: string filePath }) return;
        if (!System.IO.File.Exists(filePath)) return;
        var name = System.IO.Path.GetFileName(filePath);
        var result = System.Windows.MessageBox.Show(this,
            $"Delete '{name}'? Already-running uploads finish; this uploader stops being available after the next restart.",
            "AresToys", System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Warning,
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
                "AresToys", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    private void OnOpenCustomUploadersFolderClicked(object sender, RoutedEventArgs e)
    {
        var folder = AresToys.CustomUploaders.CustomUploaderRegistry.DefaultFolder;
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
                "AresToys", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    private void OnOpenImageEffectsClicked(object sender, RoutedEventArgs e)
    {
        // Wire the SQLite-backed preset store so changes survive across window opens. The
        // viewmodel auto-loads on construction and persists each mutation through a 600 ms
        // debounce, so a slider sweep doesn't hammer the database. _settingsStore goes in too
        // so the window restores its last position / size between sessions, same as MainWindow.
        var vm = new AresToys.App.ViewModels.ImageEffects.ImageEffectsViewModel(
            AresToys.ImageEffects.ImageEffectRegistry.Default,
            _imageEffectPresetStore);
        var window = new AresToys.App.Views.ImageEffectsWindow(vm, _settingsStore) { Owner = this };
        window.Show();
    }

    private async void OnExportSettingsClicked(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export AresToys settings",
            Filter = "JSON|*.json|All files|*.*",
            FileName = $"arestoys-settings-{DateTime.Now:yyyy-MM-dd-HHmm}.json",
            DefaultExt = ".json",
            AddExtension = true,
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            await _settingsBackup.ExportAsync(dlg.FileName).ConfigureAwait(true);
            System.Windows.MessageBox.Show(this, $"Settings exported to:\n{dlg.FileName}",
                "AresToys", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, $"Export failed: {ex.Message}",
                "AresToys", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    private async void OnImportSettingsClicked(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import AresToys settings",
            Filter = "JSON|*.json|All files|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };
        if (dlg.ShowDialog(this) != true) return;
        // Confirm before overwriting — import isn't destructive (it merges, doesn't wipe), but
        // it does silently rewrite existing keys, which the user should know.
        var ok = System.Windows.MessageBox.Show(this,
            $"Import settings from:\n{dlg.FileName}\n\nExisting settings and categories with the same name will be overwritten. Pinned items already present (matching content) are skipped. Settings not in the file stay untouched. Continue?",
            "AresToys — Import settings", System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Question);
        if (ok != System.Windows.MessageBoxResult.OK) return;
        try
        {
            var result = await _settingsBackup.ImportAsync(dlg.FileName).ConfigureAwait(true);
            var skippedNote = result.PinnedSkipped > 0 ? $" ({result.PinnedSkipped} duplicate pinned skipped)" : string.Empty;
            System.Windows.MessageBox.Show(this,
                $"Imported {result.Settings} setting(s), {result.Categories} categor{(result.Categories == 1 ? "y" : "ies")}, {result.PinnedItems} pinned item(s){skippedNote}.\n\nSome changes (theme, hotkeys) apply immediately; others may require restarting AresToys to take effect.",
                "AresToys", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, $"Import failed: {ex.Message}",
                "AresToys", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
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
