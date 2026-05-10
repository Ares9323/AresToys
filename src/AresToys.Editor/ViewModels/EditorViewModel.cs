using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AresToys.Editor.Commands;
using AresToys.Editor.Model;
using AresToys.Editor.Tools;

namespace AresToys.Editor.ViewModels;

public sealed partial class EditorViewModel : ObservableObject
{
    private readonly Dictionary<EditorTool, IDrawingTool> _tools;
    private readonly EditorCommandStack _commands = new();
    private IDrawingTool _activeTool;
    private bool _suppressSelectionSync;

    public EditorViewModel()
    {
        // Line and Arrow are intent aliases — both map to a separate LineTool instance so each
        // can carry its own sticky cap defaults (clicking Arrow seeds (false, true), Line seeds
        // (false, false)). Both produce LineShape; downstream code never branches on which key
        // was used to draw it.
        _tools = new Dictionary<EditorTool, IDrawingTool>
        {
            [EditorTool.Select] = new SelectTool(),
            [EditorTool.Rectangle] = new RectangleTool(),
            [EditorTool.Arrow] = new LineTool { StartCap = false, EndCap = true },
            [EditorTool.Line] = new LineTool(),
            [EditorTool.Ellipse] = new EllipseTool(),
            [EditorTool.Freehand] = new FreehandTool(),
            [EditorTool.Text] = new TextTool(TextStyle.Default),
            [EditorTool.StepCounter] = new StepCounterTool(),
            [EditorTool.Blur] = new BlurTool(),
            [EditorTool.Pixelate] = new PixelateTool(),
            [EditorTool.Spotlight] = new SpotlightTool(),
            [EditorTool.Crop] = new CropTool(),
            [EditorTool.SmartEraser] = new SmartEraserTool()
        };
        _activeTool = _tools[EditorTool.Rectangle];
        Shapes = [];
        PendingCrops.CollectionChanged += OnPendingCropsCollectionChanged;
    }

    /// <summary>Index into <see cref="PendingCrops"/> of the currently-selected rect, or
    /// -1 when nothing is selected. Multi-region semantics: clicking a rect in the overlay
    /// selects it; pressing Delete removes only the selected one; clicking outside any
    /// rect (in the canvas) clears the selection. Tracking selection here (vs. on the
    /// view) keeps the Delete-key handler stateless w.r.t. mouse history.</summary>
    [ObservableProperty]
    private int _selectedPendingCropIndex = -1;

    public ObservableCollection<Shape> Shapes { get; }

    /// <summary>True when the document has been modified since the last save / reset. Drives
    /// the close-confirmation prompt in <see cref="Views.EditorWindow"/>.</summary>
    public bool HasUnsavedChanges => _commands.IsDirty;

    /// <summary>Clear the dirty flag — called by the editor window after a successful Save so
    /// the close prompt doesn't fire on the way out.</summary>
    public void MarkSaved() => _commands.MarkSaved();

    [ObservableProperty]
    private byte[] _sourcePngBytes = [];

    [ObservableProperty]
    private long _editingItemId;

    [ObservableProperty]
    private EditorTool _currentTool = EditorTool.Rectangle;

    [ObservableProperty]
    private ShapeColor _outlineColor = ShapeColor.Red;

    [ObservableProperty]
    private ShapeColor _fillColor = ShapeColor.Transparent;

    [ObservableProperty]
    private double _strokeWidth = 2;

    [ObservableProperty]
    private TextStyle _currentTextStyle = TextStyle.Default;

    /// <summary>Sticky default for the freehand tool's Smooth flag. New strokes inherit this;
    /// the per-shape Smooth toggle in the properties panel also writes back here so the next
    /// stroke gets whatever the user just chose. Persisted across sessions via EditorDefaults.</summary>
    [ObservableProperty]
    private bool _freehandSmoothDefault = true;

    partial void OnFreehandSmoothDefaultChanged(bool value)
    {
        if (_tools.TryGetValue(EditorTool.Freehand, out var t) && t is FreehandTool fh) fh.SmoothStrokes = value;
    }

    /// <summary>Sticky cap defaults for the freehand "ShareX-style arrow" feature. Same
    /// propagation rules as <see cref="FreehandSmoothDefault"/>: tool reads on stroke start,
    /// per-shape toggle writes back, persisted across sessions via EditorDefaults.</summary>
    [ObservableProperty]
    private bool _freehandStartCapDefault;

    partial void OnFreehandStartCapDefaultChanged(bool value)
    {
        if (_tools.TryGetValue(EditorTool.Freehand, out var t) && t is FreehandTool fh) fh.StartCap = value;
    }

    [ObservableProperty]
    private bool _freehandEndCapDefault;

    partial void OnFreehandEndCapDefaultChanged(bool value)
    {
        if (_tools.TryGetValue(EditorTool.Freehand, out var t) && t is FreehandTool fh) fh.EndCap = value;
    }

    /// <summary>Sticky cap defaults for the unified Line/Arrow tool. The two toolbar buttons
    /// (<see cref="EditorTool.Line"/> / <see cref="EditorTool.Arrow"/>) are intent aliases for
    /// the same internal LineTool — they keep distinct default sets here so clicking "Arrow"
    /// always reseeds an arrow-style cap regardless of what the user was just doing in Line mode,
    /// matching the muscle memory of the original two-tool layout.</summary>
    [ObservableProperty]
    private bool _lineStartCapDefault;

    partial void OnLineStartCapDefaultChanged(bool value)
    {
        if (_tools.TryGetValue(EditorTool.Line, out var t) && t is LineTool lt) lt.StartCap = value;
    }

    [ObservableProperty]
    private bool _lineEndCapDefault;

    partial void OnLineEndCapDefaultChanged(bool value)
    {
        if (_tools.TryGetValue(EditorTool.Line, out var t) && t is LineTool lt) lt.EndCap = value;
    }

    [ObservableProperty]
    private bool _arrowStartCapDefault;

    partial void OnArrowStartCapDefaultChanged(bool value)
    {
        if (_tools.TryGetValue(EditorTool.Arrow, out var t) && t is LineTool lt) lt.StartCap = value;
    }

    [ObservableProperty]
    private bool _arrowEndCapDefault = true;

    partial void OnArrowEndCapDefaultChanged(bool value)
    {
        if (_tools.TryGetValue(EditorTool.Arrow, out var t) && t is LineTool lt) lt.EndCap = value;
    }

    /// <summary>Tip-style default shared across Line, Arrow and Freehand. Picking a tip style
    /// is a global aesthetic choice — there's no muscle-memory case for "different look per
    /// tool" — so we keep one slot and broadcast it to all three tools.</summary>
    [ObservableProperty]
    private LineTipStyle _lineTipStyleDefault = LineTipStyle.ShareXCurve;

    partial void OnLineTipStyleDefaultChanged(LineTipStyle value)
    {
        if (_tools.TryGetValue(EditorTool.Line, out var lt) && lt is LineTool l1) l1.TipStyle = value;
        if (_tools.TryGetValue(EditorTool.Arrow, out var at) && at is LineTool l2) l2.TipStyle = value;
        if (_tools.TryGetValue(EditorTool.Freehand, out var ft) && ft is FreehandTool f) f.TipStyle = value;
    }

    /// <summary>What alt+click does on a placement tool (Rectangle, Ellipse, Arrow, Line,
    /// Freehand, Text, Step, Blur, Pixelate, Spotlight, SmartEraser) when there is NO shape of
    /// the same type under the cursor. With a same-type hit the behaviour is fixed (select the
    /// hit shape — never place); this knob only governs the no-match fallback.
    /// <list type="bullet">
    /// <item><description><see cref="AltClickFallback.Place"/>: ignore Alt, fall through to
    /// the normal placement gesture (default — Alt acts as "select if match else place").</description></item>
    /// <item><description><see cref="AltClickFallback.SelectAny"/>: hit-test all shapes
    /// regardless of type and select if any is found, else no-op (Alt acts as a temporary
    /// "select mode" inside any placement tool).</description></item>
    /// </list>
    /// Pushed in by <see cref="Services.EditorLauncher"/> from the
    /// <c>editor.alt_click_no_match</c> setting. (Renamed from <c>shift_click_no_match</c> in
    /// 0.1.6 — Shift was reassigned to multi-select-toggle so it stacks with the new Alt
    /// behaviour: Alt+Shift = select-and-add-to-set in placement tools.)</summary>
    [ObservableProperty]
    private AltClickFallback _altClickFallback = AltClickFallback.Place;

    /// <summary>Sticky font defaults for the step counter tool. Decoupled from
    /// <see cref="CurrentTextStyle"/> so the user can pick a different register for digits
    /// inside the disc without affecting their text-shape font choice. Same propagation
    /// pattern as the freehand flags: tool reads on next stroke, per-shape edit writes back.</summary>
    [ObservableProperty]
    private string _stepFontFamily = "Segoe UI";

    partial void OnStepFontFamilyChanged(string value)
    {
        if (_tools.TryGetValue(EditorTool.StepCounter, out var t) && t is StepCounterTool sc) sc.FontFamily = value;
    }

    [ObservableProperty]
    private bool _stepBold = true;

    partial void OnStepBoldChanged(bool value)
    {
        if (_tools.TryGetValue(EditorTool.StepCounter, out var t) && t is StepCounterTool sc) sc.Bold = value;
    }

    [ObservableProperty]
    private bool _stepItalic;

    partial void OnStepItalicChanged(bool value)
    {
        if (_tools.TryGetValue(EditorTool.StepCounter, out var t) && t is StepCounterTool sc) sc.Italic = value;
    }

    [ObservableProperty]
    private Shape? _selectedShape;

    /// <summary>
    /// Multi-selection (e.g. via marquee). <see cref="SelectedShape"/> is the "primary" focused
    /// shape (kept in sync with the first entry of this set when set externally).
    /// </summary>
    public ObservableCollection<Shape> SelectedShapes { get; } = [];

    public Shape? PreviewShape => _activeTool.PreviewShape;

    /// <summary>Replace the multi-selection set; <see cref="SelectedShape"/> becomes the first entry (or null).</summary>
    public void SetSelection(IReadOnlyList<Shape> shapes)
    {
        SelectedShapes.Clear();
        foreach (var s in shapes) SelectedShapes.Add(s);
        SelectedShape = shapes.Count > 0 ? shapes[0] : null;
    }

    public void RemoveShapes(IReadOnlyList<Shape> shapes)
    {
        if (shapes.Count == 0) return;
        _commands.Execute(new RemoveShapesCommand(shapes), Shapes);
        SetSelection([]);
    }

    partial void OnCurrentToolChanged(EditorTool value)
    {
        _activeTool = _tools[value];
        if (value != EditorTool.Select) SetSelection([]);
    }

    partial void OnCurrentTextStyleChanged(TextStyle value)
    {
        // Refresh the TextTool so the next click uses the new style.
        _tools[EditorTool.Text] = new TextTool(value);
        if (CurrentTool == EditorTool.Text) _activeTool = _tools[EditorTool.Text];
    }

    public void AddTextShape(TextShape shape)
    {
        if (shape.IsEmpty) return;
        _commands.Execute(new AddShapeCommand(shape), Shapes);
    }

    public void AddImageShape(ImageShape shape)
    {
        if (shape.IsEmpty) return;
        _commands.Execute(new AddShapeCommand(shape), Shapes);
        SetSelection([shape]);
    }

    /// <summary>Add an arbitrary shape (any concrete subtype) and select it. Used by the
    /// editor's clipboard paste path to round-trip shapes as editable objects rather than
    /// rasterised images. Goes through <see cref="AddShapeCommand"/> so undo/redo works.</summary>
    public void AddShape(Shape shape)
    {
        ArgumentNullException.ThrowIfNull(shape);
        _commands.Execute(new AddShapeCommand(shape), Shapes);
        SetSelection([shape]);
    }

    public void ResetStepCounter()
    {
        if (_tools[EditorTool.StepCounter] is StepCounterTool t) t.Reset();
    }

    /// <summary>Right-click on a step counter: remove it AND decrement every counter that
    /// followed it numerically, so the visible sequence stays gap-free. Also rolls the
    /// StepCounterTool's running counter back so the next placement picks up the new
    /// max+1 (or 1 if no counters remain). Single undo step via
    /// <see cref="DeleteStepAndRenumberCommand"/>.</summary>
    public void DeleteStepAndRenumber(StepCounterShape step)
    {
        ArgumentNullException.ThrowIfNull(step);
        _commands.Execute(new DeleteStepAndRenumberCommand(step), Shapes);

        // Sync the tool's _next so the next placement isn't a number that's already used by
        // a surviving counter (or a number we just freed up). Walk the post-Apply collection
        // and pick max+1, default 1 when no counters remain.
        if (_tools[EditorTool.StepCounter] is StepCounterTool t)
        {
            var max = 0;
            foreach (var s in Shapes)
            {
                if (s is StepCounterShape sc && sc.Number > max) max = sc.Number;
            }
            t.SetNext(max + 1);
        }

        // Drop selection if the deleted step was selected (or one of the renumbered siblings
        // was — record references won't match after a `with` swap). Simpler to clear; the
        // user can re-select via the Select tool if needed.
        SetSelection([]);
    }

    partial void OnSelectedShapeChanged(Shape? value)
    {
        // Suppress when LiveReplaceShape is about to update both SelectedShapes and SelectedShape.
        if (_suppressSelectionSync) return;

        if (value is null)
        {
            if (SelectedShapes.Count > 0) SelectedShapes.Clear();
            return;
        }
        if (SelectedShapes.Contains(value)) return;
        SelectedShapes.Clear();
        SelectedShapes.Add(value);
    }

    public void BeginGesture(double x, double y)
    {
        // Re-derive the next step number from the current shape set so undo/delete reuses freed numbers.
        if (_activeTool is StepCounterTool sct)
        {
            var maxN = Shapes.OfType<StepCounterShape>().Select(s => s.Number).DefaultIfEmpty(0).Max();
            sct.SetNext(maxN + 1);
        }
        // Multi-region semantics: the new drag will APPEND another rect to the existing
        // PendingCrops list (no clear). The window dispatcher only routes to BeginGesture
        // when the click lands outside every existing rect / grip, so we don't accidentally
        // start a new rect when the user meant to drag an existing one.
        _activeTool.Begin(x, y, OutlineColor, FillColor, StrokeWidth);
        OnPropertyChanged(nameof(PreviewShape));
    }

    public void UpdateGesture(double x, double y)
    {
        _activeTool.Update(x, y);
        OnPropertyChanged(nameof(PreviewShape));
    }

    public void CommitGesture(double x, double y)
    {
        var committed = _activeTool.Commit(x, y);
        if (_activeTool is CropTool ct && ct.LastRect is { } cropRect)
        {
            // Non-destructive multi-region: APPEND the new rect to the pending list rather
            // than replace. The user can stack multiple crop rects, select / delete any of
            // them, then trigger one global Apply that composites them all (matches ShareX's
            // multi-region capture). Single-rect workflows are unaffected — the list ends up
            // with one item.
            AddPendingCrop(cropRect.X, cropRect.Y, cropRect.Width, cropRect.Height);
            // Auto-select the just-drawn rect so a subsequent Delete key removes it without
            // requiring a separate select-then-delete click sequence.
            SelectedPendingCropIndex = PendingCrops.Count - 1;
        }
        else if (committed is not null)
        {
            _commands.Execute(new AddShapeCommand(committed), Shapes);
            // Auto-select the just-drawn shape so the property panel shows its attributes.
            // The active tool stays as-is — the user can keep drawing more shapes; the next
            // BeginGesture will not clear the selection (only a tool switch does).
            SetSelection([committed]);
        }
        OnPropertyChanged(nameof(PreviewShape));
    }

    /// <summary>List of pending crop rectangles (zero or more). Multi-region: each new
    /// drag with the Crop tool APPENDS a rectangle, the user can move / resize / remove
    /// any of them, then a single global Apply All command bakes a composite — the source
    /// becomes the bounding box of all rects with everything outside any rect rendered
    /// transparent (matches ShareX's region-capture multi-region behaviour).
    /// Empty list = no overlay, no dim, no buttons. Use <see cref="PendingCrops"/>.Count
    /// instead of "is non-null" for visibility checks.</summary>
    public ObservableCollection<CropRect> PendingCrops { get; } = [];

    /// <summary>True when at least one pending crop rect exists. Surfaces as a property
    /// for XAML bindings (e.g. the Crop properties panel's Apply All button visibility);
    /// raised on every list mutation via <see cref="OnPendingCropsCollectionChanged"/>.</summary>
    public bool HasPendingCrops => PendingCrops.Count > 0;

    /// <summary>Append a new pending crop to the list. Called by the CropTool commit path
    /// after a fresh drag — multi-region semantics: doesn't replace the existing rects.
    /// Recorded on the undo stack so Ctrl+Z removes the just-added rect (matches what the
    /// user expects of "an object I dropped on the canvas"). Resize / move on a placed
    /// rect are not yet undoable as discrete steps.</summary>
    public void AddPendingCrop(double x, double y, double w, double h)
    {
        if (w < 0) { x += w; w = -w; }
        if (h < 0) { y += h; h = -h; }
        if (w < 1 || h < 1) return;
        var rect = new CropRect(x, y, w, h);
        _commands.Execute(new AddPendingCropCommand(this, rect), Shapes);
    }

    /// <summary>Replace the rect at <paramref name="index"/> with a new one (used by grip
    /// resize / inside-rect move drags). Out-of-range / degenerate rects are silently
    /// dropped — better than throwing on a fast-firing mouse-move event.</summary>
    public void UpdatePendingCrop(int index, double x, double y, double w, double h)
    {
        if (index < 0 || index >= PendingCrops.Count) return;
        if (w < 0) { x += w; w = -w; }
        if (h < 0) { y += h; h = -h; }
        if (w < 1 || h < 1) { PendingCrops.RemoveAt(index); return; }
        PendingCrops[index] = new CropRect(x, y, w, h);
    }

    /// <summary>Remove a single pending crop (the per-rect ✗ button calls this).</summary>
    public void RemovePendingCrop(int index)
    {
        if (index < 0 || index >= PendingCrops.Count) return;
        PendingCrops.RemoveAt(index);
    }

    /// <summary>Bake every pending crop into the source bitmap as a composite: bbox = union
    /// of all rects, output image keeps pixels inside any rect, everything else transparent.
    /// Single rect collapses to the original simple-crop behaviour. Undoable via Ctrl+Z.</summary>
    public void ConfirmAllPendingCrops()
    {
        if (PendingCrops.Count == 0) return;
        SetSelection([]);
        var snapshot = PendingCrops.ToList();
        _commands.Execute(new MultiCropCommand(this, snapshot), Shapes);
        PendingCrops.Clear();
    }

    /// <summary>Discard every pending crop without touching the source bitmap.</summary>
    public void CancelAllPendingCrops() => PendingCrops.Clear();

    private void OnPendingCropsCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasPendingCrops));
        OnPropertyChanged(nameof(PendingCrops));   // fire a change event so view-side listeners (RedrawPendingCrop) re-render
    }

    public void ApplyCrop(int cropX, int cropY, int cropW, int cropH)
    {
        SetSelection([]);
        _commands.Execute(new CropCommand(this, cropX, cropY, cropW, cropH), Shapes);
    }

    public void ApplyResize(int newWidth, int newHeight)
    {
        SetSelection([]);
        _commands.Execute(new ResizeCommand(this, newWidth, newHeight), Shapes);
    }

    /// <summary>Swap the source PNG bytes — used by the Effects tool after the user picks a
    /// preset in the image-effects editor. Goes through the command stack so the swap is
    /// undoable / redoable like crops and resizes. Shapes layer is preserved; only the
    /// underlying bitmap changes.</summary>
    public void ApplyReplaceSource(byte[] newPng)
    {
        if (newPng is null || newPng.Length == 0) return;
        _commands.Execute(new ReplaceSourceCommand(this, newPng), Shapes);
    }

    public void ApplyShapeEdit(Shape oldShape, Shape newShape)
    {
        if (ReferenceEquals(oldShape, newShape)) return;
        _commands.Execute(new ReplaceShapeCommand(oldShape, newShape), Shapes);
        SelectedShape = newShape;
    }

    /// <summary>Replace a shape in the collection without pushing to the command stack.
    /// Used for live preview during interactive edits (drag-to-move, property panel).
    /// Also updates the multi-selection set in lockstep so a multi-shape drag/edit doesn't collapse.</summary>
    public void LiveReplaceShape(Shape oldShape, Shape newShape)
    {
        if (ReferenceEquals(oldShape, newShape)) return;
        var idx = Shapes.IndexOf(oldShape);
        if (idx < 0) return;
        Shapes[idx] = newShape;

        var selIdx = SelectedShapes.IndexOf(oldShape);
        if (selIdx >= 0) SelectedShapes[selIdx] = newShape;

        if (ReferenceEquals(SelectedShape, oldShape))
        {
            _suppressSelectionSync = true;
            try { SelectedShape = newShape; }
            finally { _suppressSelectionSync = false; }
        }
    }

    /// <summary>Push a single ReplaceShapeCommand spanning a live-edit gesture (e.g. drag-to-move
    /// or a sequence of property changes), so undo reverts the whole gesture in one step.</summary>
    public void CommitLiveEdit(Shape originalShape, Shape currentShape)
    {
        if (ReferenceEquals(originalShape, currentShape)) return;
        // The command's Apply runs against current state; since the live edit already mutated
        // Shapes[idx] to currentShape, push a synthetic command that records the swap for undo.
        _commands.RecordCommittedReplacement(originalShape, currentShape);
    }

    [RelayCommand]
    private void Undo()
    {
        if (_commands.Undo(Shapes))
        {
            if (SelectedShape is not null && !Shapes.Contains(SelectedShape)) SelectedShape = null;
        }
    }

    [RelayCommand]
    private void Redo()
    {
        _commands.Redo(Shapes);
    }

    [RelayCommand]
    private void SelectTool(EditorTool tool) => CurrentTool = tool;
}

/// <summary>Fallback behaviour for alt+click on a placement tool when nothing of the
/// tool's own shape type lies under the cursor. See
/// <see cref="EditorViewModel.AltClickFallback"/>.</summary>
public enum AltClickFallback
{
    /// <summary>Ignore Alt and start the normal placement gesture (default).</summary>
    Place,
    /// <summary>Select whatever shape is under the cursor regardless of type, else no-op.</summary>
    SelectAny,
}
