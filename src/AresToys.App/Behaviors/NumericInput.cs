using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace AresToys.App.Behaviors;

/// <summary>App-wide numeric-input nudge for every TextBox / ui:TextBox:
/// <list type="bullet">
/// <item>Wheel up / Up arrow → +1</item>
/// <item>Wheel down / Down arrow → -1</item>
/// <item>+ Shift → ×5 (so ±5)</item>
/// </list>
/// Only fires when the TextBox is the keyboard-focused element (wheel over an unfocused
/// numeric field still scrolls the parent panel like the user expects). Non-numeric content
/// passes the event through unchanged (TryParse fails → handler doesn't mark e.Handled), so
/// the cue is harmless on non-numeric fields — it just becomes a no-op. Decimal precision is
/// preserved when the original value carried a separator.
///
/// Implementation: <see cref="System.Windows.EventManager.RegisterClassHandler"/> attaches
/// the handlers at the TYPE level once at app startup — no Style / Template / attached
/// property is touched, so the WPF / WPF-UI default visuals stay intact across the app.
/// We tried an implicit Style + EventSetter route first; it broke the WPF-UI ui:TextBox
/// rendering because the implicit Style competed with the themed dictionary registration in
/// a way <c>BasedOn</c> couldn't paper over.</summary>
public static class NumericInput
{
    /// <summary>Wire the class-level handlers once at app startup. Idempotent — calling twice
    /// won't double-register because we guard with <see cref="_registered"/>.</summary>
    public static void RegisterClassHandlers()
    {
        if (_registered) return;
        _registered = true;
        EventManager.RegisterClassHandler(typeof(TextBox),
            UIElement.PreviewMouseWheelEvent,
            new MouseWheelEventHandler(OnPreviewMouseWheel),
            handledEventsToo: false);
        EventManager.RegisterClassHandler(typeof(TextBox),
            UIElement.PreviewKeyDownEvent,
            new KeyEventHandler(OnPreviewKeyDown),
            handledEventsToo: false);
        // ui:TextBox derives from System.Windows.Controls.TextBox, so the registration above
        // covers it transparently — no separate hook needed.
    }
    private static bool _registered;

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not TextBox tb || !tb.IsKeyboardFocused) return;
        if (!TryAdjust(tb, e.Delta > 0 ? 1 : -1)) return;
        e.Handled = true;
    }

    private static void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox tb) return;
        if (e.Key != Key.Up && e.Key != Key.Down) return;
        // Multi-line TextBoxes use Up/Down for line navigation — leave that alone.
        if (tb.AcceptsReturn) return;
        if (!TryAdjust(tb, e.Key == Key.Up ? 1 : -1)) return;
        e.Handled = true;
    }

    /// <summary>Parse current text → adjust → write back. Returns false (caller passes the
    /// event through) when the text isn't numeric, so applying this behavior to a non-numeric
    /// field just makes it a no-op rather than corrupting the content.</summary>
    private static bool TryAdjust(TextBox tb, int direction)
    {
        var text = tb.Text;
        if (string.IsNullOrEmpty(text)) return false;
        // Try invariant first (most VM properties round-trip through it), then current
        // culture as a fallback for user-typed values with locale separators.
        if (!double.TryParse(text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var value)
            && !double.TryParse(text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.CurrentCulture, out value))
        {
            return false;
        }
        var stepMagnitude = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift ? 5 : 1;
        var newValue = value + direction * stepMagnitude;
        // Preserve integer formatting when the original looked integer; otherwise format with
        // up to 2 decimals (matches the typical slider readout precision in our UI).
        var hasDecimal = text.Contains('.', StringComparison.Ordinal) || text.Contains(',', StringComparison.Ordinal);
        var newText = hasDecimal
            ? newValue.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)
            : ((long)Math.Round(newValue)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        tb.Text = newText;
        tb.CaretIndex = tb.Text.Length;
        // Push to the binding source immediately — VM should see the new value before focus
        // loss, otherwise the next wheel tick may run against stale VM state.
        var binding = BindingOperations.GetBindingExpression(tb, TextBox.TextProperty);
        binding?.UpdateSource();
        return true;
    }
}
