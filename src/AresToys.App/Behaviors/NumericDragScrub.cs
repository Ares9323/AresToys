using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace AresToys.App.Behaviors;

/// <summary>Attached behavior that turns a vanilla <see cref="TextBox"/> into a drag-to-scrub
/// numeric field — Unreal/Blender style. Press the left button on the text and drag horizontally
/// to scrub the integer value (delta pixels × sensitivity). A simple click without movement
/// still focuses the box for normal keyboard editing, so users get both gestures from the same
/// control. Hover shows the SizeWE cursor as a discoverability cue.
///
/// Attach in XAML with <c>behaviors:NumericDragScrub.IsEnabled="True"</c> on each numeric
/// TextBox. Per-instance state lives in a <c>State</c> attached property bag so multiple boxes
/// can be scrubbed independently without static collisions.
///
/// Live binding: as the user drags, we push every interim value through the binding's
/// <see cref="BindingExpression.UpdateSource"/> so dependent surfaces (the Wormhole window
/// position/size) move/resize in real time, not just on mouse-up.</summary>
public static class NumericDragScrub
{
    /// <summary>Pixels of horizontal movement before we commit to "this is a drag, not a click".
    /// Below this we do nothing and let the eventual MouseUp fall through to normal click +
    /// focus + select-all behaviour.</summary>
    private const double DragThreshold = 4;

    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled",
        typeof(bool),
        typeof(NumericDragScrub),
        new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject obj) => (bool)obj.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject obj, bool value) => obj.SetValue(IsEnabledProperty, value);

    /// <summary>Pixels of mouse movement that map to one unit of the integer value. Default 1.0
    /// = "one screen pixel of drag = one unit". Increase to make scrubbing slower (e.g. 2.0 for
    /// finer control on small ranges); decrease to make it faster (rare).</summary>
    public static readonly DependencyProperty SensitivityProperty = DependencyProperty.RegisterAttached(
        "Sensitivity",
        typeof(double),
        typeof(NumericDragScrub),
        new PropertyMetadata(1.0));

    public static double GetSensitivity(DependencyObject obj) => (double)obj.GetValue(SensitivityProperty);
    public static void SetSensitivity(DependencyObject obj, double value) => obj.SetValue(SensitivityProperty, value);

    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State",
        typeof(ScrubState),
        typeof(NumericDragScrub),
        new PropertyMetadata(null));

    private static ScrubState? GetState(DependencyObject obj) => (ScrubState?)obj.GetValue(StateProperty);
    private static void SetState(DependencyObject obj, ScrubState? value) => obj.SetValue(StateProperty, value);

    private sealed class ScrubState
    {
        public bool MouseDown;
        public bool Dragging;
        public Point AnchorPoint;
        public int AnchorValue;
    }

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox tb) return;
        if ((bool)e.NewValue)
        {
            SetState(tb, new ScrubState());
            tb.PreviewMouseLeftButtonDown += OnPreviewMouseDown;
            tb.PreviewMouseMove += OnPreviewMouseMove;
            tb.PreviewMouseLeftButtonUp += OnPreviewMouseUp;
            tb.MouseEnter += OnMouseEnter;
            tb.MouseLeave += OnMouseLeave;
        }
        else
        {
            tb.PreviewMouseLeftButtonDown -= OnPreviewMouseDown;
            tb.PreviewMouseMove -= OnPreviewMouseMove;
            tb.PreviewMouseLeftButtonUp -= OnPreviewMouseUp;
            tb.MouseEnter -= OnMouseEnter;
            tb.MouseLeave -= OnMouseLeave;
            SetState(tb, null);
        }
    }

    private static void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBox tb) return;
        // If the box already has keyboard focus the user is editing — let the click position
        // the caret normally without arming the scrub. They can click outside and back in to
        // re-arm scrubbing.
        if (tb.IsKeyboardFocusWithin) return;
        var state = GetState(tb);
        if (state is null) return;
        if (!int.TryParse(tb.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) return;

        state.MouseDown = true;
        state.Dragging = false;
        state.AnchorPoint = e.GetPosition(tb);
        state.AnchorValue = v;
        // Don't mark e.Handled — we WANT the click to fall through and focus + select-all when
        // it turns out to be a simple click (no drag past threshold). Suppression happens on
        // MouseUp inside the dragging branch.
    }

    private static void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not TextBox tb) return;
        var state = GetState(tb);
        if (state is null || !state.MouseDown) return;
        var pos = e.GetPosition(tb);
        var deltaX = pos.X - state.AnchorPoint.X;
        if (!state.Dragging)
        {
            if (Math.Abs(deltaX) < DragThreshold) return;
            state.Dragging = true;
            tb.CaptureMouse();
            tb.Cursor = Cursors.SizeWE;
        }
        var sensitivity = GetSensitivity(tb);
        if (sensitivity <= 0) sensitivity = 1.0;
        var newValue = state.AnchorValue + (int)Math.Round(deltaX / sensitivity);
        var newText = newValue.ToString(CultureInfo.InvariantCulture);
        if (tb.Text != newText)
        {
            tb.Text = newText;
            // Live source push so the bound surface (wormhole window geometry) tracks the drag
            // frame-by-frame instead of waiting for LostFocus / Enter.
            var be = tb.GetBindingExpression(TextBox.TextProperty);
            be?.UpdateSource();
        }
        e.Handled = true;
    }

    private static void OnPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBox tb) return;
        var state = GetState(tb);
        if (state is null) return;
        if (state.Dragging)
        {
            tb.ReleaseMouseCapture();
            tb.Cursor = tb.IsKeyboardFocusWithin ? Cursors.IBeam : Cursors.SizeWE;
            // Eat the up so the click-to-focus + select-all path doesn't re-fire after a drag
            // (the textbox may have grabbed focus on the original Down — that's fine and
            // unavoidable without breaking the simple-click case — but we don't want a second
            // SelectAll wiping the value the user just scrubbed to).
            e.Handled = true;
        }
        state.MouseDown = false;
        state.Dragging = false;
    }

    private static void OnMouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is TextBox tb && !tb.IsKeyboardFocusWithin)
            tb.Cursor = Cursors.SizeWE;
    }

    private static void OnMouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is TextBox tb && !tb.IsKeyboardFocusWithin)
            tb.Cursor = Cursors.IBeam;
    }
}
