using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shapes;
using AresToys.App.Resources;
using AresToys.App.Services.Zones;
using Wpf.Ui.Controls;
using TextBox = System.Windows.Controls.TextBox;

namespace AresToys.App.Views;

/// <summary>Asks for the number of rows and columns of a new grid layout (1 to <see cref="ZoneLayoutPresets.MaxGridLines"/>
/// each) and shows the zone count and a small preview. <see cref="Rows"/> and <see cref="Columns"/> hold the answer
/// when the dialog closes with OK.</summary>
public sealed partial class ZoneGridSizeDialog : FluentWindow
{
    private const int DefaultRows = 2;
    private const int DefaultColumns = 3;

    public ZoneGridSizeDialog()
    {
        InitializeComponent();
        RowsBox.Text = DefaultRows.ToString(CultureInfo.CurrentCulture);
        ColumnsBox.Text = DefaultColumns.ToString(CultureInfo.CurrentCulture);
        Loaded += (_, _) =>
        {
            RowsBox.Focus();
            RowsBox.SelectAll();
        };
    }

    public int Rows { get; private set; } = DefaultRows;

    public int Columns { get; private set; } = DefaultColumns;

    /// <summary>A whole number of at least 1, limited to the maximum; false for empty, zero or non numeric text.</summary>
    private static bool TryRead(TextBox box, out int value)
    {
        value = 0;
        if (!int.TryParse(box.Text.Trim(), NumberStyles.None, CultureInfo.CurrentCulture, out int parsed) || parsed < 1) return false;
        value = Math.Min(parsed, ZoneLayoutPresets.MaxGridLines);
        return true;
    }

    private void OnNumberInput(object sender, TextCompositionEventArgs e) => e.Handled = !e.Text.All(char.IsAsciiDigit);

    private void OnSizeChanged(object sender, TextChangedEventArgs e)
    {
        // Controls are not all created yet while InitializeComponent sets the first text.
        if (OkButton is null || CountText is null) return;
        bool valid = TryRead(RowsBox, out int rows) & TryRead(ColumnsBox, out int columns);
        OkButton.IsEnabled = valid;
        if (valid)
        {
            int zones = rows * columns;
            CountText.Text = string.Format(CultureInfo.CurrentCulture,
                zones == 1 ? Strings.WindowSnapping_GridZoneOne : Strings.WindowSnapping_GridZonesFormat, zones);
        }
        else
        {
            CountText.Text = string.Empty;
        }

        DrawPreview();
    }

    /// <summary>Shows the clamped value back in the box once the user leaves it.</summary>
    private void OnSizeLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox box && TryRead(box, out int value))
        {
            box.Text = value.ToString(CultureInfo.CurrentCulture);
        }
    }

    private void OnPreviewSizeChanged(object sender, SizeChangedEventArgs e) => DrawPreview();

    private void DrawPreview()
    {
        if (PreviewCanvas is null) return;
        PreviewCanvas.Children.Clear();
        if (!TryRead(RowsBox, out int rows) || !TryRead(ColumnsBox, out int columns)) return;
        double width = PreviewCanvas.ActualWidth, height = PreviewCanvas.ActualHeight;
        if (width <= 0 || height <= 0) return;

        const double gap = 2;
        double cellWidth = (width - gap * (columns + 1)) / columns;
        double cellHeight = (height - gap * (rows + 1)) / rows;
        if (cellWidth <= 0 || cellHeight <= 0) return;
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < columns; c++)
            {
                var cell = new Rectangle
                {
                    Width = cellWidth,
                    Height = cellHeight,
                    RadiusX = 2,
                    RadiusY = 2,
                    Opacity = 0.7,
                };
                cell.SetResourceReference(Shape.FillProperty, "AccentBackgroundBrush");
                Canvas.SetLeft(cell, gap + c * (cellWidth + gap));
                Canvas.SetTop(cell, gap + r * (cellHeight + gap));
                PreviewCanvas.Children.Add(cell);
            }
        }
    }

    private void OnOkClicked(object sender, RoutedEventArgs e)
    {
        if (!TryRead(RowsBox, out int rows))
        {
            RowsBox.Focus();
            return;
        }

        if (!TryRead(ColumnsBox, out int columns))
        {
            ColumnsBox.Focus();
            return;
        }

        Rows = rows;
        Columns = columns;
        DialogResult = true;
        Close();
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
