using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ShareQ.Editor.Adorners;
using ShareQ.Editor.HitTesting;
using ShareQ.Editor.Model;
using ShareQ.Editor.Tools;
using ShareQ.Editor.ViewModels;
using Wpf.Ui.Controls;

namespace ShareQ.Editor.Views;

public partial class EditorWindow : FluentWindow
{
    private readonly EditorViewModel _vm;
    private double _gestureStartX, _gestureStartY;

    private double _zoom = 1.0;
    private double _minZoom = 0.1;
    private const double MaxZoom = 8.0;
    private bool _initialFitDone;
    /// <summary>True after <see cref="EnableFullscreen"/> is called by the launcher. Switches
    /// the initial fit pass to "always fit to viewport" — including upscale beyond 100% — so a
    /// small image fills the screen instead of sitting tiny in the middle of a maximised window.</summary>
    private bool _fitToScreenMode;

    // Marquee state (Select tool, drag on empty area).
    private bool _isMarqueeing;
    private System.Windows.Shapes.Rectangle? _marqueeRect;
    private double _marqueeStartX, _marqueeStartY;

    // Move-drag state (Select tool): _moveStartShape captures the shape at mouse-down,
    // _moveOriginalShape captures the shape as it was when first selected (for undo span).
    private Shape? _moveStartShape;
    private double _moveAnchorX, _moveAnchorY;
    private bool _isDraggingShape;
    // Snapshot of selected shapes at mouse-down, used to compute translation across all selected items.
    private List<Shape>? _moveStartSnapshots;

    // Live-edit tracking: per-shape original snapshots, so a sequence of property tweaks (or drag-to-move)
    // commits as ONE undo step per shape when the selection changes (or window closes).
    private List<Shape> _liveEditOriginals = [];
    private bool _suppressLiveUpdates;

    // Inline text editing: a TextBox is parented to the canvas at the click point.
    // _editingTextShape is non-null only when modifying an existing TextShape.
    private System.Windows.Controls.TextBox? _activeTextBox;
    private TextShape? _editingTextShape;

    // Text-frame drag state: when the user drags with the Text tool active we draw a marquee
    // and use its final bounds as the new TextShape's box. Mouse-up below the threshold falls
    // back to a click-and-default-size at the down point.
    private bool _isDrawingTextFrame;
    private double _textFrameStartX, _textFrameStartY;
    private System.Windows.Shapes.Rectangle? _textFrameMarquee;
    private const double TextFrameDragThreshold = 4;

    // Grip drag state (resize / endpoint / font-size). Only active for single-selection in Select tool.
    private GripKind _activeGrip = GripKind.None;
    private Shape? _gripStartShape;
    private double _gripAnchorX, _gripAnchorY;

    // Eyedropper state. While non-null, the next canvas click samples a pixel and feeds it back.
    private Action<ShapeColor?>? _eyedropperContinuation;

    // RMB-pan state. While the right button is held over the canvas, dragging the mouse
    // translates the ScrollViewer's scroll offsets — same gesture Photoshop / Aseprite / Figma
    // use for canvas pan. We capture the screen-space cursor at mouse-down (RootVisual avoids
    // jitter from the scroll motion itself moving the viewport relative to CanvasHost) and the
    // initial scroll offsets; MouseMove computes a delta and adjusts the scrollbars.
    private bool _isPanning;
    private System.Windows.Point _panStartCursor;
    private double _panStartScrollH;
    private double _panStartScrollV;
    private System.Windows.Input.Cursor? _panSavedCursor;

    private readonly ShareQ.Core.Imaging.IImageEncoder? _encoder;

    // Crop magnifier — built lazily on first mouse-move while the Crop tool is active,
    // hidden when the tool changes. The Image inside shows a NearestNeighbor-scaled chunk of
    // SourceImage centered on the cursor so the user can see where pixel boundaries fall.
    private System.Windows.Controls.Border? _cropMagnifier;
    private System.Windows.Controls.Image? _cropMagnifierImage;

    public EditorWindow(EditorViewModel viewModel, ShareQ.Core.Imaging.IImageEncoder? encoder = null)
    {
        InitializeComponent();
        _vm = viewModel;
        DataContext = _vm;
        _encoder = encoder;

        OutlineSwatch.SelectedColor = _vm.OutlineColor;
        FillSwatch.SelectedColor = _vm.FillColor;
        TextSwatch.SelectedColor = _vm.CurrentTextStyle.Color;
        OutlineSwatch.SetBinding(ColorSwatchButton.SelectedColorProperty,
            new System.Windows.Data.Binding(nameof(EditorViewModel.OutlineColor)) { Mode = System.Windows.Data.BindingMode.TwoWay });
        FillSwatch.SetBinding(ColorSwatchButton.SelectedColorProperty,
            new System.Windows.Data.Binding(nameof(EditorViewModel.FillColor)) { Mode = System.Windows.Data.BindingMode.TwoWay });
        StrokeSlider.SetBinding(Slider.ValueProperty,
            new System.Windows.Data.Binding(nameof(EditorViewModel.StrokeWidth)) { Mode = System.Windows.Data.BindingMode.TwoWay });
        // TextSwatch can't bind directly because TextStyle is an immutable record — instead we
        // hook the swatch's SelectedColor change and project it back into a fresh CurrentTextStyle.
        // Reverse direction (style change → swatch refresh) is handled in the PropertyChanged
        // handler below to keep the two views in sync after Set-as-current / external mutations.
        var textSwatchDescriptor = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(
            ColorSwatchButton.SelectedColorProperty, typeof(ColorSwatchButton));
        textSwatchDescriptor?.AddValueChanged(TextSwatch, (_, _) =>
        {
            var current = _vm.CurrentTextStyle;
            if (TextSwatch.SelectedColor.Equals(current.Color)) return;
            _vm.CurrentTextStyle = current with { Color = TextSwatch.SelectedColor };
        });

        _vm.Shapes.CollectionChanged += OnShapesChanged;
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EditorViewModel.PreviewShape)) RedrawPreview();
            if (e.PropertyName == nameof(EditorViewModel.SourcePngBytes)) LoadSourceImage();
            if (e.PropertyName == nameof(EditorViewModel.SelectedShape)) OnSelectedShapeChanged();
            if (e.PropertyName == nameof(EditorViewModel.CurrentTool))
            {
                CommitInlineTextEdit();
                RefreshToolButtonHighlight();
                RefreshPropertyPanel();
                // Crop magnifier is tool-specific — switching to anything else means we need to
                // pull it off the canvas, otherwise it sticks at its last position forever.
                if (_vm.CurrentTool != EditorTool.Crop) HideCropMagnifier();
            }
            if (e.PropertyName == nameof(EditorViewModel.CurrentTextStyle))
            {
                // Reverse-sync: when the VM's text style changes (e.g. Set-as-current adopts
                // the selected TextShape's colour), pull the new colour into the swatch so
                // both views stay aligned. The forward direction (swatch click → vm) is
                // wired via the DependencyPropertyDescriptor above.
                if (!TextSwatch.SelectedColor.Equals(_vm.CurrentTextStyle.Color))
                    TextSwatch.SelectedColor = _vm.CurrentTextStyle.Color;
            }
        };
        _vm.SelectedShapes.CollectionChanged += (_, e) =>
        {
            RedrawAll();
            // Re-snapshot live-edit originals only on real selection changes (Add/Remove/Reset),
            // NOT on Replace (which fires during LiveReplaceShape).
            if (e.Action != System.Collections.Specialized.NotifyCollectionChangedAction.Replace)
            {
                OnSelectionSetChanged();
            }
        };

        // Realtime property panel: each change live-updates the shape via DependencyPropertyDescriptor hooks.
        var swatchColorDesc = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(
            ColorSwatchButton.SelectedColorProperty, typeof(ColorSwatchButton));
        swatchColorDesc?.AddValueChanged(SelOutlineSwatch, (_, _) => OnSelOutlineChanged());
        swatchColorDesc?.AddValueChanged(SelFillSwatch, (_, _) => OnSelFillChanged());
        SelStrokeSlider.ValueChanged += (_, _) => OnSelStrokeChanged();
        SelRotationSlider.ValueChanged += (_, _) => OnSelRotationSliderChanged();
        SelEffectSlider.ValueChanged += (_, _) => OnSelEffectSliderChanged();
        SelEffectBox.LostFocus += (_, _) => OnSelEffectBoxCommitted();
        SelEffectBox.KeyDown += (_, ev) => { if (ev.Key == Key.Enter) OnSelEffectBoxCommitted(); };
        SelEffectSecondarySlider.ValueChanged += (_, _) => OnSelEffectSecondarySliderChanged();
        SelEffectSecondaryBox.LostFocus += (_, _) => OnSelEffectSecondaryBoxCommitted();
        SelEffectSecondaryBox.KeyDown += (_, ev) => { if (ev.Key == Key.Enter) OnSelEffectSecondaryBoxCommitted(); };
        SelRotationBox.LostFocus += (_, _) => OnSelRotationBoxCommitted();
        SelRotationBox.KeyDown += (_, ev) => { if (ev.Key == Key.Enter) OnSelRotationBoxCommitted(); };
        SelFreehandSmoothCheck.Checked += (_, _) => OnSelFreehandSmoothChanged();
        SelFreehandSmoothCheck.Unchecked += (_, _) => OnSelFreehandSmoothChanged();
        SelFreehandEndArrowCheck.Checked += (_, _) => OnSelFreehandEndArrowChanged();
        SelFreehandEndArrowCheck.Unchecked += (_, _) => OnSelFreehandEndArrowChanged();

        WireFontPickers();
        SelFontSizeSlider.ValueChanged += (_, _) => OnSelFontSizeSliderChanged();
        SelFontSizeBox.LostFocus += (_, _) => OnSelFontSizeBoxCommitted();
        SelFontSizeBox.KeyDown += (_, ev) => { if (ev.Key == Key.Enter) OnSelFontSizeBoxCommitted(); };
        SelBoldCheck.Checked += (_, _) => OnSelTextStyleChanged();
        SelBoldCheck.Unchecked += (_, _) => OnSelTextStyleChanged();
        SelItalicCheck.Checked += (_, _) => OnSelTextStyleChanged();
        SelItalicCheck.Unchecked += (_, _) => OnSelTextStyleChanged();

        SelStepBoldCheck.Checked += (_, _) => OnSelStepFontStyleChanged();
        SelStepBoldCheck.Unchecked += (_, _) => OnSelStepFontStyleChanged();
        SelStepItalicCheck.Checked += (_, _) => OnSelStepFontStyleChanged();
        SelStepItalicCheck.Unchecked += (_, _) => OnSelStepFontStyleChanged();

        var swatchColorDescAlt = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(
            ColorSwatchButton.SelectedColorProperty, typeof(ColorSwatchButton));
        swatchColorDescAlt?.AddValueChanged(SelTextColorSwatch, (_, _) => OnSelTextStyleChanged());

        Loaded += (_, _) =>
        {
            LoadSourceImage();
            RefreshPropertyPanel();
            RefreshToolButtonHighlight();
            // Defer fit to after layout pass so ViewportWidth/Height is populated.
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () => FitZoomToViewport());
            ColorSwatchButton.EyedropperHandler = continuation =>
            {
                EnterCanvasEyedropperMode(continuation);
                return null;
            };
        };
        Closing += OnClosing;
    }

    public bool Saved { get; private set; }

    /// <summary>PNG bytes captured at Save-click time, BEFORE <see cref="System.Windows.Window.Close"/>
    /// runs. Hosts (EditorLauncher) read this from the <c>Closed</c> event handler instead of
    /// calling <see cref="ExportCanvasPng"/> there directly — by the time the visual tree gets
    /// the Closed event, the window has been disconnected from its PresentationSource and the
    /// RenderTargetBitmap of CanvasHost ends up blank (the user's "the workflow's clipboard
    /// copy is empty" bug). Capturing inside <c>OnSaveClicked</c> while the canvas is still
    /// attached produces the real bytes.</summary>
    public byte[]? SavedPng { get; private set; }

    /// <summary>Cross-assembly handoff for the Effects tool. The host (ShareQ.App.EditorLauncher)
    /// wires this once at startup with a function that opens its <c>ImageEffectsWindow</c>
    /// preloaded with the editor's source bytes; on Apply, the function returns the rendered
    /// PNG, which the editor swaps in via an undoable <c>ReplaceSourceCommand</c>. Returns
    /// null when the user cancels (closes the window without applying). Same pattern as
    /// <see cref="ColorSwatchButton.EyedropperHandler"/>.</summary>
    public static Func<byte[], System.Windows.Window?, Task<byte[]?>>? OpenEffectsHandler { get; set; }

    /// <summary>Cross-assembly handoff for the Trace tool. Takes source bytes + owner window
    /// (for Save dialog parenting); the host opens its own dialog and writes the .svg to
    /// disk so the editor stays I/O-free. Fire-and-forget — the editor doesn't get the SVG
    /// back since tracing produces a separate vector artifact rather than a new raster.</summary>
    public static Func<byte[], System.Windows.Window?, Task>? TraceHandler { get; set; }

    private async void OnEffectsClicked(object sender, RoutedEventArgs e)
    {
        var handler = OpenEffectsHandler;
        if (handler is null || _vm.SourcePngBytes.Length == 0) return;
        try
        {
            var result = await handler(_vm.SourcePngBytes, this);
            if (result is null || result.Length == 0) return;
            _vm.ApplyReplaceSource(result);
        }
        catch
        {
            // Effects flow is opportunistic — a failure (decode error, missing preset store)
            // shouldn't crash the editor. The host's handler also logs internally.
        }
    }

    private async void OnTraceClicked(object sender, RoutedEventArgs e)
    {
        var handler = TraceHandler;
        if (handler is null || _vm.SourcePngBytes.Length == 0) return;
        TraceBtn.IsEnabled = false;
        try { await handler(_vm.SourcePngBytes, this); }
        catch
        {
            // Trace failures are logged inside the handler (host-side); the editor stays open.
        }
        finally { TraceBtn.IsEnabled = true; }
    }

    /// <summary>Render the canvas (image + shapes) to PNG bytes. Hosts (EditorLauncher) call
    /// this from the <c>Closed</c> handler when <see cref="Saved"/> is true; the in-window
    /// "Save as…" button calls it inline. Hides selection adorners (dashed bbox + grip handles)
    /// during the render so the export captures only the user-drawn artwork — without this the
    /// confirm flow would bake the bright-blue selection rectangle into the screenshot.</summary>
    public byte[] ExportCanvasPng()
    {
        var canvasHost = (System.Windows.Controls.Grid)FindName("CanvasHost")!;
        var (exportW, exportH, dpiX, dpiY) = ResolveExportPixels(canvasHost);

        // Adorners (selection bbox + every resize/rotate/bend grip) all live as direct children
        // of DrawingCanvas with Tag="adorner" — see RefreshSelectionAdorner. Collapse them for
        // the render, restore in the finally. Restoring matters for "Save as…" (which keeps
        // editing); for the confirm flow the window is about to close so cleanup is moot.
        var hidden = new List<UIElement>();
        foreach (UIElement child in DrawingCanvas.Children)
        {
            if (child is FrameworkElement fe
                && fe.Visibility == Visibility.Visible
                && (fe.Tag as string) == "adorner")
            {
                hidden.Add(child);
            }
        }
        foreach (var a in hidden) a.Visibility = Visibility.Collapsed;
        try
        {
            return ShareQ.Editor.Rendering.CanvasPngExporter.Export(canvasHost, exportW, exportH, dpiX, dpiY);
        }
        finally
        {
            foreach (var a in hidden) a.Visibility = Visibility.Visible;
        }
    }

    /// <summary>Localised label dictionary pushed by the host (ShareQ.App.EditorLauncher) just
    /// before <see cref="System.Windows.Window.ShowDialog"/>. Same handoff pattern as
    /// <see cref="ColorPickerWindow.ApplyLocalization"/> — this assembly can't reach the App's
    /// resx, so the host resolves every key against its own ResourceManager and pushes the
    /// strings here. Missing keys leave the XAML defaults intact.</summary>
    private IReadOnlyDictionary<string, string> _labels = new Dictionary<string, string>();

    private string Loc(string key, string fallback) =>
        _labels.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v) ? v : fallback;

    public void ApplyLocalization(IReadOnlyDictionary<string, string> labels)
    {
        _labels = labels ?? new Dictionary<string, string>();

        if (_labels.TryGetValue("TitleBar", out var titleBar)) TitleBarText.Text = titleBar;

        // Tool palette tooltips. The icon is the visual; tooltip carries the localised name +
        // (where present) the keyboard hint as a single string per resx entry.
        if (_labels.TryGetValue("Tool_Select", out var s)) SelectToolBtn.ToolTip = s;
        if (_labels.TryGetValue("Tool_Rectangle", out var r)) RectangleToolBtn.ToolTip = r;
        if (_labels.TryGetValue("Tool_Ellipse", out var el)) EllipseToolBtn.ToolTip = el;
        if (_labels.TryGetValue("Tool_Line", out var ln)) LineToolBtn.ToolTip = ln;
        if (_labels.TryGetValue("Tool_Arrow", out var ar)) ArrowToolBtn.ToolTip = ar;
        if (_labels.TryGetValue("Tool_Freehand", out var fh)) FreehandToolBtn.ToolTip = fh;
        if (_labels.TryGetValue("Tool_Text", out var tx)) TextToolBtn.ToolTip = tx;
        if (_labels.TryGetValue("Tool_Step", out var st)) StepToolBtn.ToolTip = st;
        if (_labels.TryGetValue("Tool_Image", out var im)) ImageBtn.ToolTip = im;
        if (_labels.TryGetValue("Tool_Blur", out var bl)) BlurToolBtn.ToolTip = bl;
        if (_labels.TryGetValue("Tool_Pixelate", out var px)) PixelateToolBtn.ToolTip = px;
        if (_labels.TryGetValue("Tool_Spotlight", out var sp)) SpotlightToolBtn.ToolTip = sp;
        if (_labels.TryGetValue("Tool_SmartEraser", out var se)) SmartEraserToolBtn.ToolTip = se;
        if (_labels.TryGetValue("Tool_Crop", out var cr)) CropToolBtn.ToolTip = cr;
        if (_labels.TryGetValue("Tool_Resize", out var rz)) ResizeBtn.ToolTip = rz;
        if (_labels.TryGetValue("Tool_Trace", out var tr)) TraceBtn.ToolTip = tr;

        // Action triplet (top of right column).
        if (_labels.TryGetValue("Save", out var save)) SaveButton.Content = save;
        if (_labels.TryGetValue("SaveAs", out var saveAs)) SaveAsButton.Content = saveAs;
        if (_labels.TryGetValue("Cancel", out var cancel)) CancelButton.Content = cancel;
        if (_labels.TryGetValue("TooltipSave", out var tSave)) SaveButton.ToolTip = tSave;
        if (_labels.TryGetValue("TooltipSaveAs", out var tSaveAs)) SaveAsButton.ToolTip = tSaveAs;

        // Default properties block.
        if (_labels.TryGetValue("DefaultProperties", out var dp)) DefaultPropertiesTitle.Text = dp;
        if (_labels.TryGetValue("Outline", out var outl)) OutlineLabel.Text = outl;
        if (_labels.TryGetValue("Fill", out var fl)) FillLabel.Text = fl;
        if (_labels.TryGetValue("Text", out var txtLbl)) TextLabel.Text = txtLbl;
        if (_labels.TryGetValue("Stroke", out var strk)) StrokeLabel.Text = strk;
        if (_labels.TryGetValue("ApplyToSelected", out var aps)) ApplyToSelectedBtn.Content = aps;
        if (_labels.TryGetValue("TooltipApplyToSelected", out var tAps)) ApplyToSelectedBtn.ToolTip = tAps;

        // No-selection placeholder + dynamic title (live default, refreshed in RefreshPropertyPanel).
        if (_labels.TryGetValue("NoSelection", out var ns)) NoSelectionText.Text = ns;
        if (_labels.TryGetValue("Properties", out var props)) PanelTitleText.Text = props;

        // Selected-shape labels.
        if (_labels.TryGetValue("Outline", out var sOutl)) SelOutlineLabel.Text = sOutl;
        if (_labels.TryGetValue("Fill", out var sFl)) SelFillLabel.Text = sFl;
        if (_labels.TryGetValue("Stroke", out var sStrk)) SelStrokeLabel.Text = sStrk;
        if (_labels.TryGetValue("Rotation", out var rot)) SelRotationLabel.Text = rot;
        if (_labels.TryGetValue("TextColor", out var tc)) SelTextColorLabel.Text = tc;
        if (_labels.TryGetValue("Font", out var fnt))
        {
            SelFontLabel.Text = fnt;
            SelStepFontLabel.Text = fnt;
        }
        if (_labels.TryGetValue("Size", out var sz)) SelSizeLabel.Text = sz;
        if (_labels.TryGetValue("Alignment", out var al)) SelAlignmentLabel.Text = al;
        if (_labels.TryGetValue("Bold", out var bold))
        {
            SelBoldCheck.Content = bold;
            SelStepBoldCheck.Content = bold;
        }
        if (_labels.TryGetValue("Italic", out var ital))
        {
            SelItalicCheck.Content = ital;
            SelStepItalicCheck.Content = ital;
        }
        if (_labels.TryGetValue("SmoothStroke", out var ss)) SelFreehandSmoothCheck.Content = ss;
        if (_labels.TryGetValue("EndArrow", out var ea)) SelFreehandEndArrowCheck.Content = ea;
        if (_labels.TryGetValue("TooltipSmoothStroke", out var tSs)) SelFreehandSmoothCheck.ToolTip = tSs;
        if (_labels.TryGetValue("TooltipEndArrow", out var tEa)) SelFreehandEndArrowCheck.ToolTip = tEa;

        // "Set as default" button + tooltip.
        if (_labels.TryGetValue("SetAsDefault", out var sad)) SetAsDefaultBtn.Content = sad;
        if (_labels.TryGetValue("TooltipSetAsDefault", out var tSad)) SetAsDefaultBtn.ToolTip = tSad;

        // Footer.
        if (_labels.TryGetValue("Undo", out var undo)) UndoButton.Content = undo;
        if (_labels.TryGetValue("Redo", out var redo)) RedoButton.Content = redo;

        // Re-run RefreshPropertyPanel so dynamic strings (panel title, effect labels) pick up
        // the new dictionary on the next paint — the populate path reads through Loc() now.
        RefreshPropertyPanel();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // Cleanup that runs whichever path closes the window.
        CommitInlineTextEdit();
        CommitPendingLiveEdit();

        // Skip the prompt when the user explicitly clicked Save (Saved=true) or when nothing was
        // changed. Cancel button + the X button + Alt+F4 all land here with Saved=false; if there
        // are unsaved changes we ask for confirmation. Yes saves and closes, No discards and
        // closes, Cancel keeps the editor open.
        if (!Saved && _vm.HasUnsavedChanges)
        {
            var result = System.Windows.MessageBox.Show(
                "The image has unsaved changes. Save before closing?",
                "ShareQ editor",
                System.Windows.MessageBoxButton.YesNoCancel,
                System.Windows.MessageBoxImage.Warning,
                System.Windows.MessageBoxResult.Yes);
            if (result == System.Windows.MessageBoxResult.Cancel)
            {
                e.Cancel = true;
                return;
            }
            if (result == System.Windows.MessageBoxResult.Yes)
            {
                Saved = true;
                _vm.MarkSaved();
            }
            // No → fall through and close without saving.
        }

        ColorSwatchButton.EyedropperHandler = null;
        CancelEyedropper();
    }

    private void LoadSourceImage()
    {
        if (_vm.SourcePngBytes.Length == 0) return;
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = new MemoryStream(_vm.SourcePngBytes);
        bmp.EndInit();
        bmp.Freeze();
        SourceImage.Source = bmp;
        DrawingCanvas.Width = bmp.PixelWidth;
        DrawingCanvas.Height = bmp.PixelHeight;
        SourceImage.Width = bmp.PixelWidth;
        SourceImage.Height = bmp.PixelHeight;
    }

    private void OnSelectToolClicked(object sender, RoutedEventArgs e) => _vm.CurrentTool = EditorTool.Select;
    private void OnRectangleToolClicked(object sender, RoutedEventArgs e) => _vm.CurrentTool = EditorTool.Rectangle;
    private void OnArrowToolClicked(object sender, RoutedEventArgs e) => _vm.CurrentTool = EditorTool.Arrow;
    private void OnLineToolClicked(object sender, RoutedEventArgs e) => _vm.CurrentTool = EditorTool.Line;
    private void OnEllipseToolClicked(object sender, RoutedEventArgs e) => _vm.CurrentTool = EditorTool.Ellipse;
    private void OnFreehandToolClicked(object sender, RoutedEventArgs e) => _vm.CurrentTool = EditorTool.Freehand;
    private void OnTextToolClicked(object sender, RoutedEventArgs e) => _vm.CurrentTool = EditorTool.Text;
    private void OnStepToolClicked(object sender, RoutedEventArgs e) => _vm.CurrentTool = EditorTool.StepCounter;
    private void OnBlurToolClicked(object sender, RoutedEventArgs e) => _vm.CurrentTool = EditorTool.Blur;
    private void OnPixelateToolClicked(object sender, RoutedEventArgs e) => _vm.CurrentTool = EditorTool.Pixelate;
    private void OnSpotlightToolClicked(object sender, RoutedEventArgs e) => _vm.CurrentTool = EditorTool.Spotlight;
    private void OnCropToolClicked(object sender, RoutedEventArgs e) => _vm.CurrentTool = EditorTool.Crop;
    private void OnSmartEraserToolClicked(object sender, RoutedEventArgs e) => _vm.CurrentTool = EditorTool.SmartEraser;
    private void OnResizeClicked(object sender, RoutedEventArgs e) => OpenResizeDialog();
    private void OnImageClicked(object sender, RoutedEventArgs e) => InsertImageFromFile();

    private void RefreshToolButtonHighlight()
    {
        var buttons = new[]
        {
            (Btn: SelectToolBtn, Tool: EditorTool.Select),
            (Btn: RectangleToolBtn, Tool: EditorTool.Rectangle),
            (Btn: ArrowToolBtn, Tool: EditorTool.Arrow),
            (Btn: LineToolBtn, Tool: EditorTool.Line),
            (Btn: EllipseToolBtn, Tool: EditorTool.Ellipse),
            (Btn: FreehandToolBtn, Tool: EditorTool.Freehand),
            (Btn: TextToolBtn, Tool: EditorTool.Text),
            (Btn: StepToolBtn, Tool: EditorTool.StepCounter),
            (Btn: BlurToolBtn, Tool: EditorTool.Blur),
            (Btn: PixelateToolBtn, Tool: EditorTool.Pixelate),
            (Btn: SpotlightToolBtn, Tool: EditorTool.Spotlight),
            (Btn: CropToolBtn, Tool: EditorTool.Crop),
            (Btn: SmartEraserToolBtn, Tool: EditorTool.SmartEraser)
        };
        foreach (var (btn, tool) in buttons)
        {
            btn.Appearance = tool == _vm.CurrentTool
                ? Wpf.Ui.Controls.ControlAppearance.Primary
                : Wpf.Ui.Controls.ControlAppearance.Secondary;
        }
    }
    private void OnUndoClicked(object sender, RoutedEventArgs e) => _vm.UndoCommand.Execute(null);
    private void OnRedoClicked(object sender, RoutedEventArgs e) => _vm.RedoCommand.Execute(null);
    private void InsertImageFromFile()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Insert image",
            Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp)|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|All files (*.*)|*.*",
            CheckFileExists = true
        };
        if (dlg.ShowDialog(this) != true) return;

        BitmapSource bs;
        try
        {
            var bmp = new System.Windows.Media.Imaging.BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(dlg.FileName);
            bmp.EndInit();
            bmp.Freeze();
            bs = bmp;
        }
        catch (Exception) { return; }

        InsertImageShape(bs);
    }

    private void PasteImageFromClipboard()
    {
        BitmapSource? bs;
        try
        {
            if (!System.Windows.Clipboard.ContainsImage()) return;
            bs = System.Windows.Clipboard.GetImage();
        }
        catch (System.Runtime.InteropServices.COMException) { return; }
        if (bs is null) return;
        InsertImageShape(bs);
    }

    /// <summary>Static cache of the last copy/cut from inside the editor. Holds the actual
    /// <see cref="Shape"/> records (immutable, so safe to share). Paste consults this whenever
    /// our clipboard sentinel <see cref="ShapeClipboardFormat"/> is still on the system clipboard
    /// — that's how we tell "the user is pasting our own copy" apart from "the user grabbed
    /// something from another app". Survives editor close/reopen (the field is static), but
    /// gets invalidated the moment any other app pushes data to the clipboard, since the
    /// sentinel disappears with it.</summary>
    private static IReadOnlyList<Shape>? _shapeClipboard;

    /// <summary>Custom DataObject format key for the round-trip sentinel — its value is
    /// irrelevant ("1" works), the presence of the key alone signals "shapes are in our
    /// in-process cache". Versioned so future schema changes can branch cleanly.</summary>
    private const string ShapeClipboardFormat = "ShareQ.Editor.Shapes.v1";

    /// <summary>Pixel offset applied to every pasted shape so the new copy lands beside (not on
    /// top of) the original. Standard convention in vector editors (Figma, Illustrator, etc.).</summary>
    private const double PasteOffset = 12;

    /// <summary>Ctrl+V entry point. Resolution order:
    /// <list type="number">
    /// <item><description>Our sentinel on the clipboard + cached shapes → restore them as
    ///     editable objects (offset by <see cref="PasteOffset"/> so they don't overlap).</description></item>
    /// <item><description>Image on the clipboard → insert as <see cref="ImageShape"/>.</description></item>
    /// <item><description>Text on the clipboard → insert as <see cref="TextShape"/> at the canvas centre.</description></item>
    /// </list>
    /// Mixed HTML+text+image clipboards (e.g. browser drag) hit the image branch — that's usually
    /// what the user wants. Sentinel always wins, so an in-editor copy round-trips losslessly.</summary>
    private void PasteFromClipboard()
    {
        try
        {
            if (System.Windows.Clipboard.ContainsData(ShapeClipboardFormat) && _shapeClipboard is { Count: > 0 } cached)
            {
                var pasted = cached.Select(s => TranslateShape(s, PasteOffset, PasteOffset)).ToList();
                foreach (var s in pasted) _vm.AddShape(s);
                // AddShape selects each individually; final state ends with the last one focused
                // — match the user's expectation that "paste" leaves the freshly-pasted shapes
                // selected so they can immediately drag/edit them.
                _vm.SelectedShapes.Clear();
                foreach (var s in pasted) _vm.SelectedShapes.Add(s);
                return;
            }
            if (System.Windows.Clipboard.ContainsImage())
            {
                PasteImageFromClipboard();
                return;
            }
            if (System.Windows.Clipboard.ContainsText())
            {
                var text = System.Windows.Clipboard.GetText();
                if (!string.IsNullOrEmpty(text)) InsertTextShape(text);
            }
        }
        catch (System.Runtime.InteropServices.COMException) { /* clipboard contention — drop the request */ }
    }

    /// <summary>Add a <see cref="TextShape"/> at the canvas centre using the current style,
    /// pre-populated with <paramref name="text"/>. Skipped when text is empty (matches the
    /// in-place text tool which also rejects empty submissions).</summary>
    private void InsertTextShape(string text)
    {
        var canvasW = DrawingCanvas.Width  > 0 ? DrawingCanvas.Width  : 800;
        var canvasH = DrawingCanvas.Height > 0 ? DrawingCanvas.Height : 600;
        var x = canvasW * 0.1;
        var y = canvasH * 0.1;
        var fontSize = _vm.CurrentTextStyle.FontSize;
        _vm.AddTextShape(new TextShape(x, y,
            TextShape.DefaultWidthFor(fontSize), TextShape.DefaultHeightFor(fontSize),
            text, _vm.CurrentTextStyle, _vm.OutlineColor, ShapeColor.Transparent, _vm.StrokeWidth));
    }

    /// <summary>Ctrl+C / Ctrl+X — export the current selection. Two channels are populated in
    /// parallel on a single <see cref="DataObject"/>:
    /// <list type="bullet">
    /// <item><description>Our private <see cref="ShapeClipboardFormat"/> sentinel — paired with
    ///     a static cache of the actual shape records, so an in-editor round-trip preserves
    ///     them as editable objects (arrows stay arrows, not flattened images).</description></item>
    /// <item><description>A native fallback for other apps: text for a single <see cref="TextShape"/>,
    ///     image for a single <see cref="ImageShape"/>, otherwise a rasterised PNG of the
    ///     selection's bounding rect.</description></item>
    /// </list>
    /// When <paramref name="cut"/> is true, the selection is also removed via the existing
    /// remove-shapes path so undo/redo continue to work.</summary>
    private void CopySelectionToClipboard(bool cut)
    {
        var sels = _vm.SelectedShapes.ToList();
        if (sels.Count == 0) return;
        CommitPendingLiveEdit();

        // Cache the immutable shape records first — even if SetDataObject throws below, the
        // in-process clipboard still has them. Records are immutable so sharing references is
        // safe; paste applies TranslateShape which produces a fresh `with`-cloned instance.
        _shapeClipboard = sels;

        var data = new DataObject();
        data.SetData(ShapeClipboardFormat, "1"); // sentinel — value irrelevant, presence drives paste

        // Native fallback so external apps still see something useful. Single-shape branches
        // map to lossless formats; the catch-all rasterises onto a transparent bitmap.
        try
        {
            switch (sels.Count == 1 ? sels[0] : null)
            {
                case TextShape t when !string.IsNullOrEmpty(t.Text):
                    data.SetText(t.Text);
                    break;
                case ImageShape img when img.PngBytes is { Length: > 0 }:
                    using (var ms = new System.IO.MemoryStream(img.PngBytes))
                    {
                        var decoder = System.Windows.Media.Imaging.BitmapFrame.Create(
                            ms,
                            System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
                            System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                        data.SetImage(decoder);
                    }
                    break;
                default:
                    if (RasterizeSelection(sels) is { } bs) data.SetImage(bs);
                    break;
            }
            // copy: true → clipboard survives this process exiting; required so the user can
            // paste in another app after closing the editor.
            System.Windows.Clipboard.SetDataObject(data, copy: true);
        }
        catch (System.Runtime.InteropServices.COMException) { /* clipboard contention — drop */ }

        if (cut) _vm.RemoveShapes(sels);
    }

    /// <summary>Render the bounding rect of <paramref name="shapes"/> to a transparent
    /// <see cref="BitmapSource"/>. Each shape is materialised through the same factory the
    /// canvas uses (<see cref="MakeUiElement"/>), translated so the bbox top-left lands at
    /// (0, 0), and packed into a throwaway Canvas. RenderTargetBitmap captures the result at
    /// device DPI. Returns null when the selection has zero area (collapsed shapes).</summary>
    private BitmapSource? RasterizeSelection(IReadOnlyList<Shape> shapes)
    {
        var bounds = shapes
            .Select(ComputeBounds)
            .Where(b => b.Width > 0 && b.Height > 0)
            .ToList();
        if (bounds.Count == 0) return null;
        var minX = bounds.Min(b => b.X);
        var minY = bounds.Min(b => b.Y);
        var maxX = bounds.Max(b => b.X + b.Width);
        var maxY = bounds.Max(b => b.Y + b.Height);
        // Pad by stroke width so anti-aliased edges aren't clipped at the bbox border.
        const double pad = 4;
        var w = (int)Math.Ceiling(maxX - minX + pad * 2);
        var h = (int)Math.Ceiling(maxY - minY + pad * 2);
        if (w <= 0 || h <= 0) return null;

        var host = new Canvas { Width = w, Height = h, Background = System.Windows.Media.Brushes.Transparent };
        foreach (var s in shapes)
        {
            var ui = MakeUiElement(s);
            if (ui is FrameworkElement fe)
            {
                // Compose a translation so the selection bbox top-left lands inside the
                // throwaway host. Existing per-shape RenderTransform (rotation) is preserved
                // by stacking via TransformGroup.
                var tg = new TransformGroup();
                if (fe.RenderTransform is { } existing && existing != Transform.Identity)
                    tg.Children.Add(existing);
                tg.Children.Add(new TranslateTransform(-minX + pad, -minY + pad));
                fe.RenderTransform = tg;
            }
            host.Children.Add(ui);
        }
        host.Measure(new Size(w, h));
        host.Arrange(new Rect(0, 0, w, h));

        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(host);
        rtb.Freeze();
        return rtb;
    }

    /// <summary>Encode <paramref name="bs"/> as PNG, fit to ~50% of the canvas, center, add as ImageShape.</summary>
    private void InsertImageShape(BitmapSource bs)
    {
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bs));
        using var ms = new System.IO.MemoryStream();
        encoder.Save(ms);
        var bytes = ms.ToArray();

        var canvasW = DrawingCanvas.Width > 0 ? DrawingCanvas.Width : bs.PixelWidth;
        var canvasH = DrawingCanvas.Height > 0 ? DrawingCanvas.Height : bs.PixelHeight;
        var maxW = canvasW * 0.5;
        var maxH = canvasH * 0.5;
        var scale = Math.Min(1.0, Math.Min(maxW / bs.PixelWidth, maxH / bs.PixelHeight));
        var w = bs.PixelWidth * scale;
        var h = bs.PixelHeight * scale;
        var x = (canvasW - w) / 2;
        var y = (canvasH - h) / 2;

        _vm.AddImageShape(new ImageShape(x, y, w, h, bytes));
    }

    private void OpenResizeDialog()
    {
        if (SourceImage.Source is not BitmapSource src) return;
        var dlg = new ResizeDialog(src.PixelWidth, src.PixelHeight) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            _vm.ApplyResize(dlg.NewWidth, dlg.NewHeight);
        }
    }

    private void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        Saved = true;
        // Capture the canvas BEFORE Close — RenderTargetBitmap on a closed window's visual tree
        // produces a blank bitmap (window detached from its PresentationSource), which surfaced
        // as "Region capture → editor save → empty image on clipboard". With the bytes cached
        // here, the EditorLauncher's Closed handler just reads SavedPng off the window.
        try { SavedPng = ExportCanvasPng(); }
        catch { /* defensive: degraded path = no bytes, caller treats as cancel */ }
        _vm.MarkSaved(); // belt-and-braces: the Closing handler also bypasses the prompt when Saved=true
        Close();
    }

    /// <summary>"Save as…" — explicit user export. The user picks both the path and the format
    /// (via the SaveFileDialog filter); we render the canvas to PNG, encode into the chosen
    /// format, write to disk. Bypasses history entirely — Save-as is a one-shot export of
    /// the current edits, not a state mutation. The editor stays open so the user can keep
    /// editing or hit "Save" afterwards to also commit the result to the history item.</summary>
    private void OnSaveAsClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save image as",
            Filter = "PNG (*.png)|*.png|JPEG (*.jpg;*.jpeg)|*.jpg;*.jpeg|BMP (*.bmp)|*.bmp|GIF (*.gif)|*.gif",
            FilterIndex = 1,
            FileName = $"shareq-{DateTime.Now:yyyyMMdd-HHmmss}.png",
            AddExtension = true,
        };
        if (dialog.ShowDialog(this) != true) return;

        // FilterIndex is 1-based, mirrors the Filter ordering above.
        var target = dialog.FilterIndex switch
        {
            2 => ShareQ.Core.Imaging.ImageFormat.Jpeg,
            3 => ShareQ.Core.Imaging.ImageFormat.Bmp,
            4 => ShareQ.Core.Imaging.ImageFormat.Gif,
            _ => ShareQ.Core.Imaging.ImageFormat.Png,
        };

        try
        {
            // ExportCanvasPng hides the selection adorners during render so the saved file
            // captures only the user-drawn artwork — no dashed selection rect baked in.
            var pngBytes = ExportCanvasPng();

            byte[] outputBytes;
            if (target == ShareQ.Core.Imaging.ImageFormat.Png || _encoder is null)
                outputBytes = pngBytes;
            else
                outputBytes = _encoder.Encode(pngBytes, target, jpegQuality: 90);

            System.IO.File.WriteAllBytes(dialog.FileName, outputBytes);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this,
                $"Could not save the image:\n\n{ex.Message}",
                "Save as failed",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
        }
    }

    /// <summary>Same logic <see cref="ShareQ.App.Services.EditorLauncher"/> uses to size the
    /// export — find the source <c>BitmapSource</c> inside CanvasHost and use its native
    /// pixel dimensions, falling back to the host's ActualWidth/Height if the lookup fails.</summary>
    private static (int W, int H, double DpiX, double DpiY) ResolveExportPixels(System.Windows.Controls.Grid canvasHost)
    {
        foreach (var child in canvasHost.Children)
        {
            if (child is System.Windows.Controls.Image img &&
                img.Source is System.Windows.Media.Imaging.BitmapSource src)
            {
                // Honour the source's embedded DPI metadata. GDI+ (BitBltCaptureSource) writes
                // the system DPI into the PNG, so on a 150% display DpiX is 144 and the
                // exporter MUST use that for the RTB or the visual tree paints at half-width.
                return (src.PixelWidth, src.PixelHeight, src.DpiX, src.DpiY);
            }
        }
        return ((int)Math.Round(canvasHost.ActualWidth), (int)Math.Round(canvasHost.ActualHeight), 96, 96);
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e) { Saved = false; Close(); }

    private void OnZoomInClicked(object sender, RoutedEventArgs e) => SetZoom(_zoom * 1.25);
    private void OnZoomOutClicked(object sender, RoutedEventArgs e) => SetZoom(_zoom / 1.25);
    private void OnZoomResetClicked(object sender, RoutedEventArgs e) => SetZoom(1.0);

    private void OnCanvasMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        var alt = (Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;

        if (ctrl)
        {
            // Zoom anchored at the cursor: capture the canvas-space point under the mouse, then
            // after the LayoutTransform resize, scroll so that point sits where the cursor is.
            // ScaleTransform alone scales from the canvas origin — without the post-scroll the
            // user would see content shift away from their cursor.
            var factor = e.Delta > 0 ? 1.15 : (1.0 / 1.15);
            ZoomAt(_zoom * factor, e.GetPosition(CanvasHost));
            e.Handled = true;
            return;
        }

        if (shift)
        {
            // Shift+wheel = horizontal scroll. Standard Windows convention (Edge/Chrome/VS).
            // We intercept here because WPF's ScrollViewer doesn't translate the modifier
            // automatically. Match the per-notch step the SystemParameters expose so the
            // pacing feels native; SystemParameters.WheelScrollLines is the user's "lines per
            // notch" preference (default 3).
            var step = SystemParameters.WheelScrollLines * 16; // ~16px per logical line
            var hScrollSign = e.Delta > 0 ? -1 : 1;
            CanvasScrollViewer.ScrollToHorizontalOffset(CanvasScrollViewer.HorizontalOffset + hScrollSign * step);
            e.Handled = true;
            return;
        }

        // Wheel (no modifier) over a selection adjusts stroke width on each selected shape;
        // Alt+wheel rotates them. Falls through to scrollviewer scroll when nothing is selected.
        // Live-edit pattern: each notch live-replaces the shape; the existing
        // CommitPendingLiveEdit machinery wraps the whole gesture into one undo step when the
        // selection changes / window closes / Esc is pressed.
        if (_vm.SelectedShapes.Count == 0) return;
        var direction = e.Delta > 0 ? 1 : -1;

        if (alt)
        {
            // 5° per notch — Shift could later quantize to 1° / 15° etc.
            ApplyRotationDelta(direction * 5.0);
            e.Handled = true;
        }
        else
        {
            ApplyStrokeDelta(direction); // ±1 px per notch
            e.Handled = true;
        }
    }

    private void ApplyStrokeDelta(int delta)
    {
        foreach (var s in _vm.SelectedShapes.ToList())
        {
            // Text shapes have no meaningful stroke; the analogous "thickness" knob is the font
            // size. We adjust by 2pt per notch so each scroll feels tactile, clamped 6..200pt.
            if (s is ShareQ.Editor.Model.TextShape t)
            {
                var nextSize = Math.Clamp(t.Style.FontSize + delta * 2.0, 6.0, 200.0);
                if (Math.Abs(nextSize - t.Style.FontSize) < 0.001) continue;
                var replacedText = t with { Style = t.Style with { FontSize = nextSize } };
                _vm.LiveReplaceShape(s, replacedText);
                continue;
            }
            var newStroke = Math.Clamp(s.StrokeWidth + delta, 1.0, 32.0);
            if (Math.Abs(newStroke - s.StrokeWidth) < 0.001) continue;
            var replaced = WithStrokeWidth(s, newStroke);
            if (replaced is null) continue;
            _vm.LiveReplaceShape(s, replaced);
        }
        RefreshPropertyPanel();
    }

    private void ApplyRotationDelta(double degrees)
    {
        foreach (var s in _vm.SelectedShapes.ToList())
        {
            var current = GetRotation(s);
            if (current is null) continue; // shape type doesn't support rotation
            var next = Normalize360(current.Value + degrees);
            var replaced = WithRotation(s, next);
            if (replaced is null) continue;
            _vm.LiveReplaceShape(s, replaced);
        }
        RefreshPropertyPanel();
    }

    private static double Normalize360(double deg)
    {
        var n = deg % 360.0;
        if (n < 0) n += 360.0;
        return n;
    }

    private static Shape? WithStrokeWidth(Shape s, double w) => s switch
    {
        ShareQ.Editor.Model.RectangleShape r  => r  with { StrokeWidth = w },
        ShareQ.Editor.Model.EllipseShape e    => e  with { StrokeWidth = w },
        ShareQ.Editor.Model.TextShape t       => t  with { StrokeWidth = w },
        ShareQ.Editor.Model.ArrowShape a      => a  with { StrokeWidth = w },
        ShareQ.Editor.Model.LineShape l       => l  with { StrokeWidth = w },
        ShareQ.Editor.Model.FreehandShape f   => f  with { StrokeWidth = w },
        ShareQ.Editor.Model.StepCounterShape sc => sc with { StrokeWidth = w },
        // ImageShape / Blur / Pixelate / Spotlight / SmartEraser have no meaningful stroke.
        _ => null,
    };

    private static double? GetRotation(Shape s) => s switch
    {
        ShareQ.Editor.Model.RectangleShape r => r.Rotation,
        ShareQ.Editor.Model.EllipseShape e   => e.Rotation,
        ShareQ.Editor.Model.TextShape t      => t.Rotation,
        ShareQ.Editor.Model.ImageShape img   => img.Rotation,
        ShareQ.Editor.Model.ArrowShape a     => a.Rotation,
        ShareQ.Editor.Model.LineShape l      => l.Rotation,
        ShareQ.Editor.Model.FreehandShape f  => f.Rotation,
        _ => null,
    };

    private static Shape? WithRotation(Shape s, double r) => s switch
    {
        ShareQ.Editor.Model.RectangleShape rect => rect with { Rotation = r },
        ShareQ.Editor.Model.EllipseShape e      => e with { Rotation = r },
        ShareQ.Editor.Model.TextShape t         => t with { Rotation = r },
        ShareQ.Editor.Model.ImageShape img      => img with { Rotation = r },
        ShareQ.Editor.Model.ArrowShape a        => a with { Rotation = r },
        ShareQ.Editor.Model.LineShape l         => l with { Rotation = r },
        ShareQ.Editor.Model.FreehandShape f     => f with { Rotation = r },
        _ => null,
    };

    private void SetZoom(double newZoom)
    {
        var clamped = Math.Clamp(newZoom, _minZoom, MaxZoom);
        if (Math.Abs(clamped - _zoom) < 0.001) return;
        _zoom = clamped;
        CanvasHost.LayoutTransform = new ScaleTransform(_zoom, _zoom);
        ZoomLabel.Text = $"{_zoom * 100:F0}%";
        // Grips are sized in screen pixels via inverse zoom; refresh so the new factor applies.
        RefreshSelectionAdorner();
    }

    /// <summary>Zoom anchored at a canvas-space point (typically the mouse cursor). Applies the
    /// new zoom factor, then scrolls the viewport so the captured point ends up at the same
    /// screen position. Without this the canvas appears to slide away from the cursor on every
    /// wheel notch — disorienting on large images.</summary>
    private void ZoomAt(double newZoom, Point canvasPoint)
    {
        var clamped = Math.Clamp(newZoom, _minZoom, MaxZoom);
        if (Math.Abs(clamped - _zoom) < 0.001) return;
        // Pixel coords (in the viewport) BEFORE the zoom. Same point's pixel coords AFTER zoom
        // is canvasPoint * clamped. The difference is what the viewport needs to scroll by to
        // keep it stationary on screen.
        var beforeX = canvasPoint.X * _zoom;
        var beforeY = canvasPoint.Y * _zoom;
        _zoom = clamped;
        CanvasHost.LayoutTransform = new ScaleTransform(_zoom, _zoom);
        ZoomLabel.Text = $"{_zoom * 100:F0}%";
        var afterX = canvasPoint.X * _zoom;
        var afterY = canvasPoint.Y * _zoom;
        // Defer the scroll adjustment until layout has flushed the new ScaleTransform — without
        // this the ScrollViewer's extents are still the old size and Scroll calls clamp wrong.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render, () =>
        {
            CanvasScrollViewer.ScrollToHorizontalOffset(CanvasScrollViewer.HorizontalOffset + (afterX - beforeX));
            CanvasScrollViewer.ScrollToVerticalOffset(CanvasScrollViewer.VerticalOffset + (afterY - beforeY));
            RefreshSelectionAdorner();
        });
    }

    /// <summary>On first open, zoom out so the entire screenshot fits in the viewport. Capped at 100%
    /// (we never zoom IN if the image already fits). The fit factor becomes the new zoom-out floor —
    /// the user can keep zooming in but not past the initial fit.</summary>
    private void FitZoomToViewport()
    {
        if (_initialFitDone) return;
        if (SourceImage.Source is not BitmapSource src) return;
        var vw = CanvasScrollViewer.ViewportWidth;
        var vh = CanvasScrollViewer.ViewportHeight;
        if (vw <= 0 || vh <= 0) return;

        const double margin = 16;
        var fit = Math.Min((vw - margin) / src.PixelWidth, (vh - margin) / src.PixelHeight);
        // Keep the initial-floor min-zoom (0.1 from the field initialiser). The previous impl
        // raised _minZoom to `fit` for big images, which capped zoom-out at the auto-fit level
        // and felt like a bug. Now the auto-fit only chooses the *initial* zoom; the user can
        // still zoom out further with the slider / Ctrl+wheel.
        if (_fitToScreenMode)
        {
            // Fullscreen launch: always fit the image inside the viewport, including upscale.
            SetZoom(fit);
        }
        else if (fit < 1.0)
        {
            // Default behaviour: only fit when the image is bigger than the viewport — never
            // zoom IN automatically.
            SetZoom(fit);
        }
        _initialFitDone = true;
    }

    /// <summary>Place the window on the monitor whose bounds are <paramref name="x"/>/<paramref name="y"/>/
    /// <paramref name="width"/>/<paramref name="height"/> and maximise into it. Also flips the
    /// initial-zoom pass into "fit-to-screen" mode so the image scales up to fill the viewport
    /// instead of sitting at native pixel size in a giant maximised window.
    ///
    /// Call this BEFORE <c>ShowDialog()</c> — WindowStartupLocation has already been honoured
    /// after Show, so a late call would maximise on whatever monitor the OS picked first.
    ///
    /// Implementation note: a FluentWindow has <c>ExtendsContentIntoTitleBar</c>, so a plain
    /// <c>WindowState=Maximized</c> ends up oversized — the chrome doesn't honour the monitor's
    /// work area and the borderless WindowChrome bleeds 7-8 px past every edge. We hook
    /// WM_GETMINMAXINFO via SourceInitialized and clamp ptMaxPosition / ptMaxSize / ptMaxTrackSize
    /// to the active monitor's work area. Standard WPF borderless-maximised pattern.</summary>
    public void EnableFullscreen(double x, double y, double width, double height)
    {
        WindowStartupLocation = WindowStartupLocation.Manual;
        // Park inside the target monitor BEFORE the maximise — WPF's Maximized snaps to whatever
        // monitor the window currently sits on, so the Left/Top hint is what makes it land here
        // and not on the primary.
        Left = x;
        Top = y;
        Width = Math.Max(640, width / 2);   // fallback restore size when the user un-maximises
        Height = Math.Max(480, height / 2);

        SourceInitialized += (_, _) =>
        {
            var helper = new System.Windows.Interop.WindowInteropHelper(this);
            var src = System.Windows.Interop.HwndSource.FromHwnd(helper.Handle);
            src?.AddHook(MinMaxInfoHook);
            // Maximize NOW, after the hook is in place — the WM_GETMINMAXINFO that fires from
            // this state change will be intercepted and clamped to the monitor work area.
            WindowState = WindowState.Maximized;
        };
        _fitToScreenMode = true;
    }

    private const int WM_GETMINMAXINFO = 0x0024;
    private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFO lpmi);

    /// <summary>Clamp the maximised size to the monitor's WORK AREA (not full screen — leaves
    /// the taskbar visible) and align ptMaxPosition to the monitor origin so a maximize on a
    /// non-primary screen doesn't drag the window back onto the primary.</summary>
    private static IntPtr MinMaxInfoHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_GETMINMAXINFO) return IntPtr.Zero;
        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero) return IntPtr.Zero;
        var info = new MONITORINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfoW(monitor, ref info)) return IntPtr.Zero;
        var mmi = System.Runtime.InteropServices.Marshal.PtrToStructure<MINMAXINFO>(lParam);
        var work = info.rcWork;
        var screen = info.rcMonitor;
        // ptMaxPosition is RELATIVE to the monitor origin in MINMAXINFO semantics.
        mmi.ptMaxPosition.X = Math.Abs(work.Left - screen.Left);
        mmi.ptMaxPosition.Y = Math.Abs(work.Top - screen.Top);
        mmi.ptMaxSize.X = Math.Abs(work.Right - work.Left);
        mmi.ptMaxSize.Y = Math.Abs(work.Bottom - work.Top);
        mmi.ptMaxTrackSize.X = mmi.ptMaxSize.X;
        mmi.ptMaxTrackSize.Y = mmi.ptMaxSize.Y;
        System.Runtime.InteropServices.Marshal.StructureToPtr(mmi, lParam, true);
        handled = true;
        return IntPtr.Zero;
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _eyedropperContinuation is not null) { CancelEyedropper(); e.Handled = true; return; }
        // Skip when the focus is in any text input (caption editing, font-size NumberBox — its
        // inner control is a TextBox, the inline text-shape edit, etc.). The user is likely
        // typing; Enter would otherwise unexpectedly save and Esc would close the editor while
        // they're editing a single field.
        if (e.OriginalSource is System.Windows.Controls.TextBox) return;

        // Editor-level shortcuts: Enter = Save (matches the Save button), Esc = Cancel (matches
        // the Cancel button — the OnClosing handler already prompts for unsaved changes, so we
        // don't need to gate this).
        if (e.Key == Key.Enter)  { OnSaveClicked(this, new RoutedEventArgs()); e.Handled = true; return; }
        if (e.Key == Key.Escape) { OnCancelClicked(this, new RoutedEventArgs()); e.Handled = true; return; }

        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        if (ctrl && e.Key == Key.Z) { _vm.UndoCommand.Execute(null); e.Handled = true; return; }
        if (ctrl && e.Key == Key.Y) { _vm.RedoCommand.Execute(null); e.Handled = true; return; }
        if (ctrl && e.Key == Key.V) { PasteFromClipboard(); e.Handled = true; return; }
        if (ctrl && e.Key == Key.C) { CopySelectionToClipboard(cut: false); e.Handled = true; return; }
        if (ctrl && e.Key == Key.X) { CopySelectionToClipboard(cut: true);  e.Handled = true; return; }

        if (e.Key == Key.Delete || e.Key == Key.Back)
        {
            if (_vm.SelectedShapes.Count > 0)
            {
                CommitPendingLiveEdit(); // Settle any in-flight edit before destroying the selection
                _vm.RemoveShapes([.. _vm.SelectedShapes]);
                e.Handled = true;
                return;
            }
        }

        // Bare-letter tool shortcuts only fire without modifiers — otherwise Ctrl+X / Ctrl+C
        // would bleed through and switch tools after copy / cut. Modifier-bearing keys are
        // handled above (Ctrl+Z / Y / V / C / X) and below (none yet).
        if (ctrl) return;

        switch (e.Key)
        {
            case Key.V: _vm.CurrentTool = EditorTool.Select; e.Handled = true; break;
            case Key.R: _vm.CurrentTool = EditorTool.Rectangle; e.Handled = true; break;
            case Key.A: _vm.CurrentTool = EditorTool.Arrow; e.Handled = true; break;
            case Key.L: _vm.CurrentTool = EditorTool.Line; e.Handled = true; break;
            case Key.E: _vm.CurrentTool = EditorTool.Ellipse; e.Handled = true; break;
            case Key.F: _vm.CurrentTool = EditorTool.Freehand; e.Handled = true; break;
            case Key.T: _vm.CurrentTool = EditorTool.Text; e.Handled = true; break;
            case Key.N: _vm.CurrentTool = EditorTool.StepCounter; e.Handled = true; break;
            case Key.B: _vm.CurrentTool = EditorTool.Blur; e.Handled = true; break;
            case Key.X: _vm.CurrentTool = EditorTool.Pixelate; e.Handled = true; break;
            case Key.O: _vm.CurrentTool = EditorTool.Spotlight; e.Handled = true; break;
            case Key.C: _vm.CurrentTool = EditorTool.Crop; e.Handled = true; break;
            case Key.K: _vm.CurrentTool = EditorTool.SmartEraser; e.Handled = true; break;
            case Key.I: InsertImageFromFile(); e.Handled = true; break;
            default: break;
        }
    }

    private void OnCanvasMouseDown(object sender, MouseButtonEventArgs e)
    {
        // We listen on the ScrollViewer so clicks outside the bitmap area still hit shapes
        // (DrawingCanvas with Transparent Background only hit-tests within Width×Height).
        // Filter out clicks on scrollbar parts and on the inline TextBox editor.
        if (IsScrollBarOrInputControl(e.OriginalSource as DependencyObject)) return;
        var p = e.GetPosition(DrawingCanvas);

        if (_eyedropperContinuation is not null)
        {
            var sampled = SamplePixelAt(p.X, p.Y);
            FinishEyedropper(sampled);
            e.Handled = true;
            return;
        }

        if (_vm.CurrentTool == EditorTool.Text)
        {
            // Start a frame-draw gesture. If the user just clicks (mouse up before
            // dragging past the threshold) we fall back to default-size at the down point;
            // otherwise the marquee bounds become the new TextShape's box.
            _isDrawingTextFrame = true;
            _textFrameStartX = p.X;
            _textFrameStartY = p.Y;
            DrawingCanvas.CaptureMouse();
            e.Handled = true;
            return;
        }

        if (_vm.CurrentTool == EditorTool.Select)
        {
            var shiftPressed = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;

            // Grip hit-test takes priority over shape hit-test, but only on single-selection.
            if (_vm.SelectedShapes.Count == 1)
            {
                var sel = _vm.SelectedShapes[0];
                var rotOffset = sel is RectangleShape or EllipseShape or TextShape ? 25.0 / _zoom : 0;
                var grip = ShapeGripLayout.HitTest(sel, p.X, p.Y, ShapeGripLayout.DefaultHitTolerance / _zoom, rotOffset);
                if (grip != GripKind.None)
                {
                    _activeGrip = grip;
                    _gripStartShape = sel;
                    _gripAnchorX = p.X;
                    _gripAnchorY = p.Y;
                    DrawingCanvas.CaptureMouse();
                    e.Handled = true;
                    return;
                }
            }

            var hit = ShapeHitTester.HitTest(_vm.Shapes, p.X, p.Y);
            if (hit is not null)
            {
                if (e.ClickCount == 2 && hit is TextShape ts)
                {
                    BeginInlineTextEdit(ts.X, ts.Y, existing: ts);
                    e.Handled = true;
                    return;
                }
                if (shiftPressed)
                {
                    // Shift+Click: toggle in selection set. No drag.
                    CommitPendingLiveEdit();
                    var current = _vm.SelectedShapes.ToList();
                    if (!current.Remove(hit)) current.Add(hit);
                    _vm.SetSelection(current);
                    return;
                }

                if (!_vm.SelectedShapes.Contains(hit))
                {
                    CommitPendingLiveEdit();
                    _vm.SetSelection([hit]);
                }
                _moveStartShape = hit;
                _moveAnchorX = p.X;
                _moveAnchorY = p.Y;
                _isDraggingShape = true;
                _moveStartSnapshots = null; // built lazily on first move tick
                DrawingCanvas.CaptureMouse();
            }
            else
            {
                // Click on empty area. Without Shift: clear selection then start marquee.
                // With Shift: keep current selection; marquee will add to it on release.
                if (!shiftPressed)
                {
                    CommitPendingLiveEdit();
                    _vm.SetSelection([]);
                }
                _isMarqueeing = true;
                _marqueeStartX = p.X;
                _marqueeStartY = p.Y;
                _marqueeRect = new System.Windows.Shapes.Rectangle
                {
                    Stroke = new SolidColorBrush(Color.FromArgb(180, 80, 200, 255)),
                    StrokeThickness = 1,
                    StrokeDashArray = [3.0, 2.0],
                    Fill = new SolidColorBrush(Color.FromArgb(40, 80, 200, 255)),
                    IsHitTestVisible = false,
                    Tag = "marquee"
                };
                Canvas.SetLeft(_marqueeRect, p.X);
                Canvas.SetTop(_marqueeRect, p.Y);
                DrawingCanvas.Children.Add(_marqueeRect);
                DrawingCanvas.CaptureMouse();
            }
            return;
        }

        _gestureStartX = p.X;
        _gestureStartY = p.Y;
        DrawingCanvas.CaptureMouse();
        _vm.BeginGesture(p.X, p.Y);
    }

    private void OnCanvasMouseMove(object sender, MouseEventArgs e)
    {
        // RMB pan takes precedence — when the right button is held, every mouse-move
        // translates the scroll offsets and bypasses tool-specific behaviour. Computed in
        // ScrollViewer-space (CanvasScrollViewer) because that's the surface the offsets
        // live on; CanvasHost moves with the scroll, which would feed back into our delta.
        if (_isPanning && e.RightButton == MouseButtonState.Pressed)
        {
            var current = e.GetPosition(CanvasScrollViewer);
            var dx = current.X - _panStartCursor.X;
            var dy = current.Y - _panStartCursor.Y;
            CanvasScrollViewer.ScrollToHorizontalOffset(_panStartScrollH - dx);
            CanvasScrollViewer.ScrollToVerticalOffset(_panStartScrollV - dy);
            e.Handled = true;
            return;
        }

        // Cursor feedback on grip hover (independent of mouse button state).
        UpdateCursorForGripHover(e.GetPosition(DrawingCanvas));

        // Crop tool magnifier — runs on every mouse-move, not just while dragging, so the user
        // can preview where pixels lie before clicking down. Cheap because the underlying
        // CroppedBitmap is just a window into the existing source bitmap (no copy).
        if (_vm.CurrentTool == EditorTool.Crop)
        {
            UpdateCropMagnifier(e.GetPosition(DrawingCanvas));
        }
        else
        {
            HideCropMagnifier();
        }

        if (e.LeftButton != MouseButtonState.Pressed) return;

        if (_isDrawingTextFrame)
        {
            var p = e.GetPosition(DrawingCanvas);
            UpdateTextFrameMarquee(p.X, p.Y);
            return;
        }

        if (_vm.CurrentTool == EditorTool.Select)
        {
            var p = e.GetPosition(DrawingCanvas);
            if (_activeGrip != GripKind.None && _gripStartShape is not null)
            {
                ApplyGripDrag(p.X, p.Y);
                return;
            }
            if (_isMarqueeing && _marqueeRect is not null)
            {
                var x = Math.Min(_marqueeStartX, p.X);
                var y = Math.Min(_marqueeStartY, p.Y);
                Canvas.SetLeft(_marqueeRect, x);
                Canvas.SetTop(_marqueeRect, y);
                _marqueeRect.Width = Math.Abs(p.X - _marqueeStartX);
                _marqueeRect.Height = Math.Abs(p.Y - _marqueeStartY);
                return;
            }
            if (_isDraggingShape && _moveStartShape is not null)
            {
                var dx = p.X - _moveAnchorX;
                var dy = p.Y - _moveAnchorY;
                MoveSelectedShapesBy(dx, dy);
            }
            return;
        }

        var pos = e.GetPosition(DrawingCanvas);
        var (cx, cy) = ApplyShiftConstraint(pos.X, pos.Y);
        _vm.UpdateGesture(cx, cy);
    }

    private void EnterCanvasEyedropperMode(Action<ShapeColor?> continuation)
    {
        _eyedropperContinuation = continuation;
        DrawingCanvas.Cursor = Cursors.Cross;
        // Window stays focused so Esc cancels.
        Focus();
    }

    private void FinishEyedropper(ShapeColor? sampled)
    {
        var cont = _eyedropperContinuation;
        _eyedropperContinuation = null;
        DrawingCanvas.Cursor = null;
        cont?.Invoke(sampled);
    }

    private void CancelEyedropper() => FinishEyedropper(null);

    /// <summary>Sample the pixel at (x, y) of the source bitmap. Returns null when out of bounds
    /// or the source isn't loaded yet.</summary>
    private ShapeColor? SamplePixelAt(double x, double y)
    {
        if (SourceImage.Source is not BitmapSource src) return null;
        var ix = (int)Math.Floor(x);
        var iy = (int)Math.Floor(y);
        if (ix < 0 || iy < 0 || ix >= src.PixelWidth || iy >= src.PixelHeight) return null;

        // Force conversion to a uniform 32-bit BGRA so we can read 4 bytes regardless of source format.
        var conv = new System.Windows.Media.Imaging.FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        var px = new byte[4];
        conv.CopyPixels(new Int32Rect(ix, iy, 1, 1), px, 4, 0);
        return new ShapeColor(px[3], px[2], px[1], px[0]);
    }

    /// <summary>True when the originating element is a scrollbar part or a text input — don't treat
    /// such clicks as canvas gestures.</summary>
    private static bool IsScrollBarOrInputControl(DependencyObject? d)
    {
        while (d is not null)
        {
            if (d is System.Windows.Controls.Primitives.ScrollBar) return true;
            if (d is System.Windows.Controls.TextBox tb && (tb.Tag as string) == "inline-text-editor") return true;
            d = System.Windows.Media.VisualTreeHelper.GetParent(d);
        }
        return false;
    }

    private void UpdateCursorForGripHover(System.Windows.Point p)
    {
        if (_vm.CurrentTool != EditorTool.Select || _vm.SelectedShapes.Count != 1)
        {
            if (DrawingCanvas.Cursor != null) DrawingCanvas.Cursor = null;
            return;
        }
        var sel = _vm.SelectedShapes[0];
        var rotOffset = sel is RectangleShape or EllipseShape or TextShape ? 25.0 / _zoom : 0;
        var grip = ShapeGripLayout.HitTest(sel, p.X, p.Y, ShapeGripLayout.DefaultHitTolerance / _zoom, rotOffset);
        Cursor? c = grip switch
        {
            GripKind.TopLeft or GripKind.BottomRight or GripKind.Resize => Cursors.SizeNWSE,
            GripKind.TopRight or GripKind.BottomLeft => Cursors.SizeNESW,
            GripKind.Top or GripKind.Bottom => Cursors.SizeNS,
            GripKind.Left or GripKind.Right => Cursors.SizeWE,
            GripKind.From or GripKind.To => Cursors.Hand,
            GripKind.Rotate => Cursors.Cross,
            GripKind.Bend => Cursors.Hand,
            _ => null
        };
        if (DrawingCanvas.Cursor != c) DrawingCanvas.Cursor = c;
    }

    /// <summary>Apply a grip drag by computing a new shape from the original snapshot and current pointer.
    /// We always recompute from <see cref="_gripStartShape"/> (not the previous tick) so the result is
    /// monotonic in the drag distance — no cumulative drift.</summary>
    private void ApplyGripDrag(double x, double y)
    {
        if (_gripStartShape is null || _vm.SelectedShapes.Count == 0) return;
        var current = _vm.SelectedShapes[0];
        var shiftPressed = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
        var updated = GripDrag.Transform(_gripStartShape, _activeGrip, x, y, shiftPressed);
        if (updated is null || ReferenceEquals(updated, current)) return;
        _vm.LiveReplaceShape(current, updated);
    }

    /// <summary>Translate every selected shape by (dx, dy) relative to the move-anchor's recorded snapshot.
    /// We track per-selected-shape "start" snapshots in <see cref="_moveStartSnapshots"/>.</summary>
    private void MoveSelectedShapesBy(double dx, double dy)
    {
        if (_moveStartSnapshots is null) BuildMoveStartSnapshots();
        if (_moveStartSnapshots is null) return;

        // Build a parallel list of new shapes and apply via LiveReplaceShape.
        // Iterate by snapshot, find current incarnation (may differ from snapshot if previously replaced).
        var currentList = _vm.SelectedShapes.ToList();
        for (var i = 0; i < _moveStartSnapshots.Count && i < currentList.Count; i++)
        {
            var snapshot = _moveStartSnapshots[i];
            var translated = TranslateShape(snapshot, dx, dy);
            var currentIncarnation = currentList[i];
            _vm.LiveReplaceShape(currentIncarnation, translated);
        }
    }

    private void BuildMoveStartSnapshots()
    {
        _moveStartSnapshots = [.. _vm.SelectedShapes];
    }

    private void OnCanvasMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_isDrawingTextFrame)
        {
            FinishTextFrameDraw(e.GetPosition(DrawingCanvas));
            e.Handled = true;
            return;
        }

        if (_vm.CurrentTool == EditorTool.Select)
        {
            if (_activeGrip != GripKind.None)
            {
                _activeGrip = GripKind.None;
                _gripStartShape = null;
                DrawingCanvas.ReleaseMouseCapture();
                // Grip drag commit happens via OnSelectionSetChanged → CommitPendingLiveEdit
                // when selection changes, or via Closing.
                return;
            }
            if (_isMarqueeing && _marqueeRect is not null)
            {
                var p = e.GetPosition(DrawingCanvas);
                var x = Math.Min(_marqueeStartX, p.X);
                var y = Math.Min(_marqueeStartY, p.Y);
                var w = Math.Abs(p.X - _marqueeStartX);
                var h = Math.Abs(p.Y - _marqueeStartY);

                DrawingCanvas.Children.Remove(_marqueeRect);
                _marqueeRect = null;
                _isMarqueeing = false;
                DrawingCanvas.ReleaseMouseCapture();

                if (w >= 3 && h >= 3)
                {
                    var shiftPressed = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
                    var picked = _vm.Shapes
                        .Where(s => MarqueeIntersects(s, x, y, w, h))
                        .ToList();
                    if (shiftPressed)
                    {
                        // Add to existing selection (deduplicating).
                        var union = _vm.SelectedShapes.ToList();
                        foreach (var s in picked) if (!union.Contains(s)) union.Add(s);
                        _vm.SetSelection(union);
                    }
                    else
                    {
                        _vm.SetSelection(picked);
                    }
                }
            }
            else if (_isDraggingShape)
            {
                DrawingCanvas.ReleaseMouseCapture();
                _isDraggingShape = false;
                _moveStartShape = null;
                _moveStartSnapshots = null;
                // Move's commit happens via OnSelectedShapeChanged when the user changes selection
                // or via Closing → CommitPendingLiveEdit.
            }
            return;
        }

        DrawingCanvas.ReleaseMouseCapture();
        var pos = e.GetPosition(DrawingCanvas);
        var (cx, cy) = ApplyShiftConstraint(pos.X, pos.Y);
        _vm.CommitGesture(cx, cy);
    }

    private static bool MarqueeIntersects(Shape s, double mx, double my, double mw, double mh)
    {
        var b = ComputeBounds(s);
        return !(b.X + b.Width < mx || mx + mw < b.X || b.Y + b.Height < my || my + mh < b.Y);
    }

    private static Shape TranslateShape(Shape s, double dx, double dy) => s switch
    {
        RectangleShape r => r with { X = r.X + dx, Y = r.Y + dy },
        EllipseShape e => e with { X = e.X + dx, Y = e.Y + dy },
        ArrowShape a => a with { FromX = a.FromX + dx, FromY = a.FromY + dy, ToX = a.ToX + dx, ToY = a.ToY + dy },
        LineShape l => l with { FromX = l.FromX + dx, FromY = l.FromY + dy, ToX = l.ToX + dx, ToY = l.ToY + dy },
        FreehandShape f => f with { Points = f.Points.Select(p => (p.X + dx, p.Y + dy)).ToList() },
        TextShape t => t with { X = t.X + dx, Y = t.Y + dy },
        StepCounterShape c => c with { CenterX = c.CenterX + dx, CenterY = c.CenterY + dy },
        BlurShape b => b with { X = b.X + dx, Y = b.Y + dy },
        PixelateShape p => p with { X = p.X + dx, Y = p.Y + dy },
        SpotlightShape sp => sp with { X = sp.X + dx, Y = sp.Y + dy },
        ImageShape i => i with { X = i.X + dx, Y = i.Y + dy },
        SmartEraserShape se => se with { X = se.X + dx, Y = se.Y + dy },
        _ => s
    };

    /// <summary>Start RMB-pan: capture the scroll offsets + cursor in viewport coords, swap
    /// the cursor to a hand. The PreviewMouseMove handler reads <see cref="_isPanning"/> and
    /// translates the scroll to follow the cursor. Mouse-capture on the ScrollViewer makes
    /// MouseMove keep firing even if the user drags outside the canvas bounds.</summary>
    private void OnCanvasRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Skip when the click lands on a scrollbar / interactive child — they have their own
        // RMB semantics (e.g. scrollbar context menu) we shouldn't override.
        if (IsScrollBarOrInputControl(e.OriginalSource as DependencyObject)) return;

        _isPanning = true;
        _panStartCursor = e.GetPosition(CanvasScrollViewer);
        _panStartScrollH = CanvasScrollViewer.HorizontalOffset;
        _panStartScrollV = CanvasScrollViewer.VerticalOffset;
        _panSavedCursor = CanvasScrollViewer.Cursor;
        CanvasScrollViewer.Cursor = System.Windows.Input.Cursors.SizeAll;
        CanvasScrollViewer.CaptureMouse();
        e.Handled = true;
    }

    private void OnCanvasRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isPanning) return;
        _isPanning = false;
        CanvasScrollViewer.ReleaseMouseCapture();
        CanvasScrollViewer.Cursor = _panSavedCursor;
        _panSavedCursor = null;
        // Mark handled so the right-button release doesn't propagate to a parent that might
        // open a context menu — RMB on the canvas is now exclusively pan.
        e.Handled = true;
    }

    private void OnSelectedShapeChanged()
    {
        RefreshPropertyPanel();
    }

    private void OnSelectionSetChanged()
    {
        CommitPendingLiveEdit();
        _liveEditOriginals = [.. _vm.SelectedShapes];
        RefreshPropertyPanel();
    }

    private void CommitPendingLiveEdit()
    {
        if (_liveEditOriginals.Count == 0) return;
        var current = _vm.SelectedShapes.ToList();
        for (var i = 0; i < _liveEditOriginals.Count && i < current.Count; i++)
        {
            var orig = _liveEditOriginals[i];
            var cur = current[i];
            if (!ReferenceEquals(orig, cur) && _vm.Shapes.Contains(cur))
            {
                _vm.CommitLiveEdit(orig, cur);
            }
        }
        _liveEditOriginals = [];
    }

    private void OnSelOutlineChanged()
    {
        if (_suppressLiveUpdates) return;
        var color = SelOutlineSwatch.SelectedColor;
        foreach (var s in _vm.SelectedShapes.ToList())
        {
            _vm.LiveReplaceShape(s, ApplyOutlineColor(s, color));
        }
    }

    private void OnSelFillChanged()
    {
        if (_suppressLiveUpdates) return;
        var color = SelFillSwatch.SelectedColor;
        foreach (var s in _vm.SelectedShapes.ToList())
        {
            _vm.LiveReplaceShape(s, ApplyFillColor(s, color));
        }
    }

    private void OnSelStrokeChanged()
    {
        if (_suppressLiveUpdates) return;
        var width = SelStrokeSlider.Value;
        foreach (var s in _vm.SelectedShapes.ToList())
        {
            _vm.LiveReplaceShape(s, ApplyStrokeWidth(s, width));
        }
    }

    private void OnSelFreehandSmoothChanged()
    {
        if (_suppressLiveUpdates) return;
        var smooth = SelFreehandSmoothCheck.IsChecked == true;
        foreach (var s in _vm.SelectedShapes.ToList())
        {
            if (s is FreehandShape f && f.Smooth != smooth)
                _vm.LiveReplaceShape(s, f with { Smooth = smooth });
        }
        // Sticky: the next freshly-drawn freehand stroke inherits the user's last choice.
        // The VM persists FreehandSmoothDefault to EditorDefaults on close.
        _vm.FreehandSmoothDefault = smooth;
    }

    /// <summary>Read the step-counter font fields and apply them to every selected step counter,
    /// then write back to the VM defaults so subsequent step counters inherit the same look.
    /// Family/Bold/Italic are handled together — each individual control's change event funnels
    /// through here, mirroring the text-style picker's <see cref="OnSelTextStyleChanged"/>.</summary>
    private void OnSelStepFontStyleChanged()
    {
        if (_suppressLiveUpdates) return;
        var family = string.IsNullOrWhiteSpace(SelStepFontInput.Text) ? "Segoe UI" : SelStepFontInput.Text;
        var bold = SelStepBoldCheck.IsChecked == true;
        var italic = SelStepItalicCheck.IsChecked == true;

        _vm.StepFontFamily = family;
        _vm.StepBold = bold;
        _vm.StepItalic = italic;

        foreach (var s in _vm.SelectedShapes.ToList())
        {
            if (s is StepCounterShape sc)
                _vm.LiveReplaceShape(s, sc with { FontFamily = family, Bold = bold, Italic = italic });
        }
    }

    private void OnSelFreehandEndArrowChanged()
    {
        if (_suppressLiveUpdates) return;
        var endArrow = SelFreehandEndArrowCheck.IsChecked == true;
        foreach (var s in _vm.SelectedShapes.ToList())
        {
            if (s is FreehandShape f && f.EndArrow != endArrow)
                _vm.LiveReplaceShape(s, f with { EndArrow = endArrow });
        }
        // Sticky: same propagation pattern as Smooth — next stroke inherits.
        _vm.FreehandEndArrowDefault = endArrow;
    }

    private void OnSelRotationSliderChanged()
    {
        if (_suppressLiveUpdates) return;
        var deg = SelRotationSlider.Value;
        SelRotationBox.Text = ((int)Math.Round(deg)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        ApplyRotation(deg);
    }

    private void OnSelRotationBoxCommitted()
    {
        if (_suppressLiveUpdates) return;
        if (!double.TryParse(SelRotationBox.Text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v))
        {
            SelRotationBox.Text = ((int)Math.Round(SelRotationSlider.Value)).ToString(System.Globalization.CultureInfo.InvariantCulture);
            return;
        }
        v = ((v + 180) % 360 + 360) % 360 - 180;
        _suppressLiveUpdates = true;
        try { SelRotationSlider.Value = v; }
        finally { _suppressLiveUpdates = false; }
        ApplyRotation(v);
    }

    private void OnSelEffectSliderChanged()
    {
        if (_suppressLiveUpdates) return;
        var v = SelEffectSlider.Value;
        SelEffectBox.Text = ((int)Math.Round(v)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        ApplyEffectParam(v);
    }

    private void OnSelEffectBoxCommitted()
    {
        if (_suppressLiveUpdates) return;
        if (!double.TryParse(SelEffectBox.Text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v))
        {
            SelEffectBox.Text = ((int)Math.Round(SelEffectSlider.Value)).ToString(System.Globalization.CultureInfo.InvariantCulture);
            return;
        }
        v = Math.Max(SelEffectSlider.Minimum, v);
        _suppressLiveUpdates = true;
        try { SelEffectSlider.Value = Math.Clamp(v, SelEffectSlider.Minimum, SelEffectSlider.Maximum); }
        finally { _suppressLiveUpdates = false; }
        ApplyEffectParam(v);
    }

    private void OnSelEffectSecondarySliderChanged()
    {
        if (_suppressLiveUpdates) return;
        var v = SelEffectSecondarySlider.Value;
        SelEffectSecondaryBox.Text = ((int)Math.Round(v)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        ApplyEffectSecondaryParam(v);
    }

    private void OnSelEffectSecondaryBoxCommitted()
    {
        if (_suppressLiveUpdates) return;
        if (!double.TryParse(SelEffectSecondaryBox.Text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v))
        {
            SelEffectSecondaryBox.Text = ((int)Math.Round(SelEffectSecondarySlider.Value)).ToString(System.Globalization.CultureInfo.InvariantCulture);
            return;
        }
        v = Math.Max(SelEffectSecondarySlider.Minimum, v);
        _suppressLiveUpdates = true;
        try { SelEffectSecondarySlider.Value = Math.Clamp(v, SelEffectSecondarySlider.Minimum, SelEffectSecondarySlider.Maximum); }
        finally { _suppressLiveUpdates = false; }
        ApplyEffectSecondaryParam(v);
    }

    private void ApplyEffectParam(double v)
    {
        foreach (var s in _vm.SelectedShapes.ToList())
        {
            Shape? updated = s switch
            {
                BlurShape b => b with { Radius = Math.Max(0, v) },
                PixelateShape p => p with { BlockSize = (int)Math.Max(2, Math.Round(v)) },
                SpotlightShape sp => sp with { DimAmount = Math.Clamp(v / 100.0, 0, 1) },
                _ => null
            };
            if (updated is not null) _vm.LiveReplaceShape(s, updated);
        }
    }

    private void ApplyEffectSecondaryParam(double v)
    {
        foreach (var s in _vm.SelectedShapes.ToList())
        {
            Shape? updated = s switch
            {
                SpotlightShape sp => sp with { BlurRadius = Math.Max(0, v) },
                _ => null
            };
            if (updated is not null) _vm.LiveReplaceShape(s, updated);
        }
    }

    private void ApplyRotation(double deg)
    {
        foreach (var s in _vm.SelectedShapes.ToList())
        {
            Shape? updated = s switch
            {
                RectangleShape r => r with { Rotation = deg },
                EllipseShape e => e with { Rotation = deg },
                TextShape t => t with { Rotation = deg },
                ImageShape i => i with { Rotation = deg },
                _ => null
            };
            if (updated is not null) _vm.LiveReplaceShape(s, updated);
        }
    }

    /// <summary>Update or create the dashed marquee that previews the text-frame box during
    /// drag. Lazily instantiates the rectangle on first move past the threshold so a plain
    /// click (no drag) doesn't paint anything.</summary>
    private void UpdateTextFrameMarquee(double px, double py)
    {
        var dx = Math.Abs(px - _textFrameStartX);
        var dy = Math.Abs(py - _textFrameStartY);
        if (dx < TextFrameDragThreshold && dy < TextFrameDragThreshold) return;

        if (_textFrameMarquee is null)
        {
            _textFrameMarquee = new System.Windows.Shapes.Rectangle
            {
                Stroke = new SolidColorBrush(Color.FromArgb(180, 80, 200, 255)),
                StrokeThickness = 1,
                StrokeDashArray = [4.0, 3.0],
                Fill = new SolidColorBrush(Color.FromArgb(20, 80, 200, 255)),
                IsHitTestVisible = false
            };
            DrawingCanvas.Children.Add(_textFrameMarquee);
        }
        var x = Math.Min(_textFrameStartX, px);
        var y = Math.Min(_textFrameStartY, py);
        Canvas.SetLeft(_textFrameMarquee, x);
        Canvas.SetTop(_textFrameMarquee, y);
        _textFrameMarquee.Width = Math.Abs(px - _textFrameStartX);
        _textFrameMarquee.Height = Math.Abs(py - _textFrameStartY);
    }

    /// <summary>Wrap up a text-frame drag: tear down the marquee, release capture, then open
    /// the inline editor sized to the dragged rect. A drag below <see cref="TextFrameDragThreshold"/>
    /// in both axes (i.e. just a click) falls back to <see cref="TextShape.DefaultWidthFor"/>
    /// / <see cref="TextShape.DefaultHeightFor"/> at the down point, matching the legacy
    /// behaviour for users who don't want to think about box size.</summary>
    private void FinishTextFrameDraw(System.Windows.Point upPoint)
    {
        if (_textFrameMarquee is not null)
        {
            DrawingCanvas.Children.Remove(_textFrameMarquee);
            _textFrameMarquee = null;
        }
        DrawingCanvas.ReleaseMouseCapture();
        _isDrawingTextFrame = false;

        var w = Math.Abs(upPoint.X - _textFrameStartX);
        var h = Math.Abs(upPoint.Y - _textFrameStartY);
        var x = Math.Min(_textFrameStartX, upPoint.X);
        var y = Math.Min(_textFrameStartY, upPoint.Y);

        if (w < TextFrameDragThreshold || h < TextFrameDragThreshold)
        {
            // Click → use defaults at the down point.
            BeginInlineTextEdit(_textFrameStartX, _textFrameStartY, existing: null);
            return;
        }
        BeginInlineTextEdit(x, y, existing: null, boxWidth: w, boxHeight: h);
    }

    private void BeginInlineTextEdit(double x, double y, TextShape? existing)
        => BeginInlineTextEdit(x, y, existing, boxWidth: null, boxHeight: null);

    private void BeginInlineTextEdit(double x, double y, TextShape? existing, double? boxWidth, double? boxHeight)
    {
        CommitInlineTextEdit();

        // For a brand-new text, clear any previous selection so the previously selected text
        // doesn't stay highlighted while the user types into the new one.
        if (existing is null && _vm.SelectedShapes.Count > 0) _vm.SetSelection([]);

        var style = existing?.Style ?? _vm.CurrentTextStyle;
        var initialText = existing?.Text ?? "";

        _editingTextShape = existing;
        // Box size resolution order: caller-supplied (drag-to-draw frame) → existing shape's
        // size (in-place edit) → font-size-derived defaults (plain click). Defaults scale with
        // the active font so a tiny / huge font doesn't get the same fixed-px frame.
        var boxW = boxWidth ?? existing?.Width ?? TextShape.DefaultWidthFor(style.FontSize);
        var boxH = boxHeight ?? existing?.Height ?? TextShape.DefaultHeightFor(style.FontSize);

        // For plain-click placement (no drag-rect, not editing an existing shape) the click
        // point becomes the anchor that matches the text alignment: Left → top-left,
        // Centre → top-centre, Right → top-right. That way clicking exactly where you want
        // the text edge to live "just works" regardless of which alignment is active.
        // Drag-rect callers already chose the bounds explicitly, so they're left alone.
        if (boxWidth is null && existing is null)
        {
            switch (style.Align)
            {
                case TextAlign.Center: x -= boxW / 2; break;
                case TextAlign.Right:  x -= boxW; break;
            }
        }
        _activeTextBox = new System.Windows.Controls.TextBox
        {
            // Style=null escapes WPF-UI's implicit TextBox style (merged in via the App's
            // ControlsDictionary) which would otherwise paint a TextControlBackground-bound
            // surface3 fill on top of our subtle dark overlay AND add internal chrome padding
            // that crops the trailing characters during typing. The bare WPF template gives
            // us a transparent-friendly TextBox we can dress ourselves.
            Style = null,
            Text = initialText,
            FontFamily = new FontFamily(style.FontFamily),
            FontSize = style.FontSize,
            FontWeight = style.Bold ? FontWeights.Bold : FontWeights.Normal,
            FontStyle = style.Italic ? FontStyles.Italic : FontStyles.Normal,
            Foreground = ToBrush(style.Color),
            CaretBrush = ToBrush(style.Color),
            TextAlignment = ToTextAlignment(style.Align),
            Background = new SolidColorBrush(Color.FromArgb(40, 0, 0, 0)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(180, 80, 200, 255)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(2),
            // Fixed width — content wraps horizontally — but height auto-grows with the typed
            // content. MinHeight is the original shape's box so a freshly-created TextShape
            // doesn't visually shrink before the user types anything. The Canvas it lives in
            // doesn't constrain the bottom edge, so additional lines extend down naturally.
            Width = boxW,
            MinHeight = boxH,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            AcceptsReturn = true,
            AcceptsTab = false,
            Tag = "inline-text-editor"
        };
        Canvas.SetLeft(_activeTextBox, x);
        Canvas.SetTop(_activeTextBox, y);
        if (existing is { Rotation: var rot } && rot != 0)
        {
            _activeTextBox.RenderTransform = new RotateTransform(rot, boxW / 2, boxH / 2);
        }
        DrawingCanvas.Children.Add(_activeTextBox);
        _activeTextBox.KeyDown += OnInlineTextBoxKeyDown;
        var tb = _activeTextBox;
        tb.Loaded += (_, _) =>
        {
            // Wire LostFocus only after focus has been acquired, otherwise the very first
            // focus arrival from Loaded fires LostFocus immediately and commits-empty.
            tb.Focus();
            Keyboard.Focus(tb);
            tb.SelectAll();
            tb.LostFocus += (_, _) => CommitInlineTextEdit();
        };

        if (existing is not null) RedrawAll();
    }

    private void OnInlineTextBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            // Esc commits a new text (so the user keeps the typed content selected) but cancels
            // an in-progress edit of an existing text (Esc = "back out, leave the original alone").
            if (_editingTextShape is null) CommitInlineTextEdit();
            else CancelInlineTextEdit();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) != ModifierKeys.Shift)
        {
            CommitInlineTextEdit();
            e.Handled = true;
        }
    }

    private void CommitInlineTextEdit()
    {
        if (_activeTextBox is null) return;
        var text = _activeTextBox.Text;
        var x = Canvas.GetLeft(_activeTextBox);
        var y = Canvas.GetTop(_activeTextBox);
        // Inherit the inline editor's current size — that's the user-resized width/height the
        // editor was tracking. For a fresh text the editor was sized to the existing shape (or
        // DefaultWidth/Height for new text); for an in-place edit we honour the existing
        // shape's size so a "just type more text" pass doesn't shrink the box.
        var fontSize = (_editingTextShape?.Style ?? _vm.CurrentTextStyle).FontSize;
        var width = _activeTextBox.ActualWidth > 0 ? _activeTextBox.ActualWidth : TextShape.DefaultWidthFor(fontSize);
        var height = _activeTextBox.ActualHeight > 0 ? _activeTextBox.ActualHeight : TextShape.DefaultHeightFor(fontSize);

        DrawingCanvas.Children.Remove(_activeTextBox);
        var existing = _editingTextShape;
        var style = existing?.Style ?? _vm.CurrentTextStyle;
        _activeTextBox = null;
        _editingTextShape = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            if (existing is not null) _vm.RemoveShapes([existing]);
            return;
        }

        // The TextShape's Outline is unused for rendering (foreground comes from Style.Color);
        // we still set it for hit-testing parity with other shapes. Preserve rotation + size
        // on edit; new shapes inherit the inline editor's working dimensions.
        var rotation = existing?.Rotation ?? 0;
        var finalWidth = existing?.Width ?? width;
        var finalHeight = existing?.Height ?? height;
        var shape = new TextShape(x, y, finalWidth, finalHeight,
            text, style, style.Color, ShapeColor.Transparent, 1, rotation);
        if (existing is null)
        {
            _vm.AddTextShape(shape);
            // Auto-select the new text WITHOUT switching tool — symmetric to drawing tools
            // (CommitGesture does the same). The user can immediately tweak font/color/size/align
            // from the panel and a fresh click on the canvas creates another text.
            _vm.SetSelection([shape]);
        }
        else
        {
            _vm.ApplyShapeEdit(existing, shape);
        }
    }

    private void CancelInlineTextEdit()
    {
        if (_activeTextBox is null) return;
        DrawingCanvas.Children.Remove(_activeTextBox);
        _activeTextBox = null;
        _editingTextShape = null;
        RedrawAll();
    }

    private double _currentTextSize = TextStyle.Default.FontSize;

    private void OnSelFontSizeSliderChanged()
    {
        if (_suppressLiveUpdates) return;
        _currentTextSize = SelFontSizeSlider.Value;
        SelFontSizeBox.Text = ((int)Math.Round(_currentTextSize)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        OnSelTextStyleChanged();
    }

    private void OnSelFontSizeBoxCommitted()
    {
        if (_suppressLiveUpdates) return;
        if (!double.TryParse(SelFontSizeBox.Text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v))
        {
            SelFontSizeBox.Text = ((int)Math.Round(_currentTextSize)).ToString(System.Globalization.CultureInfo.InvariantCulture);
            return;
        }
        // Hard cap at 1000 — anything bigger crashes WPF text rendering on the canvas.
        v = Math.Clamp(v, 1, 1000);
        _currentTextSize = v;
        SelFontSizeBox.Text = ((int)Math.Round(v)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        // Update slider only if value fits its bounds; otherwise let the slider sit at its max.
        _suppressLiveUpdates = true;
        try { SelFontSizeSlider.Value = Math.Clamp(v, SelFontSizeSlider.Minimum, SelFontSizeSlider.Maximum); }
        finally { _suppressLiveUpdates = false; }
        OnSelTextStyleChanged();
    }

    private void OnSelAlignLeftClicked(object sender, RoutedEventArgs e) => SetTextAlign(TextAlign.Left);
    private void OnSelAlignCenterClicked(object sender, RoutedEventArgs e) => SetTextAlign(TextAlign.Center);
    private void OnSelAlignRightClicked(object sender, RoutedEventArgs e) => SetTextAlign(TextAlign.Right);

    private void SetTextAlign(TextAlign a)
    {
        if (_suppressLiveUpdates) return;
        ApplyTextStyle(a);
        RefreshAlignToggles(a);
    }

    private void RefreshAlignToggles(TextAlign a)
    {
        SelAlignLeftBtn.IsChecked = a == TextAlign.Left;
        SelAlignCenterBtn.IsChecked = a == TextAlign.Center;
        SelAlignRightBtn.IsChecked = a == TextAlign.Right;
    }

    private void OnSelTextStyleChanged() => ApplyTextStyle(null);

    /// <summary>Read every field of the text panel, build a TextStyle and push it to <see cref="EditorViewModel.CurrentTextStyle"/>
    /// and to all selected TextShapes. <paramref name="alignOverride"/> lets the alignment buttons short-circuit
    /// the read of the toggle buttons (which haven't toggled yet at click time).</summary>
    private void ApplyTextStyle(TextAlign? alignOverride)
    {
        if (_suppressLiveUpdates) return;
        var family = string.IsNullOrWhiteSpace(SelFontInput.Text) ? "Segoe UI" : SelFontInput.Text;
        var size = _currentTextSize;
        var bold = SelBoldCheck.IsChecked == true;
        var italic = SelItalicCheck.IsChecked == true;
        var color = SelTextColorSwatch.SelectedColor;
        var align = alignOverride ?? CurrentAlignFromToggles();
        var newStyle = new TextStyle(family, size, bold, italic, color, align);

        _vm.CurrentTextStyle = newStyle;

        foreach (var s in _vm.SelectedShapes.OfType<TextShape>().ToList())
        {
            _vm.LiveReplaceShape(s, s with { Style = newStyle });
        }
    }

    /// <summary>Cached system font family names. Lazy-initialized once per process — enumerating
    /// Fonts.SystemFontFamilies isn't free.</summary>
    private static readonly Lazy<List<string>> SystemFonts = new(() =>
        Fonts.SystemFontFamilies
            .Select(f => f.Source)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList());

    private bool _suppressFontInput;
    private bool _suppressStepFontInput;
    private FontPicker? _textFontPicker;
    private FontPicker? _stepFontPicker;

    /// <summary>Bag of refs + callbacks needed by <see cref="WireFontPicker(FontPicker)"/>. One
    /// instance per autocomplete picker: text-shape style and step-counter font live independently
    /// so each gets its own state (suppress flag, commit callback, controls).</summary>
    private sealed class FontPicker
    {
        public required System.Windows.Controls.TextBox Input;
        public required System.Windows.Controls.Primitives.Popup Popup;
        public required ListBox List;
        public required Action OnCommit;
        public required Func<bool> GetSuppress;
        public required Action<bool> SetSuppress;
    }

    /// <summary>Custom font picker: TextBox + Popup with ListBox. The TextBox shows the current font
    /// and acts as a search field; the Popup lists matching fonts; clicking one applies it. This
    /// replaces the editable ComboBox approach which had constant flash from CollectionView refresh
    /// fighting with WPF's text/caret bookkeeping. Parameterised so the same wiring serves both
    /// the text-style picker and the step-counter picker.</summary>
    private void WireFontPickers()
    {
        _textFontPicker = new FontPicker
        {
            Input = SelFontInput,
            Popup = SelFontPopup,
            List = SelFontList,
            OnCommit = () => OnSelTextStyleChanged(),
            GetSuppress = () => _suppressFontInput,
            SetSuppress = v => _suppressFontInput = v
        };
        _stepFontPicker = new FontPicker
        {
            Input = SelStepFontInput,
            Popup = SelStepFontPopup,
            List = SelStepFontList,
            OnCommit = OnSelStepFontStyleChanged,
            GetSuppress = () => _suppressStepFontInput,
            SetSuppress = v => _suppressStepFontInput = v
        };
        WireFontPicker(_textFontPicker);
        WireFontPicker(_stepFontPicker);
    }

    private void WireFontPicker(FontPicker p)
    {
        // No popup on GotFocus — that caused a flash from binding ~500 items into the ListBox.
        // Popup opens only when the user actually starts typing or hits Down arrow.
        p.Input.TextChanged += (_, _) =>
        {
            if (p.GetSuppress()) return;
            var text = p.Input.Text ?? "";
            if (text.Length == 0) { p.Popup.IsOpen = false; return; }
            RebuildFontList(p, text);
        };
        p.Input.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { p.Popup.IsOpen = false; e.Handled = true; }
            else if (e.Key == Key.Down)
            {
                if (!p.Popup.IsOpen)
                {
                    RebuildFontList(p, p.Input.Text ?? "");
                    p.List.SelectedIndex = 0;
                    (p.List.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem)?.BringIntoView();
                }
                else if (p.List.Items.Count > 0)
                {
                    p.List.SelectedIndex = Math.Min(p.List.SelectedIndex + 1, p.List.Items.Count - 1);
                    if (p.List.SelectedIndex < 0) p.List.SelectedIndex = 0;
                    (p.List.ItemContainerGenerator.ContainerFromIndex(p.List.SelectedIndex) as ListBoxItem)?.BringIntoView();
                }
                PreviewSelectedFont(p);
                e.Handled = true;
            }
            else if (e.Key == Key.Up)
            {
                if (p.List.SelectedIndex > 0)
                {
                    p.List.SelectedIndex--;
                    (p.List.ItemContainerGenerator.ContainerFromIndex(p.List.SelectedIndex) as ListBoxItem)?.BringIntoView();
                }
                PreviewSelectedFont(p);
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                if (p.List.SelectedItem is string s) CommitFontPick(p, s);
                else if (p.List.Items.Count > 0 && p.List.Items[0] is string first) CommitFontPick(p, first);
                e.Handled = true;
            }
        };
        p.List.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d)
            {
                var item = FindParent<ListBoxItem>(d);
                if (item?.Content is string s) CommitFontPick(p, s);
            }
        };
    }

    private static void RebuildFontList(FontPicker p, string filter)
    {
        var all = SystemFonts.Value;
        IEnumerable<string> source = string.IsNullOrEmpty(filter)
            ? all
            : all.Where(f => f.Contains(filter, StringComparison.OrdinalIgnoreCase));
        p.List.ItemsSource = source.Take(200).ToList();
        if (!p.Popup.IsOpen) p.Popup.IsOpen = true;
    }

    private static void CommitFontPick(FontPicker p, string family)
    {
        p.SetSuppress(true);
        try
        {
            p.Input.Text = family;
            p.Input.CaretIndex = family.Length;
        }
        finally { p.SetSuppress(false); }
        p.Popup.IsOpen = false;
        p.OnCommit();
    }

    /// <summary>Live-preview the font of the currently highlighted ListBox item without closing the popup.
    /// Updates the input silently and applies the new style via the picker's commit callback.</summary>
    private static void PreviewSelectedFont(FontPicker p)
    {
        if (p.List.SelectedItem is not string family) return;
        p.SetSuppress(true);
        try
        {
            p.Input.Text = family;
            p.Input.CaretIndex = family.Length;
        }
        finally { p.SetSuppress(false); }
        p.OnCommit();
    }

    private static T? FindParent<T>(DependencyObject d) where T : DependencyObject
    {
        while (d is not null)
        {
            if (d is T t) return t;
            d = System.Windows.Media.VisualTreeHelper.GetParent(d);
        }
        return null;
    }

    /// <summary>(Legacy method — kept for reference; not wired anymore.)</summary>
    private static void WireFontFilter(ComboBox combo, List<string> allFonts)
    {
        var view = System.Windows.Data.CollectionViewSource.GetDefaultView(allFonts);
        var currentFilter = "";
        view.Filter = obj =>
        {
            if (string.IsNullOrEmpty(currentFilter)) return true;
            return obj is string s && s.Contains(currentFilter, StringComparison.OrdinalIgnoreCase);
        };
        combo.ItemsSource = view;

        const int MinChars = 2;
        var pendingText = "";
        var debounce = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(350)
        };
        debounce.Tick += (_, _) =>
        {
            debounce.Stop();
            // Don't filter on tiny inputs — too many matches, churn high, more chance of caret loss.
            var effective = pendingText.Length >= MinChars ? pendingText : "";
            if (string.Equals(currentFilter, effective, StringComparison.Ordinal)) return;
            currentFilter = effective;

            // Refresh resets the editable ComboBox's caret/text. Snapshot then restore.
            var inner = combo.Template?.FindName("PART_EditableTextBox", combo) as System.Windows.Controls.TextBox;
            var savedText = inner?.Text ?? combo.Text;
            var savedCaret = inner?.CaretIndex ?? savedText.Length;

            view.Refresh();
            // Move the keyboard "current item" to the first match so arrow-down navigates within the
            // filtered list rather than the full one. CurrentItem is the cursor for INavigateBy/key
            // events on a CollectionView.
            view.MoveCurrentToFirst();

            combo.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
            {
                if (inner is null) return;
                if (inner.Text != savedText) inner.Text = savedText;
                inner.CaretIndex = Math.Min(savedCaret, inner.Text.Length);
                if (!string.IsNullOrEmpty(currentFilter) && !combo.IsDropDownOpen) combo.IsDropDownOpen = true;
            });
        };

        combo.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent,
            new System.Windows.Controls.TextChangedEventHandler((_, _) =>
            {
                var text = combo.Text ?? "";
                // Don't filter when the user has just committed a selection (text matches selected item).
                if (combo.SelectedItem is string sel && string.Equals(sel, text, StringComparison.Ordinal))
                {
                    debounce.Stop();
                    if (currentFilter.Length > 0)
                    {
                        currentFilter = "";
                        view.Refresh();
                    }
                    return;
                }
                pendingText = text;
                debounce.Stop();
                debounce.Start();
            }));
    }

    private TextAlign CurrentAlignFromToggles()
    {
        if (SelAlignCenterBtn.IsChecked == true) return TextAlign.Center;
        if (SelAlignRightBtn.IsChecked == true) return TextAlign.Right;
        return TextAlign.Left;
    }

    /// <summary>Capabilities of a shape category, used to drive section visibility and the
    /// "no-selection defaults" preview. Maps 1:1 to a drawing tool that produces this shape
    /// kind (text → TextShape, freehand → FreehandShape, etc.). Tools that don't produce a
    /// freestanding editable shape (Select, Crop, SmartEraser) return null from <see cref="ToolCategory"/>.</summary>
    private sealed record ShapeCategory(
        string Name,
        bool Outline,
        bool Fill,
        bool Stroke,
        bool Text,
        bool Freehand,
        ShapeCategory.EffectKind Effect)
    {
        public enum EffectKind { None, Blur, Pixelate, Spotlight }
    }

    private static ShapeCategory? ToolCategory(EditorTool tool) => tool switch
    {
        EditorTool.Rectangle   => new("Rectangle",   Outline: true,  Fill: true,  Stroke: true,  Text: false, Freehand: false, ShapeCategory.EffectKind.None),
        EditorTool.Ellipse     => new("Ellipse",     Outline: true,  Fill: true,  Stroke: true,  Text: false, Freehand: false, ShapeCategory.EffectKind.None),
        EditorTool.Arrow       => new("Arrow",       Outline: true,  Fill: false, Stroke: true,  Text: false, Freehand: false, ShapeCategory.EffectKind.None),
        EditorTool.Line        => new("Line",        Outline: true,  Fill: false, Stroke: true,  Text: false, Freehand: false, ShapeCategory.EffectKind.None),
        EditorTool.Freehand    => new("Freehand",    Outline: true,  Fill: false, Stroke: true,  Text: false, Freehand: true,  ShapeCategory.EffectKind.None),
        EditorTool.Text        => new("Text",        Outline: false, Fill: false, Stroke: false, Text: true,  Freehand: false, ShapeCategory.EffectKind.None),
        EditorTool.StepCounter => new("Step counter",Outline: true,  Fill: true,  Stroke: true,  Text: false, Freehand: false, ShapeCategory.EffectKind.None),
        EditorTool.Blur        => new("Blur",        Outline: false, Fill: false, Stroke: false, Text: false, Freehand: false, ShapeCategory.EffectKind.Blur),
        EditorTool.Pixelate    => new("Pixelate",    Outline: false, Fill: false, Stroke: false, Text: false, Freehand: false, ShapeCategory.EffectKind.Pixelate),
        EditorTool.Spotlight   => new("Spotlight",   Outline: false, Fill: false, Stroke: false, Text: false, Freehand: false, ShapeCategory.EffectKind.Spotlight),
        // Select / Crop / SmartEraser have no per-shape props worth previewing — fall back to
        // the empty-state hint.
        _ => null,
    };

    private string ShapeKindName(Shape s) => s switch
    {
        RectangleShape    => Loc("Shape_Rectangle",   "Rectangle"),
        EllipseShape      => Loc("Shape_Ellipse",     "Ellipse"),
        ArrowShape        => Loc("Shape_Arrow",       "Arrow"),
        LineShape         => Loc("Shape_Line",        "Line"),
        FreehandShape     => Loc("Shape_Freehand",    "Freehand"),
        TextShape         => Loc("Shape_Text",        "Text"),
        StepCounterShape  => Loc("Shape_StepCounter", "Step counter"),
        BlurShape         => Loc("Shape_Blur",        "Blur"),
        PixelateShape     => Loc("Shape_Pixelate",    "Pixelate"),
        SpotlightShape    => Loc("Shape_Spotlight",   "Spotlight"),
        ImageShape        => Loc("Shape_Image",       "Image"),
        SmartEraserShape  => Loc("Shape_SmartEraser", "Smart eraser"),
        _ => s.GetType().Name.Replace("Shape", string.Empty),
    };

    /// <summary>Map a <see cref="ShapeCategory.Name"/> (the literal English fallback stored in
    /// <see cref="ToolCategory"/>) onto its localised display string. Lets the static category
    /// table stay static while still flowing through Loc().</summary>
    private string LocalizedCategoryName(string fallback) => fallback switch
    {
        "Rectangle"    => Loc("Shape_Rectangle",   fallback),
        "Ellipse"      => Loc("Shape_Ellipse",     fallback),
        "Arrow"        => Loc("Shape_Arrow",       fallback),
        "Line"         => Loc("Shape_Line",        fallback),
        "Freehand"     => Loc("Shape_Freehand",    fallback),
        "Text"         => Loc("Shape_Text",        fallback),
        "Step counter" => Loc("Shape_StepCounter", fallback),
        "Blur"         => Loc("Shape_Blur",        fallback),
        "Pixelate"     => Loc("Shape_Pixelate",    fallback),
        "Spotlight"    => Loc("Shape_Spotlight",   fallback),
        _ => fallback,
    };

    /// <summary>Show the sections relevant to <paramref name="category"/> and pre-fill them
    /// with the current VM defaults so the user sees the values their next stroke will inherit.
    /// Mirrors what an active selection would show, just sourced from <c>EditorViewModel</c>
    /// state instead of a concrete shape.</summary>
    private void ApplyToolDefaultsToPanel(ShapeCategory category)
    {
        SelOutlineSection.Visibility = category.Outline ? Visibility.Visible : Visibility.Collapsed;
        SelFillSection.Visibility    = category.Fill    ? Visibility.Visible : Visibility.Collapsed;
        SelOutlineFillRow.Visibility = (category.Outline || category.Fill) ? Visibility.Visible : Visibility.Collapsed;
        SelStrokeSection.Visibility  = category.Stroke  ? Visibility.Visible : Visibility.Collapsed;
        SelTextStyleSection.Visibility = category.Text  ? Visibility.Visible : Visibility.Collapsed;
        SelFreehandSection.Visibility  = category.Freehand ? Visibility.Visible : Visibility.Collapsed;
        SelEffectSection.Visibility    = category.Effect != ShapeCategory.EffectKind.None ? Visibility.Visible : Visibility.Collapsed;
        // Rotation only makes sense once a concrete shape exists with an X/Y/W/H bbox, so it
        // stays hidden in the defaults view — there's nothing to rotate yet.
        SelRotationSection.Visibility = Visibility.Collapsed;
        // Step counter controls are per-shape only — radius is concrete, not a sticky default.
        // The user picks font/bold/italic on a drawn step and that propagates to subsequent ones
        // via the StepFontFamily/StepBold/StepItalic sticky defaults.
        SelStepCounterSection.Visibility = Visibility.Collapsed;

        _suppressLiveUpdates = true;
        try
        {
            if (category.Outline) SelOutlineSwatch.SelectedColor = _vm.OutlineColor;
            if (category.Fill)    SelFillSwatch.SelectedColor    = _vm.FillColor;
            if (category.Stroke)  SelStrokeSlider.Value          = _vm.StrokeWidth;
            if (category.Text)    ApplyDefaultsToTextSection();
            if (category.Freehand)
            {
                SelFreehandSmoothCheck.IsChecked = _vm.FreehandSmoothDefault;
                SelFreehandEndArrowCheck.IsChecked = _vm.FreehandEndArrowDefault;
            }
            // Effect-tool defaults: surface the slider's range/label even when no shape exists
            // yet, so the user sees what they're about to draw. Concrete values come from the
            // shape itself once it's drawn — here we just tee up the chrome.
            switch (category.Effect)
            {
                case ShapeCategory.EffectKind.Blur:
                    SelEffectLabel.Text = Loc("BlurRadius", "Blur radius (px)");
                    SelEffectSlider.Minimum = 0; SelEffectSlider.Maximum = 60;
                    SelEffectSecondarySection.Visibility = Visibility.Collapsed;
                    break;
                case ShapeCategory.EffectKind.Pixelate:
                    SelEffectLabel.Text = Loc("PixelBlockSize", "Pixel block size");
                    SelEffectSlider.Minimum = 2; SelEffectSlider.Maximum = 60;
                    SelEffectSecondarySection.Visibility = Visibility.Collapsed;
                    break;
                case ShapeCategory.EffectKind.Spotlight:
                    SelEffectLabel.Text = Loc("SpotlightDim", "Spotlight dim (%)");
                    SelEffectSlider.Minimum = 0; SelEffectSlider.Maximum = 100;
                    SelEffectSecondarySection.Visibility = Visibility.Visible;
                    SelEffectSecondaryLabel.Text = Loc("EdgeBlur", "Edge blur (px)");
                    SelEffectSecondarySlider.Minimum = 0; SelEffectSecondarySlider.Maximum = 60;
                    break;
            }
        }
        finally { _suppressLiveUpdates = false; }
    }

    private void ApplyDefaultsToTextSection()
    {
        _suppressLiveUpdates = true;
        try
        {
            var s = _vm.CurrentTextStyle;
            _suppressFontInput = true;
            try { SelFontInput.Text = s.FontFamily; } finally { _suppressFontInput = false; }
            _currentTextSize = s.FontSize;
            SelFontSizeSlider.Value = Math.Clamp(_currentTextSize, SelFontSizeSlider.Minimum, SelFontSizeSlider.Maximum);
            SelFontSizeBox.Text = ((int)Math.Round(_currentTextSize)).ToString(System.Globalization.CultureInfo.InvariantCulture);
            SelBoldCheck.IsChecked = s.Bold;
            SelItalicCheck.IsChecked = s.Italic;
            SelTextColorSwatch.SelectedColor = s.Color;
            RefreshAlignToggles(s.Align);
        }
        finally { _suppressLiveUpdates = false; }
    }

    /// <summary>Apply the default outline / fill / stroke / text-color (Default properties group)
    /// to every selected shape in one gesture. The selection's live-edit snapshot was captured
    /// when the shapes were selected, so this commits as a single undo step per shape (same
    /// machinery as drag-to-move). Text color only applies to TextShape; ignored elsewhere.</summary>
    private void OnApplyCurrentClicked(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedShapes.Count == 0) return;
        var outline = _vm.OutlineColor;
        var fill = _vm.FillColor;
        var stroke = _vm.StrokeWidth;
        var textColor = _vm.CurrentTextStyle.Color;

        foreach (var s in _vm.SelectedShapes.ToList())
        {
            var updated = ApplyStrokeWidth(ApplyFillColor(ApplyOutlineColor(s, outline), fill), stroke);
            updated = ApplyTextColor(updated, textColor);
            _vm.LiveReplaceShape(s, updated);
        }
        RefreshPropertyPanel();
    }

    /// <summary>Adopt the selected shape's outline / fill / stroke as the default values; if the
    /// selection is a TextShape, also lift its Style.Color into the default text color so the
    /// next text-tool click reproduces it. On multi-selection, takes the first shape.</summary>
    private void OnSetAsDefaultClicked(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedShapes.Count == 0) return;
        var s = _vm.SelectedShapes[0];
        // Only project properties the shape actually uses. Otherwise picking "Set as default"
        // on e.g. a TextShape would clobber the global Outline/Fill/Stroke defaults with
        // whatever residual values that shape happened to carry — TextShape stores its visible
        // color in Style.Color, so its Outline/Fill aren't meaningful signal.
        if (ShapeSupportsOutline(s)) _vm.OutlineColor = s.Outline;
        if (ShapeSupportsFill(s)) _vm.FillColor = s.Fill;
        if (ShapeSupportsStroke(s)) _vm.StrokeWidth = s.StrokeWidth;
        if (s is TextShape t)
        {
            // Mutate via record-with so OnCurrentTextStyleChanged fires, which refreshes the
            // TextTool factory and the text swatch through the PropertyChanged handler above.
            _vm.CurrentTextStyle = _vm.CurrentTextStyle with { Color = t.Style.Color };
        }
    }

    /// <summary>Project the default text colour onto a TextShape's Style.Color. Non-TextShapes
    /// pass through unchanged so this can be called unconditionally inside the apply loop.</summary>
    private static Shape ApplyTextColor(Shape s, ShapeColor c) => s switch
    {
        TextShape t => t with { Style = t.Style with { Color = c } },
        _ => s,
    };

    private static Shape ApplyOutlineColor(Shape s, ShapeColor c) => s switch
    {
        RectangleShape r => r with { Outline = c },
        EllipseShape e => e with { Outline = c },
        ArrowShape a => a with { Outline = c },
        LineShape l => l with { Outline = c },
        FreehandShape f => f with { Outline = c },
        TextShape t => t with { Outline = c },
        StepCounterShape sc => sc with { Outline = c },
        ImageShape i => i with { Outline = c },
        _ => s
    };

    private static Shape ApplyFillColor(Shape s, ShapeColor c) => s switch
    {
        RectangleShape r => r with { Fill = c },
        EllipseShape e => e with { Fill = c },
        StepCounterShape sc => sc with { Fill = c },
        // Arrow/Line/Freehand/Text have no fill semantics; ignore.
        _ => s
    };

    private static bool ShapeSupportsFill(Shape s) => s is RectangleShape or EllipseShape or StepCounterShape;

    /// <summary>True for shapes that visually use the Outline color. TextShape uses Style.Color
    /// instead, and effect shapes (blur/pixelate/spotlight) have no colored stroke at all.
    /// <see cref="ImageShape"/> participates because outer-aligned outlines wrap the bitmap
    /// without affecting the rendered pixels — same UX as Photoshop's "Stroke" layer effect.</summary>
    private static bool ShapeSupportsOutline(Shape s) =>
        s is RectangleShape or EllipseShape or ArrowShape or LineShape or FreehandShape or StepCounterShape or ImageShape;

    /// <summary>True for shapes that visually use StrokeWidth. TextShape uses FontSize, effect shapes
    /// have no stroke.</summary>
    private static bool ShapeSupportsStroke(Shape s) =>
        s is RectangleShape or EllipseShape or ArrowShape or LineShape or FreehandShape or StepCounterShape or ImageShape;

    private static Shape ApplyStrokeWidth(Shape s, double w) => s switch
    {
        RectangleShape r => r with { StrokeWidth = w },
        EllipseShape e => e with { StrokeWidth = w },
        ArrowShape a => a with { StrokeWidth = w },
        LineShape l => l with { StrokeWidth = w },
        FreehandShape f => f with { StrokeWidth = w },
        StepCounterShape sc => sc with { StrokeWidth = w },
        ImageShape i => i with { StrokeWidth = w },
        // TextShape uses Style.FontSize, not StrokeWidth — ignore.
        _ => s
    };

    private (double X, double Y) ApplyShiftConstraint(double x, double y)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != ModifierKeys.Shift) return (x, y);

        var dx = x - _gestureStartX;
        var dy = y - _gestureStartY;

        switch (_vm.CurrentTool)
        {
            case EditorTool.Rectangle:
            case EditorTool.Ellipse:
            {
                // Constrain to square / circle (equal width and height).
                var size = Math.Max(Math.Abs(dx), Math.Abs(dy));
                var sx = Math.Sign(dx) * size;
                var sy = Math.Sign(dy) * size;
                if (sx == 0) sx = size;
                if (sy == 0) sy = size;
                return (_gestureStartX + sx, _gestureStartY + sy);
            }
            case EditorTool.Line:
            case EditorTool.Arrow:
            {
                // Snap to nearest multiple of 45° (8 angles).
                var angle = Math.Atan2(dy, dx);
                var snapped = Math.Round(angle / (Math.PI / 4)) * (Math.PI / 4);
                var len = Math.Sqrt(dx * dx + dy * dy);
                return (_gestureStartX + len * Math.Cos(snapped), _gestureStartY + len * Math.Sin(snapped));
            }
            default:
                return (x, y);
        }
    }

    private void OnShapesChanged(object? sender, NotifyCollectionChangedEventArgs e) => RedrawAll();

    private void RedrawAll()
    {
        DrawingCanvas.Children.Clear();
        foreach (var shape in _vm.Shapes)
        {
            if (ReferenceEquals(shape, _editingTextShape)) continue;
            DrawingCanvas.Children.Add(MakeUiElement(shape));
        }
        RedrawPreview();
        RefreshSelectionAdorner();
        if (_activeTextBox is not null) DrawingCanvas.Children.Add(_activeTextBox);
    }

    private void RedrawPreview()
    {
        for (var i = DrawingCanvas.Children.Count - 1; i >= 0; i--)
        {
            if (DrawingCanvas.Children[i] is FrameworkElement fe && (fe.Tag as string) == "preview")
            {
                DrawingCanvas.Children.RemoveAt(i);
            }
        }
        if (_vm.PreviewShape is null) return;
        // Crop tool gets a ShareX-style "marching ants" rect: white dash on top of black dash
        // shifted half a period, so the gaps interlock and the rectangle reads on any
        // background. Less aggressive than the red default outline used by drawing tools.
        if (_vm.CurrentTool == EditorTool.Crop && _vm.PreviewShape is RectangleShape cropRect)
        {
            foreach (var element in CreateCropPreview(cropRect))
            {
                if (element is FrameworkElement fe2) fe2.Tag = "preview";
                DrawingCanvas.Children.Add(element);
            }
            return;
        }
        var ui = MakeUiElement(_vm.PreviewShape);
        if (ui is FrameworkElement fe1) fe1.Tag = "preview";
        DrawingCanvas.Children.Add(ui);
    }

    /// <summary>Two stacked Rectangles forming the ShareX-style dashed selection. The black
    /// rect uses an offset dash array so its dashes sit in the gaps of the white one — gives
    /// the "marching ants" silhouette legibility on both bright and dark backgrounds without
    /// animating anything (cheaper than a DispatcherTimer-driven offset).</summary>
    private static IEnumerable<UIElement> CreateCropPreview(RectangleShape r)
    {
        const double dashOn = 4;
        const double dashOff = 4;
        var black = new System.Windows.Shapes.Rectangle
        {
            Width = r.Width, Height = r.Height,
            Stroke = System.Windows.Media.Brushes.Black,
            StrokeThickness = 1,
            StrokeDashArray = [dashOn, dashOff],
            StrokeDashOffset = dashOn, // half-period shift relative to the white pass below
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(black, r.X);
        Canvas.SetTop(black, r.Y);
        var white = new System.Windows.Shapes.Rectangle
        {
            Width = r.Width, Height = r.Height,
            Stroke = System.Windows.Media.Brushes.White,
            StrokeThickness = 1,
            StrokeDashArray = [dashOn, dashOff],
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(white, r.X);
        Canvas.SetTop(white, r.Y);
        yield return black;
        yield return white;
    }

    /// <summary>Show / refresh the magnifier overlay near the cursor while the Crop tool is
    /// active. Samples an 11×11 px window around the cursor from <see cref="SourceImage"/> and
    /// scales it 10× with <c>NearestNeighbor</c> so each source pixel reads as a hard square.
    /// A 1-px crosshair marks the exact cursor pixel — that's the pixel that ends up on the
    /// crop edge if the user clicks now.</summary>
    private void UpdateCropMagnifier(System.Windows.Point cursorOnCanvas)
    {
        if (SourceImage.Source is not BitmapSource src)
        {
            HideCropMagnifier();
            return;
        }

        const int sampleSize = 11;          // odd, so a unique centre pixel exists
        const int displaySize = 110;        // 10× zoom for pixel-art readability
        var cx = (int)Math.Round(cursorOnCanvas.X);
        var cy = (int)Math.Round(cursorOnCanvas.Y);
        if (cx < 0 || cy < 0 || cx >= src.PixelWidth || cy >= src.PixelHeight)
        {
            HideCropMagnifier();
            return;
        }

        var halfSample = sampleSize / 2;
        var clampedX = Math.Max(0, Math.Min(cx - halfSample, src.PixelWidth - sampleSize));
        var clampedY = Math.Max(0, Math.Min(cy - halfSample, src.PixelHeight - sampleSize));
        // Ensure we never request a region larger than what's available — small images would
        // otherwise blow up the CroppedBitmap ctor.
        var w = Math.Min(sampleSize, src.PixelWidth);
        var h = Math.Min(sampleSize, src.PixelHeight);
        var crop = new CroppedBitmap(src, new Int32Rect(clampedX, clampedY, w, h));

        if (_cropMagnifier is null)
        {
            // Mirrors ScreenColorPickerOverlay's magnifier styling: Image inside a 1-px outline
            // border, with a small Rectangle marking the centre pixel (the pixel that ends up
            // on the crop edge if the user clicks now). Same SampleSize / displaySize ratio as
            // the screen sampler so the two surfaces feel like the same control.
            _cropMagnifierImage = new System.Windows.Controls.Image
            {
                Width = displaySize, Height = displaySize,
                Stretch = System.Windows.Media.Stretch.Fill,
                IsHitTestVisible = false,
            };
            RenderOptions.SetBitmapScalingMode(_cropMagnifierImage, BitmapScalingMode.NearestNeighbor);

            // Centre-pixel marker — Rectangle outline, same as the screen sampler. Size in
            // display space = (1 source pixel) × zoomFactor.
            var centerMarker = new System.Windows.Shapes.Rectangle
            {
                Width = (double)displaySize / sampleSize,
                Height = (double)displaySize / sampleSize,
                Stroke = System.Windows.Media.Brushes.Red,
                StrokeThickness = 1.5,
                Fill = System.Windows.Media.Brushes.Transparent,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
            };
            var inner = new System.Windows.Controls.Grid
            {
                Width = displaySize, Height = displaySize,
                ClipToBounds = true,
            };
            inner.Children.Add(_cropMagnifierImage);
            inner.Children.Add(centerMarker);
            var innerFrame = new System.Windows.Controls.Border
            {
                Child = inner,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
                BorderThickness = new Thickness(1),
            };
            _cropMagnifier = new System.Windows.Controls.Border
            {
                Padding = new Thickness(8),
                Child = innerFrame,
                Background = new SolidColorBrush(Color.FromRgb(0x1F, 0x1F, 0x1F)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                IsHitTestVisible = false,
                Tag = "crop-magnifier",
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 8, ShadowDepth = 0, Opacity = 0.6, Color = Colors.Black,
                },
            };
            DrawingCanvas.Children.Add(_cropMagnifier);
        }
        if (_cropMagnifierImage is not null) _cropMagnifierImage.Source = crop;

        // Position the magnifier offset from the cursor so it doesn't sit under the pixel
        // it's trying to show. Flip to the left / top edge when running off the canvas.
        const double cursorOffset = 20;
        var magW = _cropMagnifier.Width > 0 ? _cropMagnifier.Width : displaySize + 2;
        var magH = _cropMagnifier.Height > 0 ? _cropMagnifier.Height : displaySize + 2;
        var px = cursorOnCanvas.X + cursorOffset;
        var py = cursorOnCanvas.Y + cursorOffset;
        if (px + magW > src.PixelWidth) px = cursorOnCanvas.X - cursorOffset - displaySize;
        if (py + magH > src.PixelHeight) py = cursorOnCanvas.Y - cursorOffset - displaySize;
        Canvas.SetLeft(_cropMagnifier, px);
        Canvas.SetTop(_cropMagnifier, py);
        _cropMagnifier.Visibility = Visibility.Visible;
    }

    private void HideCropMagnifier()
    {
        if (_cropMagnifier is not null) _cropMagnifier.Visibility = Visibility.Collapsed;
    }

    /// <summary>Hide the magnifier when the cursor leaves the canvas — otherwise it would
    /// linger at the last position the mouse-move event reached, which looks dead.</summary>
    private void OnCanvasMouseLeave(object sender, MouseEventArgs e) => HideCropMagnifier();

    private void RefreshSelectionAdorner()
    {
        for (var i = DrawingCanvas.Children.Count - 1; i >= 0; i--)
        {
            if (DrawingCanvas.Children[i] is FrameworkElement fe && (fe.Tag as string) == "adorner")
                DrawingCanvas.Children.RemoveAt(i);
        }

        foreach (var s in _vm.SelectedShapes)
        {
            var b = ComputeBounds(s);
            var rotation = ShapeGripLayout.RotationOf(s);
            var box = new System.Windows.Shapes.Rectangle
            {
                Width = b.Width + 8, Height = b.Height + 8,
                Stroke = new SolidColorBrush(Color.FromArgb(255, 80, 200, 255)),
                StrokeThickness = 1.5,
                StrokeDashArray = [4.0, 3.0],
                Tag = "adorner",
                IsHitTestVisible = false
            };
            Canvas.SetLeft(box, b.X - 4);
            Canvas.SetTop(box, b.Y - 4);
            if (rotation != 0)
            {
                // Pivot is the bbox center, expressed in the rectangle's local coords.
                box.RenderTransform = new RotateTransform(rotation, (b.Width + 8) / 2, (b.Height + 8) / 2);
            }
            DrawingCanvas.Children.Add(box);
        }

        // Edit grips only for single-selection in Select tool. Multi-select keeps just the dashed boxes.
        // Grips are sized in canvas units; we apply an inverse zoom transform so they keep a constant
        // 8×8-pixel screen footprint regardless of zoom level. For rotated shapes, the grip layout
        // is computed in local (non-rotated) coordinates, then a RotateTransform is applied to each
        // grip around the shape's pivot so they follow the rotation visually.
        if (_vm.CurrentTool == EditorTool.Select && _vm.SelectedShapes.Count == 1)
        {
            var sel = _vm.SelectedShapes[0];
            var inv = 1.0 / _zoom;
            var rotation = ShapeGripLayout.RotationOf(sel);
            var pivot = ShapeGripLayout.PivotOf(sel);
            var rotateOffset = sel is RectangleShape or EllipseShape or TextShape ? 25.0 / _zoom : 0;

            foreach (var g in ShapeGripLayout.GripsFor(sel, rotateOffset))
            {
                var isRotateGrip = g.Kind == GripKind.Rotate;
                var isBendGrip = g.Kind == GripKind.Bend;
                // Rotate grip: filled blue circle. Bend grip: filled yellow circle (drag-to-curve
                // visual hint, distinct from the resize / rotate handles). Other grips: white square.
                var fillBrush = isRotateGrip ? new SolidColorBrush(Color.FromArgb(255, 80, 200, 255))
                              : isBendGrip   ? new SolidColorBrush(Color.FromArgb(255, 255, 208, 0))
                              : (Brush)Brushes.White;
                var grip = new System.Windows.Shapes.Rectangle
                {
                    Width = 8, Height = 8,
                    Stroke = new SolidColorBrush(Color.FromArgb(255, 80, 200, 255)),
                    StrokeThickness = 1.5,
                    Fill = fillBrush,
                    RadiusX = (isRotateGrip || isBendGrip) ? 4 : 0,
                    RadiusY = (isRotateGrip || isBendGrip) ? 4 : 0,
                    Tag = "adorner",
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(grip, g.X - 4);
                Canvas.SetTop(grip, g.Y - 4);
                var transforms = new TransformGroup();
                transforms.Children.Add(new ScaleTransform(inv, inv, 4, 4));
                if (rotation != 0)
                {
                    // RotateTransform is applied in the grip's local coords, so the pivot must be
                    // expressed relative to the grip's top-left (Canvas.SetLeft = g.X - 4).
                    transforms.Children.Add(new RotateTransform(rotation, pivot.X - (g.X - 4), pivot.Y - (g.Y - 4)));
                }
                grip.RenderTransform = transforms;
                DrawingCanvas.Children.Add(grip);
            }
        }
    }

    private static (double X, double Y, double Width, double Height) ComputeBounds(Shape shape) => shape switch
    {
        RectangleShape r => (r.X, r.Y, r.Width, r.Height),
        EllipseShape e => (e.X, e.Y, e.Width, e.Height),
        ArrowShape a => (Math.Min(a.FromX, a.ToX), Math.Min(a.FromY, a.ToY), Math.Abs(a.ToX - a.FromX), Math.Abs(a.ToY - a.FromY)),
        LineShape l => (Math.Min(l.FromX, l.ToX), Math.Min(l.FromY, l.ToY), Math.Abs(l.ToX - l.FromX), Math.Abs(l.ToY - l.FromY)),
        FreehandShape f => FreehandBounds(f),
        TextShape t => (t.X, t.Y, t.Width, t.Height),
        StepCounterShape c => (c.CenterX - c.Radius, c.CenterY - c.Radius, c.Radius * 2, c.Radius * 2),
        BlurShape b => (b.X, b.Y, b.Width, b.Height),
        PixelateShape p => (p.X, p.Y, p.Width, p.Height),
        SpotlightShape s => (s.X, s.Y, s.Width, s.Height),
        ImageShape i => (i.X, i.Y, i.Width, i.Height),
        SmartEraserShape se => (se.X, se.Y, se.Width, se.Height),
        _ => (0, 0, 0, 0)
    };

    private static (double X, double Y, double Width, double Height) FreehandBounds(FreehandShape f)
    {
        if (f.Points.Count == 0) return (0, 0, 0, 0);
        var minX = f.Points.Min(p => p.X);
        var minY = f.Points.Min(p => p.Y);
        var maxX = f.Points.Max(p => p.X);
        var maxY = f.Points.Max(p => p.Y);
        return (minX, minY, maxX - minX, maxY - minY);
    }

    private UIElement MakeUiElement(Shape shape)
    {
        UIElement ui = shape switch
        {
            RectangleShape r => CreateRectangle(r),
            EllipseShape e => CreateEllipse(e),
            ArrowShape a => CreateArrow(a),
            LineShape l => CreateLine(l),
            FreehandShape f => CreateFreehand(f),
            TextShape t => CreateText(t),
            StepCounterShape c => CreateStepCounter(c),
            BlurShape b => CreateBlur(b),
            PixelateShape p => CreatePixelate(p),
            SpotlightShape s => CreateSpotlight(s),
            ImageShape i => CreateImage(i),
            SmartEraserShape se => CreateSmartEraser(se),
            _ => throw new NotSupportedException($"Unknown shape kind: {shape.GetType().Name}")
        };
        // Drawn shapes never intercept clicks: hit-testing happens geometrically via ShapeHitTester.
        ui.IsHitTestVisible = false;
        return ui;
    }

    private static UIElement CreateRectangle(RectangleShape r)
        => BuildOuterStrokedShape(
            r.X, r.Y, r.Width, r.Height, r.StrokeWidth, r.Outline, r.Fill, r.Rotation,
            isEllipse: false);

    private static UIElement CreateEllipse(EllipseShape e)
        => BuildOuterStrokedShape(
            e.X, e.Y, e.Width, e.Height, e.StrokeWidth, e.Outline, e.Fill, e.Rotation,
            isEllipse: true);

    /// <summary>Render a closed shape (rectangle or ellipse) with an OUTER-aligned stroke.
    /// WPF's <c>Stroke</c>/<c>StrokeThickness</c> on a Shape straddles the geometry edge —
    /// half inside, half outside — which makes interior fills shrink as the stroke gets thicker.
    /// Outer alignment keeps the fill at the size the user dragged and pushes the visible
    /// outline band entirely outside, matching the convention every raster editor (Photoshop,
    /// Affinity, Figma) uses.
    /// <para>Implementation: build the outline as a <em>filled</em> ring geometry — outer
    /// shape minus inner shape with <see cref="FillRule.EvenOdd"/> — instead of using
    /// <c>Stroke</c>. That eliminates stroke-straddling math; the ring's inner edge is exactly
    /// the user's drawn rectangle, the outer edge sits <c>strokeWidth</c> further out. The
    /// fill (when present) is rendered separately at the original bounds. Both children live
    /// inside a single Canvas so one rotation transform spins them together.</para></summary>
    private static UIElement BuildOuterStrokedShape(
        double x, double y, double w, double h,
        double strokeWidth, ShapeColor outline, ShapeColor fill, double rotation,
        bool isEllipse)
    {
        var t = strokeWidth;
        var canvas = new Canvas
        {
            Width = w + 2 * t,
            Height = h + 2 * t,
            // Transparent background keeps the wrapper hit-test friendly when inert (the editor
            // sets IsHitTestVisible=false on the returned element anyway, but explicit is safer
            // than relying on null-background semantics).
            Background = System.Windows.Media.Brushes.Transparent
        };

        if (!fill.IsTransparent)
        {
            System.Windows.Shapes.Shape fillElement = isEllipse
                ? new System.Windows.Shapes.Ellipse { Width = w, Height = h, Fill = ToBrush(fill) }
                : new System.Windows.Shapes.Rectangle { Width = w, Height = h, Fill = ToBrush(fill) };
            Canvas.SetLeft(fillElement, t);
            Canvas.SetTop(fillElement, t);
            canvas.Children.Add(fillElement);
        }

        if (t > 0 && !outline.IsTransparent)
        {
            // Ring = outer geometry XOR inner geometry. EvenOdd treats overlapping interiors
            // as "outside", so the inner rectangle/ellipse punches a hole through the outer
            // and we get a band of width `t` exactly where we want it.
            var group = new GeometryGroup { FillRule = FillRule.EvenOdd };
            if (isEllipse)
            {
                group.Children.Add(new EllipseGeometry(new Rect(0, 0, w + 2 * t, h + 2 * t)));
                group.Children.Add(new EllipseGeometry(new Rect(t, t, w, h)));
            }
            else
            {
                group.Children.Add(new RectangleGeometry(new Rect(0, 0, w + 2 * t, h + 2 * t)));
                group.Children.Add(new RectangleGeometry(new Rect(t, t, w, h)));
            }
            var ring = new System.Windows.Shapes.Path
            {
                Data = group,
                Fill = ToBrush(outline)
            };
            canvas.Children.Add(ring);
        }

        // Place the wrapper so the inner area lands at (x, y) — that's where the user dragged
        // the geometry; the ring extends `t` further out in every direction.
        Canvas.SetLeft(canvas, x - t);
        Canvas.SetTop(canvas, y - t);
        if (rotation != 0)
        {
            canvas.RenderTransform = new RotateTransform(rotation, canvas.Width / 2, canvas.Height / 2);
        }
        return canvas;
    }

    private static UIElement CreateLine(LineShape l)
    {
        // Quadratic bezier through the optional control point. When ControlOffsetX/Y are zero
        // the control coincides with the midpoint and the bezier degenerates to a straight line —
        // visually identical to the previous Line element.
        var (cx, cy) = l.ControlPoint;
        var geom = new PathGeometry();
        var fig = new PathFigure { StartPoint = new Point(l.FromX, l.FromY) };
        fig.Segments.Add(new QuadraticBezierSegment(new Point(cx, cy), new Point(l.ToX, l.ToY), true));
        geom.Figures.Add(fig);
        var path = new System.Windows.Shapes.Path
        {
            Data = geom,
            Stroke = ToBrush(l.Outline),
            StrokeThickness = l.StrokeWidth,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        };
        if (l.Rotation != 0)
        {
            // Path geometry uses absolute canvas coords (no Canvas.SetLeft/Top), so the rotate
            // pivot is also expressed in canvas coords — the segment midpoint.
            var (mx, my) = l.Midpoint;
            path.RenderTransform = new RotateTransform(l.Rotation, mx, my);
        }
        return path;
    }

    private static UIElement CreateArrow(ArrowShape a)
    {
        // Quadratic bezier from From → ControlPoint → To. The arrowhead is anchored to the
        // tangent at the END of the curve (= ToPoint − ControlPoint), not the straight-line
        // direction, so the head still points "out of" the curve when bent.
        var (cx, cy) = a.ControlPoint;
        var tdx = a.ToX - cx;
        var tdy = a.ToY - cy;
        var tlen = Math.Max(Math.Sqrt(tdx * tdx + tdy * tdy), 1);
        var ux = tdx / tlen; var uy = tdy / tlen;
        var headSize = Math.Max(8, a.StrokeWidth * 4);
        var leftX = a.ToX - ux * headSize - uy * (headSize / 2);
        var leftY = a.ToY - uy * headSize + ux * (headSize / 2);
        var rightX = a.ToX - ux * headSize + uy * (headSize / 2);
        var rightY = a.ToY - uy * headSize - ux * (headSize / 2);

        var geom = new PathGeometry();
        var fig = new PathFigure { StartPoint = new Point(a.FromX, a.FromY) };
        fig.Segments.Add(new QuadraticBezierSegment(new Point(cx, cy), new Point(a.ToX, a.ToY), true));
        fig.Segments.Add(new LineSegment(new Point(leftX, leftY), true));
        fig.Segments.Add(new LineSegment(new Point(a.ToX, a.ToY), true));
        fig.Segments.Add(new LineSegment(new Point(rightX, rightY), true));
        geom.Figures.Add(fig);

        var path = new System.Windows.Shapes.Path
        {
            Data = geom,
            Stroke = ToBrush(a.Outline),
            StrokeThickness = a.StrokeWidth,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round
        };
        if (a.Rotation != 0)
        {
            var (mx, my) = a.Midpoint;
            path.RenderTransform = new RotateTransform(a.Rotation, mx, my);
        }
        return path;
    }

    private static UIElement CreateFreehand(FreehandShape f)
    {
        // Whichever branch we take below, we want a uniform PathGeometry so adding the
        // optional end-arrow cap is just two appended LineSegments instead of an extra
        // overlay element. Non-smooth strokes get a polyline-as-path; smooth strokes get a
        // Catmull-Rom-through-Bezier path. The cap then anchors to whatever the last
        // rendered point is — same code path for both styles.
        IReadOnlyList<(double X, double Y)> renderPts;
        var geom = new PathGeometry();
        if (f.Smooth && f.Points.Count >= 3)
        {
            // Smoothing pipeline:
            //   1. Moving-average denoise (window 7, applied twice). Each point becomes the mean
            //      of its 7-point neighbourhood — actively removes hand jitter, unlike Chaikin
            //      which only rounds corners (and on dense mouse-captured points there are no
            //      real corners to round). Two passes give a visible smoothing without flattening
            //      the gesture entirely. Endpoints are preserved by the windowed clamp.
            //   2. Catmull-Rom → cubic Bezier rendering through the denoised points so the
            //      stroke looks like a continuous ink curve, not micro-segments.
            var pts = MovingAverage(MovingAverage(f.Points, 7), 7);
            renderPts = pts;
            var fig = new PathFigure { StartPoint = new Point(pts[0].X, pts[0].Y) };
            for (var i = 0; i < pts.Count - 1; i++)
            {
                var p0 = pts[Math.Max(0, i - 1)];
                var p1 = pts[i];
                var p2 = pts[i + 1];
                var p3 = pts[Math.Min(pts.Count - 1, i + 2)];
                var c1x = p1.X + (p2.X - p0.X) / 6.0;
                var c1y = p1.Y + (p2.Y - p0.Y) / 6.0;
                var c2x = p2.X - (p3.X - p1.X) / 6.0;
                var c2y = p2.Y - (p3.Y - p1.Y) / 6.0;
                fig.Segments.Add(new BezierSegment(new Point(c1x, c1y), new Point(c2x, c2y), new Point(p2.X, p2.Y), true));
            }
            AppendEndArrowIfNeeded(fig, pts, f);
            geom.Figures.Add(fig);
        }
        else
        {
            renderPts = f.Points;
            if (f.Points.Count > 0)
            {
                var fig = new PathFigure { StartPoint = new Point(f.Points[0].X, f.Points[0].Y) };
                for (var i = 1; i < f.Points.Count; i++)
                    fig.Segments.Add(new LineSegment(new Point(f.Points[i].X, f.Points[i].Y), true));
                AppendEndArrowIfNeeded(fig, f.Points, f);
                geom.Figures.Add(fig);
            }
        }

        var element = new System.Windows.Shapes.Path
        {
            Data = geom,
            Stroke = ToBrush(f.Outline),
            StrokeThickness = f.StrokeWidth,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round
        };
        if (f.Rotation != 0)
        {
            var (px, py) = f.Pivot;
            element.RenderTransform = new RotateTransform(f.Rotation, px, py);
        }
        _ = renderPts; // reserved for future hit-testing on the rendered geometry
        return element;
    }

    /// <summary>If <see cref="FreehandShape.EndArrow"/> is set, append two stroked line segments
    /// that form a "&gt;" arrowhead at the last rendered point. Tangent is averaged over the
    /// final stretch of the stroke (up to 8 points back) so a single jittery last sample doesn't
    /// flick the head sideways. Falls back to a no-op when the stroke has fewer than 2 points.</summary>
    private static void AppendEndArrowIfNeeded(PathFigure fig,
        IReadOnlyList<(double X, double Y)> pts,
        FreehandShape f)
    {
        if (!f.EndArrow || pts.Count < 2) return;

        var endIdx = pts.Count - 1;
        var anchorIdx = Math.Max(0, endIdx - 8);
        var endX = pts[endIdx].X; var endY = pts[endIdx].Y;
        var dx = endX - pts[anchorIdx].X;
        var dy = endY - pts[anchorIdx].Y;
        var len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 0.5) return; // degenerate — tangent undefined, skip head
        var ux = dx / len; var uy = dy / len;

        // Same head-size relation as ArrowShape so a freehand-arrow visually matches a regular
        // arrow at equivalent stroke weight.
        var headSize = Math.Max(8, f.StrokeWidth * 4);
        var leftX = endX - ux * headSize - uy * (headSize / 2);
        var leftY = endY - uy * headSize + ux * (headSize / 2);
        var rightX = endX - ux * headSize + uy * (headSize / 2);
        var rightY = endY - uy * headSize - ux * (headSize / 2);

        // Trace ">" anchored at the end-point: line back to the left wing, jump to the
        // end-point, line out to the right wing. The first jump is "non-stroked" so we don't
        // draw a visible line back along the body — the segment is still part of the figure
        // for path continuity but skips the pen.
        fig.Segments.Add(new LineSegment(new Point(leftX, leftY), true));
        fig.Segments.Add(new LineSegment(new Point(endX, endY), true));
        fig.Segments.Add(new LineSegment(new Point(rightX, rightY), true));
    }

    /// <summary>Moving-average denoising. Each output point is the mean of its window-sized
    /// neighbourhood in the input — a low-pass filter that removes hand jitter without changing
    /// the overall direction of the stroke. The window clamps at the endpoints so the stroke
    /// still starts where the user pressed and ends where they released.</summary>
    private static IReadOnlyList<(double X, double Y)> MovingAverage(IReadOnlyList<(double X, double Y)> pts, int window)
    {
        if (pts.Count < 3 || window < 3) return pts;
        var result = new List<(double X, double Y)>(pts.Count);
        var half = window / 2;
        for (var i = 0; i < pts.Count; i++)
        {
            var lo = Math.Max(0, i - half);
            var hi = Math.Min(pts.Count - 1, i + half);
            double sx = 0, sy = 0;
            for (var j = lo; j <= hi; j++) { sx += pts[j].X; sy += pts[j].Y; }
            var n = hi - lo + 1;
            result.Add((sx / n, sy / n));
        }
        return result;
    }

    private static UIElement CreateText(TextShape t)
    {
        // Fixed-size box: explicit Width AND Height come from the shape; text wraps inside
        // and clips at the bottom edge if the user hasn't grown the box enough. TextAlignment
        // needs the explicit Width too — without it short lines collapse to their content
        // size and Centre/Right read identically to Left.
        var tb = new System.Windows.Controls.TextBlock
        {
            Text = t.Text,
            FontFamily = new FontFamily(t.Style.FontFamily),
            FontSize = t.Style.FontSize,
            FontWeight = t.Style.Bold ? FontWeights.Bold : FontWeights.Normal,
            FontStyle = t.Style.Italic ? FontStyles.Italic : FontStyles.Normal,
            Foreground = ToBrush(t.Style.Color),
            TextAlignment = ToTextAlignment(t.Style.Align),
            TextWrapping = TextWrapping.Wrap,
            Width = t.Width,
            Height = t.Height
        };
        Canvas.SetLeft(tb, t.X);
        Canvas.SetTop(tb, t.Y);
        if (t.Rotation != 0)
        {
            tb.RenderTransform = new RotateTransform(t.Rotation, t.Width / 2, t.Height / 2);
        }
        return tb;
    }

    private static System.Windows.TextAlignment ToTextAlignment(TextAlign a) => a switch
    {
        TextAlign.Center => System.Windows.TextAlignment.Center,
        TextAlign.Right => System.Windows.TextAlignment.Right,
        _ => System.Windows.TextAlignment.Left
    };

    private UIElement CreateBlur(BlurShape b)
    {
        if (SourceImage.Source is not BitmapSource src) return EmptyRectFor(b.X, b.Y, b.Width, b.Height);
        // Crop the source to the rect so the BlurEffect operates only on the relevant region —
        // applying it to the full-size image and clipping creates an offset halo.
        var ix = (int)Math.Max(0, Math.Floor(b.X));
        var iy = (int)Math.Max(0, Math.Floor(b.Y));
        var iw = (int)Math.Min(src.PixelWidth - ix, Math.Ceiling(b.Width));
        var ih = (int)Math.Min(src.PixelHeight - iy, Math.Ceiling(b.Height));
        if (iw <= 0 || ih <= 0) return EmptyRectFor(b.X, b.Y, b.Width, b.Height);

        var cropped = new System.Windows.Media.Imaging.CroppedBitmap(src, new Int32Rect(ix, iy, iw, ih));
        var img = new System.Windows.Controls.Image
        {
            Source = cropped,
            Stretch = Stretch.Fill,
            Width = iw,
            Height = ih,
            // Clip the effect so the blur halo doesn't bleed outside the rect.
            Clip = new RectangleGeometry(new Rect(0, 0, iw, ih)),
            Effect = new System.Windows.Media.Effects.BlurEffect
            {
                Radius = b.Radius,
                KernelType = System.Windows.Media.Effects.KernelType.Gaussian
            }
        };
        Canvas.SetLeft(img, ix);
        Canvas.SetTop(img, iy);
        return img;
    }

    private UIElement CreatePixelate(PixelateShape p)
    {
        if (SourceImage.Source is not BitmapSource src) return EmptyRectFor(p.X, p.Y, p.Width, p.Height);
        var blockSize = Math.Max(2, p.BlockSize);
        var ix = (int)Math.Max(0, Math.Floor(p.X));
        var iy = (int)Math.Max(0, Math.Floor(p.Y));
        var iw = (int)Math.Min(src.PixelWidth - ix, Math.Ceiling(p.Width));
        var ih = (int)Math.Min(src.PixelHeight - iy, Math.Ceiling(p.Height));
        if (iw <= 0 || ih <= 0) return EmptyRectFor(p.X, p.Y, p.Width, p.Height);

        var cropped = new System.Windows.Media.Imaging.CroppedBitmap(src, new Int32Rect(ix, iy, iw, ih));
        var smallW = Math.Max(1, iw / blockSize);
        var smallH = Math.Max(1, ih / blockSize);
        // Downscale via a scaling transform; the upscale happens in the Image's Stretch=Fill with NearestNeighbor.
        var down = new System.Windows.Media.Imaging.TransformedBitmap(cropped,
            new ScaleTransform((double)smallW / iw, (double)smallH / ih));
        var img = new System.Windows.Controls.Image
        {
            Source = down,
            Stretch = Stretch.Fill,
            Width = iw,
            Height = ih
        };
        System.Windows.Media.RenderOptions.SetBitmapScalingMode(img, System.Windows.Media.BitmapScalingMode.NearestNeighbor);
        Canvas.SetLeft(img, ix);
        Canvas.SetTop(img, iy);
        return img;
    }

    private UIElement CreateSpotlight(SpotlightShape s)
    {
        var canvasW = DrawingCanvas.Width > 0 ? DrawingCanvas.Width : (SourceImage.Source as BitmapSource)?.PixelWidth ?? 0;
        var canvasH = DrawingCanvas.Height > 0 ? DrawingCanvas.Height : (SourceImage.Source as BitmapSource)?.PixelHeight ?? 0;

        // Even-odd geometry covers the canvas with a hole at the spotlight rect.
        var outer = new RectangleGeometry(new Rect(0, 0, canvasW, canvasH));
        var inner = new RectangleGeometry(new Rect(s.X, s.Y, s.Width, s.Height));
        var combined = new GeometryGroup { FillRule = FillRule.EvenOdd };
        combined.Children.Add(outer);
        combined.Children.Add(inner);

        // Use a Canvas (not Grid) so absolute Canvas.SetLeft/Top on children works for the blur strips.
        var canvasHost = new Canvas { Width = canvasW, Height = canvasH };

        // Blur layer: instead of one full-size Image with Effect (which produces phantom duplicates
        // because the effect's expanded bounds escape the Clip), crop the four U-strips around the
        // spotlight rect and blur each one in place.
        if (s.BlurRadius > 0 && SourceImage.Source is BitmapSource src)
        {
            var sx = (int)Math.Max(0, Math.Floor(s.X));
            var sy = (int)Math.Max(0, Math.Floor(s.Y));
            var sw = (int)Math.Min(src.PixelWidth - sx, Math.Ceiling(s.Width));
            var sh = (int)Math.Min(src.PixelHeight - sy, Math.Ceiling(s.Height));
            // Top strip
            AddBlurStrip(canvasHost, src, 0, 0, src.PixelWidth, sy, s.BlurRadius);
            // Bottom strip
            AddBlurStrip(canvasHost, src, 0, sy + sh, src.PixelWidth, src.PixelHeight - (sy + sh), s.BlurRadius);
            // Left strip (between top and bottom strips)
            AddBlurStrip(canvasHost, src, 0, sy, sx, sh, s.BlurRadius);
            // Right strip
            AddBlurStrip(canvasHost, src, sx + sw, sy, src.PixelWidth - (sx + sw), sh, s.BlurRadius);
        }

        // Dim layer on top.
        var alpha = (byte)Math.Round(Math.Clamp(s.DimAmount, 0, 1) * 255);
        var dimPath = new System.Windows.Shapes.Path
        {
            Data = combined,
            Fill = new SolidColorBrush(Color.FromArgb(alpha, 0, 0, 0))
        };
        canvasHost.Children.Add(dimPath);
        Canvas.SetLeft(canvasHost, 0);
        Canvas.SetTop(canvasHost, 0);
        return canvasHost;
    }

    private static void AddBlurStrip(Canvas host, BitmapSource src, int x, int y, int w, int h, double blurRadius)
    {
        if (w <= 0 || h <= 0) return;
        var cropped = new System.Windows.Media.Imaging.CroppedBitmap(src, new Int32Rect(x, y, w, h));
        var img = new System.Windows.Controls.Image
        {
            Source = cropped,
            Stretch = Stretch.Fill,
            Width = w,
            Height = h,
            Clip = new RectangleGeometry(new Rect(0, 0, w, h)),
            Effect = new System.Windows.Media.Effects.BlurEffect
            {
                Radius = blurRadius,
                KernelType = System.Windows.Media.Effects.KernelType.Gaussian
            }
        };
        Canvas.SetLeft(img, x);
        Canvas.SetTop(img, y);
        host.Children.Add(img);
    }

    private static UIElement CreateImage(ImageShape i)
    {
        var bmp = new System.Windows.Media.Imaging.BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
        bmp.StreamSource = new System.IO.MemoryStream(i.PngBytes);
        bmp.EndInit();
        bmp.Freeze();

        var hasOutline = i.StrokeWidth > 0 && !i.Outline.IsTransparent;
        if (!hasOutline)
        {
            // Common case: no outline → return the bare Image so we don't pay for an extra
            // Canvas + transform layer on every paste.
            var bareImg = new System.Windows.Controls.Image
            {
                Source = bmp,
                Stretch = Stretch.Fill,
                Width = i.Width,
                Height = i.Height
            };
            Canvas.SetLeft(bareImg, i.X);
            Canvas.SetTop(bareImg, i.Y);
            if (i.Rotation != 0)
            {
                bareImg.RenderTransform = new RotateTransform(i.Rotation, i.Width / 2, i.Height / 2);
            }
            return bareImg;
        }

        // Outer-aligned outline (same EvenOdd ring trick BuildOuterStrokedShape uses). The
        // bitmap stays at user bounds; the outline band wraps it entirely outside.
        var t = i.StrokeWidth;
        var canvas = new Canvas
        {
            Width = i.Width + 2 * t,
            Height = i.Height + 2 * t,
            Background = System.Windows.Media.Brushes.Transparent
        };
        var img = new System.Windows.Controls.Image
        {
            Source = bmp,
            Stretch = Stretch.Fill,
            Width = i.Width,
            Height = i.Height
        };
        Canvas.SetLeft(img, t);
        Canvas.SetTop(img, t);
        canvas.Children.Add(img);

        var ringGroup = new GeometryGroup { FillRule = FillRule.EvenOdd };
        ringGroup.Children.Add(new RectangleGeometry(new Rect(0, 0, i.Width + 2 * t, i.Height + 2 * t)));
        ringGroup.Children.Add(new RectangleGeometry(new Rect(t, t, i.Width, i.Height)));
        var ring = new System.Windows.Shapes.Path { Data = ringGroup, Fill = ToBrush(i.Outline) };
        canvas.Children.Add(ring);

        Canvas.SetLeft(canvas, i.X - t);
        Canvas.SetTop(canvas, i.Y - t);
        if (i.Rotation != 0)
        {
            canvas.RenderTransform = new RotateTransform(i.Rotation, canvas.Width / 2, canvas.Height / 2);
        }
        return canvas;
    }

    private UIElement CreateSmartEraser(SmartEraserShape s)
    {
        if (SourceImage.Source is not BitmapSource src) return EmptyRectFor(s.X, s.Y, s.Width, s.Height);

        // Sample the four corners of the rect from the source bitmap.
        var (tl, tr, bl, br) = SampleCornerColors(src, s.X, s.Y, s.Width, s.Height);

        // Build a 2×2 BGRA bitmap with the four corner colors. WPF's BitmapScalingMode=Linear
        // bilinearly interpolates this 2×2 across whatever size the Image stretches to — exactly
        // the gradient we want, for free.
        var corners = new System.Windows.Media.Imaging.WriteableBitmap(2, 2, 96, 96, PixelFormats.Bgra32, null);
        var pixels = new byte[16];
        WritePixel(pixels, 0, tl);   // (0,0) top-left
        WritePixel(pixels, 4, tr);   // (1,0) top-right
        WritePixel(pixels, 8, bl);   // (0,1) bottom-left
        WritePixel(pixels, 12, br);  // (1,1) bottom-right
        corners.WritePixels(new Int32Rect(0, 0, 2, 2), pixels, 8, 0);

        var img = new System.Windows.Controls.Image
        {
            Source = corners,
            Stretch = Stretch.Fill,
            Width = s.Width,
            Height = s.Height
        };
        System.Windows.Media.RenderOptions.SetBitmapScalingMode(img, System.Windows.Media.BitmapScalingMode.Linear);
        Canvas.SetLeft(img, s.X);
        Canvas.SetTop(img, s.Y);
        return img;
    }

    private static (byte[] TL, byte[] TR, byte[] BL, byte[] BR) SampleCornerColors(BitmapSource src, double x, double y, double w, double h)
    {
        // Convert to a uniform Bgra32 view once so CopyPixels gives us 4-byte BGRA per pixel.
        var conv = src.Format == PixelFormats.Bgra32 ? src : new System.Windows.Media.Imaging.FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);

        byte[] SamplePixel(int px, int py)
        {
            var ix = Math.Clamp(px, 0, conv.PixelWidth - 1);
            var iy = Math.Clamp(py, 0, conv.PixelHeight - 1);
            var buf = new byte[4];
            conv.CopyPixels(new Int32Rect(ix, iy, 1, 1), buf, 4, 0);
            return buf;
        }

        // Inset by 1 px so a tiny rect doesn't sample all four corners from the same pixel.
        var x0 = (int)Math.Floor(x);
        var y0 = (int)Math.Floor(y);
        var x1 = (int)Math.Floor(x + Math.Max(1, w - 1));
        var y1 = (int)Math.Floor(y + Math.Max(1, h - 1));
        return (SamplePixel(x0, y0), SamplePixel(x1, y0), SamplePixel(x0, y1), SamplePixel(x1, y1));
    }

    private static void WritePixel(byte[] dest, int offset, byte[] bgra)
    {
        dest[offset] = bgra[0];
        dest[offset + 1] = bgra[1];
        dest[offset + 2] = bgra[2];
        dest[offset + 3] = bgra[3];
    }

    private static UIElement EmptyRectFor(double x, double y, double w, double h)
    {
        var r = new System.Windows.Shapes.Rectangle { Width = Math.Max(0, w), Height = Math.Max(0, h), Fill = Brushes.Transparent };
        Canvas.SetLeft(r, x);
        Canvas.SetTop(r, y);
        return r;
    }

    private static UIElement CreateStepCounter(StepCounterShape c)
    {
        // Borderless filled disc + centered number. The user dropped the ring/stroke entirely
        // (it just added an antialiasing seam and a redundant color knob); size now rides on
        // StrokeWidth via Radius = StrokeWidth*10 inside the shape, so the existing stroke
        // slider / mouse-wheel double as the size control. Fill is preferred when set; we
        // fall back to Outline to preserve the legacy "transparent fill = use outline color"
        // shortcut from the ShareX-era step-counter UX.
        var diameter = c.Radius * 2;
        var grid = new Grid
        {
            Width = diameter,
            Height = diameter
        };
        var disc = new System.Windows.Shapes.Ellipse
        {
            Width = diameter,
            Height = diameter,
            Fill = c.Fill.IsTransparent ? ToBrush(c.Outline) : ToBrush(c.Fill),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        grid.Children.Add(disc);

        var text = new System.Windows.Controls.TextBlock
        {
            Text = c.Number.ToString(System.Globalization.CultureInfo.InvariantCulture),
            FontFamily = new FontFamily(string.IsNullOrWhiteSpace(c.FontFamily) ? "Segoe UI" : c.FontFamily),
            Foreground = Brushes.White,
            FontWeight = c.Bold ? FontWeights.Bold : FontWeights.Normal,
            FontStyle = c.Italic ? FontStyles.Italic : FontStyles.Normal,
            FontSize = c.Radius * 0.9,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        grid.Children.Add(text);

        Canvas.SetLeft(grid, c.CenterX - c.Radius);
        Canvas.SetTop(grid, c.CenterY - c.Radius);
        return grid;
    }

    private static SolidColorBrush ToBrush(ShapeColor c)
        => c.IsTransparent
            ? (SolidColorBrush)System.Windows.Media.Brushes.Transparent
            : new SolidColorBrush(System.Windows.Media.Color.FromArgb(c.A, c.R, c.G, c.B));

    private void RefreshPropertyPanel()
    {
        var sels = _vm.SelectedShapes;
        // The "Apply to selected" button in the Default properties group is only meaningful
        // when there's at least one shape selected — anything else would be a no-op click.
        ApplyToSelectedBtn.IsEnabled = sels.Count > 0;
        if (sels.Count == 0)
        {
            // No selection: defer to the active tool. Drawing tools render their target shape's
            // sections pre-populated with the current VM defaults; the Select / Crop / Smart-eraser
            // tools (which don't produce a freestanding shape) fall through to the empty-state hint.
            var category = ToolCategory(_vm.CurrentTool);
            if (category is null)
            {
                PanelTitleText.Text = Loc("Properties", "Properties");
                NoSelectionText.Visibility = Visibility.Visible;
                NoSelectionText.Text = Loc("NoSelection", "Select a shape to edit its properties (V tool, then click).");
                SelectedShapeStack.Visibility = Visibility.Collapsed;
                RefreshSelectionAdorner();
                return;
            }

            PanelTitleText.Text = string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                Loc("PropertiesTitleFormat", "{0} Properties"),
                LocalizedCategoryName(category.Name));
            NoSelectionText.Visibility = Visibility.Collapsed;
            SelectedShapeStack.Visibility = Visibility.Visible;
            ApplyToolDefaultsToPanel(category);
            RefreshSelectionAdorner();
            return;
        }

        // Selection-driven title — single shape names its kind (e.g. "Rectangle Properties"),
        // multi-selection collapses to a generic "Shared Properties" since per-shape sections
        // (rotation / freehand toggles / effect sliders) are hidden anyway in that case.
        PanelTitleText.Text = sels.Count == 1
            ? string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                Loc("PropertiesTitleFormat", "{0} Properties"),
                ShapeKindName(sels[0]))
            : Loc("SharedProperties", "Shared Properties");
        NoSelectionText.Visibility = Visibility.Collapsed;
        SelectedShapeStack.Visibility = Visibility.Visible;

        // Hide the Fill block when no selected shape supports fill (arrows/lines/freehand/text).
        var anyFillCapable = sels.Any(ShapeSupportsFill);
        SelFillSection.Visibility = anyFillCapable ? Visibility.Visible : Visibility.Collapsed;

        // Hide outline + stroke for text-only selections (text uses Style.Color and FontSize).
        var anyOutlineCapable = sels.Any(ShapeSupportsOutline);
        var anyStrokeCapable = sels.Any(ShapeSupportsStroke);
        SelOutlineSection.Visibility = anyOutlineCapable ? Visibility.Visible : Visibility.Collapsed;
        SelStrokeSection.Visibility = anyStrokeCapable ? Visibility.Visible : Visibility.Collapsed;
        // If both outline and fill are hidden the row collapses entirely (saves vertical space).
        SelOutlineFillRow.Visibility = (anyOutlineCapable || anyFillCapable) ? Visibility.Visible : Visibility.Collapsed;

        var textShapes = sels.OfType<TextShape>().ToList();
        var showTextSection = textShapes.Count > 0 || _vm.CurrentTool == EditorTool.Text;
        SelTextStyleSection.Visibility = showTextSection ? Visibility.Visible : Visibility.Collapsed;

        // Step counter section: single-selection of a StepCounterShape, font knobs only —
        // size now rides on the Stroke slider above (Radius = StrokeWidth * 10).
        var singleStep = sels.Count == 1 ? sels[0] as StepCounterShape : null;
        SelStepCounterSection.Visibility = singleStep is not null ? Visibility.Visible : Visibility.Collapsed;

        // Rotation section: only single-selection of a rotatable shape (rect/ellipse/text/image).
        var rotatable = sels.Count == 1 && sels[0] is RectangleShape or EllipseShape or TextShape or ImageShape;
        SelRotationSection.Visibility = rotatable ? Visibility.Visible : Visibility.Collapsed;

        // Freehand-specific: smooth + end-arrow toggles. Visible when a single FreehandShape
        // is selected OR when the Freehand tool itself is active (so the user can pre-toggle
        // before drawing — same UX the text-style section uses). The checkboxes reflect the
        // selected shape's flags when a shape is picked, otherwise the persisted defaults so
        // the user immediately sees their last-session choice.
        var singleFreehand = sels.Count == 1 ? sels[0] as FreehandShape : null;
        var showFreehandSection = singleFreehand is not null || _vm.CurrentTool == EditorTool.Freehand;
        SelFreehandSection.Visibility = showFreehandSection ? Visibility.Visible : Visibility.Collapsed;
        if (showFreehandSection)
        {
            _suppressLiveUpdates = true;
            if (singleFreehand is not null)
            {
                SelFreehandSmoothCheck.IsChecked = singleFreehand.Smooth;
                SelFreehandEndArrowCheck.IsChecked = singleFreehand.EndArrow;
            }
            else
            {
                SelFreehandSmoothCheck.IsChecked = _vm.FreehandSmoothDefault;
                SelFreehandEndArrowCheck.IsChecked = _vm.FreehandEndArrowDefault;
            }
            _suppressLiveUpdates = false;
        }

        // Effect section: single-selection of an effect shape (blur/pixelate/spotlight).
        var isEffect = sels.Count == 1 && sels[0] is BlurShape or PixelateShape or SpotlightShape;
        SelEffectSection.Visibility = isEffect ? Visibility.Visible : Visibility.Collapsed;

        // For multi-selection, show common values where all selected shapes agree;
        // otherwise show a sensible placeholder (the user can still pick a value to apply to all).
        var first = sels[0];
        var allSameOutline = sels.All(s => s.Outline == first.Outline);
        var fillCapable = sels.Where(ShapeSupportsFill).ToList();
        var allSameFill = fillCapable.Count > 0 && fillCapable.All(s => s.Fill == fillCapable[0].Fill);
        var fillRef = fillCapable.Count > 0 ? fillCapable[0].Fill : ShapeColor.Transparent;
        var allSameStroke = sels.All(s => Math.Abs(s.StrokeWidth - first.StrokeWidth) < 0.01);

        _suppressLiveUpdates = true;
        try
        {
            SelOutlineSwatch.SelectedColor = allSameOutline ? first.Outline : ShapeColor.Black;
            SelFillSwatch.SelectedColor = allSameFill ? fillRef : ShapeColor.Transparent;
            SelStrokeSlider.Value = allSameStroke ? first.StrokeWidth : 2;

            if (rotatable)
            {
                var rot = ShapeGripLayout.RotationOf(sels[0]);
                rot = ((rot + 180) % 360 + 360) % 360 - 180;
                SelRotationSlider.Value = rot;
                SelRotationBox.Text = ((int)Math.Round(rot)).ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            if (isEffect)
            {
                switch (sels[0])
                {
                    case BlurShape b:
                        SelEffectLabel.Text = Loc("BlurRadius", "Blur radius (px)");
                        SelEffectSlider.Minimum = 0; SelEffectSlider.Maximum = 60;
                        SelEffectSlider.Value = Math.Clamp(b.Radius, 0, 60);
                        SelEffectBox.Text = ((int)Math.Round(b.Radius)).ToString(System.Globalization.CultureInfo.InvariantCulture);
                        break;
                    case PixelateShape p:
                        SelEffectLabel.Text = Loc("PixelBlockSize", "Pixel block size");
                        SelEffectSlider.Minimum = 2; SelEffectSlider.Maximum = 60;
                        SelEffectSlider.Value = Math.Clamp(p.BlockSize, 2, 60);
                        SelEffectBox.Text = p.BlockSize.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        break;
                    case SpotlightShape sp:
                        SelEffectLabel.Text = Loc("SpotlightDim", "Spotlight dim (%)");
                        SelEffectSlider.Minimum = 0; SelEffectSlider.Maximum = 100;
                        SelEffectSlider.Value = Math.Round(sp.DimAmount * 100);
                        SelEffectBox.Text = ((int)Math.Round(sp.DimAmount * 100)).ToString(System.Globalization.CultureInfo.InvariantCulture);
                        SelEffectSecondaryLabel.Text = Loc("SpotlightBlur", "Spotlight blur (px)");
                        SelEffectSecondarySlider.Minimum = 0; SelEffectSecondarySlider.Maximum = 60;
                        SelEffectSecondarySlider.Value = Math.Clamp(sp.BlurRadius, 0, 60);
                        SelEffectSecondaryBox.Text = ((int)Math.Round(sp.BlurRadius)).ToString(System.Globalization.CultureInfo.InvariantCulture);
                        break;
                }
                SelEffectSecondarySection.Visibility = sels[0] is SpotlightShape ? Visibility.Visible : Visibility.Collapsed;
            }

            if (singleStep is not null)
            {
                _suppressStepFontInput = true;
                try { SelStepFontInput.Text = singleStep.FontFamily; }
                finally { _suppressStepFontInput = false; }
                SelStepBoldCheck.IsChecked = singleStep.Bold;
                SelStepItalicCheck.IsChecked = singleStep.Italic;
            }

            if (textShapes.Count > 0)
            {
                var firstText = textShapes[0];
                var allSameFamily = textShapes.All(t => t.Style.FontFamily == firstText.Style.FontFamily);
                var allSameSize = textShapes.All(t => Math.Abs(t.Style.FontSize - firstText.Style.FontSize) < 0.01);
                var allSameBold = textShapes.All(t => t.Style.Bold == firstText.Style.Bold);
                var allSameItalic = textShapes.All(t => t.Style.Italic == firstText.Style.Italic);
                var allSameColor = textShapes.All(t => t.Style.Color == firstText.Style.Color);
                var allSameAlign = textShapes.All(t => t.Style.Align == firstText.Style.Align);

                _suppressFontInput = true;
                try { if (allSameFamily) SelFontInput.Text = firstText.Style.FontFamily; }
                finally { _suppressFontInput = false; }
                _currentTextSize = allSameSize ? firstText.Style.FontSize : TextStyle.Default.FontSize;
                SelFontSizeSlider.Value = Math.Clamp(_currentTextSize, SelFontSizeSlider.Minimum, SelFontSizeSlider.Maximum);
                SelFontSizeBox.Text = ((int)Math.Round(_currentTextSize)).ToString(System.Globalization.CultureInfo.InvariantCulture);
                SelBoldCheck.IsChecked = allSameBold && firstText.Style.Bold;
                SelItalicCheck.IsChecked = allSameItalic && firstText.Style.Italic;
                SelTextColorSwatch.SelectedColor = allSameColor ? firstText.Style.Color : ShapeColor.Red;
                RefreshAlignToggles(allSameAlign ? firstText.Style.Align : TextAlign.Left);
            }
            else
            {
                ApplyDefaultsToTextSection();
            }
        }
        finally
        {
            _suppressLiveUpdates = false;
        }
        RefreshSelectionAdorner();
    }
}
