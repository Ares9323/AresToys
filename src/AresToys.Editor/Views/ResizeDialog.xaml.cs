using System.Globalization;
using System.Windows;

namespace AresToys.Editor.Views;

public partial class ResizeDialog : Window
{
    private readonly int _origW, _origH;
    private bool _suppress;

    public ResizeDialog(int origWidth, int origHeight)
    {
        InitializeComponent();
        _origW = origWidth;
        _origH = origHeight;
        OriginalSizeLabel.Text = $"Original: {origWidth} × {origHeight} px";
        WidthBox.Text = origWidth.ToString(CultureInfo.InvariantCulture);
        HeightBox.Text = origHeight.ToString(CultureInfo.InvariantCulture);

        WidthBox.TextChanged += (_, _) => OnWidthChanged();
        HeightBox.TextChanged += (_, _) => OnHeightChanged();
        PercentBox.TextChanged += (_, _) => OnPercentChanged();
    }

    public int NewWidth { get; private set; }
    public int NewHeight { get; private set; }

    private void OnWidthChanged()
    {
        if (_suppress) return;
        if (!int.TryParse(WidthBox.Text, out var w) || w < 1) return;
        _suppress = true;
        try
        {
            if (MaintainAspectCheck.IsChecked == true)
            {
                var h = (int)Math.Round(w * (double)_origH / _origW);
                HeightBox.Text = h.ToString(CultureInfo.InvariantCulture);
            }
            PercentBox.Text = ((int)Math.Round(100.0 * w / _origW)).ToString(CultureInfo.InvariantCulture);
        }
        finally { _suppress = false; }
    }

    private void OnHeightChanged()
    {
        if (_suppress) return;
        if (!int.TryParse(HeightBox.Text, out var h) || h < 1) return;
        _suppress = true;
        try
        {
            if (MaintainAspectCheck.IsChecked == true)
            {
                var w = (int)Math.Round(h * (double)_origW / _origH);
                WidthBox.Text = w.ToString(CultureInfo.InvariantCulture);
            }
            PercentBox.Text = ((int)Math.Round(100.0 * h / _origH)).ToString(CultureInfo.InvariantCulture);
        }
        finally { _suppress = false; }
    }

    private void OnPercentChanged()
    {
        if (_suppress) return;
        if (!double.TryParse(PercentBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) || p <= 0) return;
        _suppress = true;
        try
        {
            WidthBox.Text = ((int)Math.Round(_origW * p / 100)).ToString(CultureInfo.InvariantCulture);
            HeightBox.Text = ((int)Math.Round(_origH * p / 100)).ToString(CultureInfo.InvariantCulture);
        }
        finally { _suppress = false; }
    }

    private void OnOkClicked(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(WidthBox.Text, out var w) || w < 1) return;
        if (!int.TryParse(HeightBox.Text, out var h) || h < 1) return;
        NewWidth = w;
        NewHeight = h;
        DialogResult = true;
        Close();
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
