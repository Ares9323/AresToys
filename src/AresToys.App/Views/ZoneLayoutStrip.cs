using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using AresToys.App.Services.Zones;

namespace AresToys.App.Views;

/// <summary>One card of <see cref="ZoneLayoutStrip"/>: the layout and the name shown under its miniature.</summary>
internal sealed record LayoutCard(string Id, string Name, ZoneLayout Layout);

/// <summary>
/// The miniature of a layout's zones, drawn like the overlay (fill, border, corner radius of the appearance) with
/// the 1-based zone numbers. The appearance and the monitor aspect are inherited attached properties, so the strip
/// sets them once and every card follows. Grid and canvas layouts both render; an invalid layout shows the empty frame.
/// </summary>
internal sealed class ZoneMiniPreview : FrameworkElement
{
    private const double MinPreviewHeight = 70;
    private const double MaxPreviewHeight = 130;
    private const double DefaultAspect = 16.0 / 9.0;
    private const int WorkWidth = 1000;
    private const int CanvasOpacityPercent = 55;
    private const int OpacityFloorPercent = 28;

    public static readonly DependencyProperty LayoutProperty = DependencyProperty.Register(
        nameof(Layout), typeof(ZoneLayout), typeof(ZoneMiniPreview),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AppearanceProperty = DependencyProperty.RegisterAttached(
        "Appearance", typeof(ZoneAppearance), typeof(ZoneMiniPreview),
        new FrameworkPropertyMetadata(ZoneAppearance.Default, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AspectProperty = DependencyProperty.RegisterAttached(
        "Aspect", typeof(double), typeof(ZoneMiniPreview),
        new FrameworkPropertyMetadata(DefaultAspect, FrameworkPropertyMetadataOptions.Inherits
            | FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Brush AreaBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x1B, 0x1B, 0x1B)));
    private static readonly Pen AreaPen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55))), 1));
    private static readonly Typeface LabelFace = new("Segoe UI");

    public ZoneMiniPreview()
    {
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
    }

    public ZoneLayout? Layout
    {
        get => (ZoneLayout?)GetValue(LayoutProperty);
        set => SetValue(LayoutProperty, value);
    }

    public static ZoneAppearance GetAppearance(DependencyObject d) => (ZoneAppearance)d.GetValue(AppearanceProperty);

    public static void SetAppearance(DependencyObject d, ZoneAppearance value) => d.SetValue(AppearanceProperty, value);

    public static double GetAspect(DependencyObject d) => (double)d.GetValue(AspectProperty);

    public static void SetAspect(DependencyObject d, double value) => d.SetValue(AspectProperty, value);

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsInfinity(availableSize.Width) ? 162 : availableSize.Width;
        return new Size(width, Math.Clamp(width / GetAspect(this), MinPreviewHeight, MaxPreviewHeight));
    }

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth, height = ActualHeight;
        if (width < 4 || height < 4) return;

        dc.DrawRoundedRectangle(AreaBrush, AreaPen, new Rect(0.5, 0.5, width - 1, height - 1), 3, 3);
        if (Layout is not { } layout) return;

        int workHeight = Math.Max(1, (int)Math.Round(WorkWidth / GetAspect(this)));
        var zones = ZoneGeometry.Compute(layout, new PixelRect(0, 0, WorkWidth, workHeight));
        if (zones.Count == 0) return;

        var appearance = GetAppearance(this);
        int opacity = layout.IsCanvas ? CanvasOpacityPercent : Math.Max(appearance.FillOpacityPercent, OpacityFloorPercent);
        var fill = Freeze(new SolidColorBrush(ZoneAppearance.WithOpacity(appearance.Fill, opacity)));
        var pen = Freeze(new Pen(Freeze(new SolidColorBrush(appearance.Border)), 1));

        double sx = width / WorkWidth, sy = height / workHeight;
        double radius = Math.Min(appearance.CornerRadius, 3);
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        for (int i = 0; i < zones.Count; i++)
        {
            var z = zones[i];
            var rect = new Rect(z.Left * sx, z.Top * sy, Math.Max(1, z.Width * sx), Math.Max(1, z.Height * sy));
            dc.DrawRoundedRectangle(fill, pen, rect, radius, radius);
            if (rect.Width > 18 && rect.Height > 14)
            {
                var number = new FormattedText((i + 1).ToString(CultureInfo.CurrentCulture), CultureInfo.CurrentUICulture,
                    FlowDirection.LeftToRight, LabelFace, 9, Brushes.White, dpi);
                dc.DrawText(number, new Point(rect.X + (rect.Width - number.Width) / 2, rect.Y + (rect.Height - number.Height) / 2));
            }
        }
    }

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}

/// <summary>
/// The layouts of the snapping editor as one horizontally scrolling row of cards, each a miniature of its zones
/// with the name below; the selected card is filled with the accent colour. Arrow keys move the selection and the
/// mouse wheel scrolls the row. Rebuilding the cards or selecting by id does not raise SelectionChanged: only user
/// changes (click, arrow keys) do, and they scroll the card into view.
/// </summary>
internal sealed class ZoneLayoutStrip : ListBox
{
    private const double WheelStep = 60;

    private const string ItemStyleXaml = """
        <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
               xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
               TargetType="ListBoxItem">
            <Setter Property="Width" Value="176" />
            <Setter Property="Margin" Value="0,0,8,0" />
            <Setter Property="Padding" Value="6,6,6,3" />
            <Setter Property="Cursor" Value="Hand" />
            <Setter Property="FocusVisualStyle" Value="{x:Null}" />
            <Setter Property="Template">
                <Setter.Value>
                    <ControlTemplate TargetType="ListBoxItem">
                        <Border x:Name="Bd" CornerRadius="6" BorderThickness="1" Padding="{TemplateBinding Padding}"
                                Background="{DynamicResource Surface3Brush}"
                                BorderBrush="{DynamicResource InnerBorderBrush}">
                            <ContentPresenter />
                        </Border>
                        <ControlTemplate.Triggers>
                            <Trigger Property="IsMouseOver" Value="True">
                                <Setter TargetName="Bd" Property="BorderBrush" Value="{DynamicResource AccentForegroundBrush}" />
                            </Trigger>
                            <Trigger Property="IsSelected" Value="True">
                                <Setter Property="Foreground" Value="{DynamicResource AccentForegroundBrush}" />
                                <Setter TargetName="Bd" Property="Background" Value="{DynamicResource AccentBackgroundDarkBrush}" />
                                <Setter TargetName="Bd" Property="BorderBrush" Value="{DynamicResource AccentBackgroundDarkBrush}" />
                            </Trigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>
        """;

    // The scrollbar of a horizontal strip sits in the space reserved below the cards, never over them.
    private const string ListTemplateXaml = """
        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                         TargetType="ListBox">
            <ScrollViewer Focusable="False" Padding="0"
                          HorizontalScrollBarVisibility="{TemplateBinding ScrollViewer.HorizontalScrollBarVisibility}"
                          VerticalScrollBarVisibility="Disabled">
                <ItemsPresenter Margin="0,0,0,12" />
            </ScrollViewer>
        </ControlTemplate>
        """;

    private bool _silent;
    private ScrollViewer? _scroller;

    public ZoneLayoutStrip()
    {
        Background = Brushes.Transparent;
        BorderThickness = new Thickness(0);
        Focusable = true;
        IsTabStop = true;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        SelectionMode = SelectionMode.Single;
        ScrollViewer.SetHorizontalScrollBarVisibility(this, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(this, ScrollBarVisibility.Disabled);
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        ItemsPanel = new ItemsPanelTemplate(panel);
        ItemContainerStyle = (Style)XamlReader.Parse(ItemStyleXaml);
        Template = (ControlTemplate)XamlReader.Parse(ListTemplateXaml);
        ItemTemplate = BuildCardTemplate();
    }

    /// <summary>The id of the selected card, null when nothing is selected.</summary>
    public string? SelectedLayoutId => (SelectedItem as LayoutCard)?.Id;

    /// <summary>Sets the look the miniatures are drawn with.</summary>
    public void SetAppearance(ZoneAppearance appearance) => ZoneMiniPreview.SetAppearance(this, appearance);

    /// <summary>Sets the proportions (width over height) of the monitor the miniatures show; cards resize to match.</summary>
    public void SetMonitorAspect(double widthOverHeight)
    {
        if (widthOverHeight > 0)
        {
            ZoneMiniPreview.SetAspect(this, widthOverHeight);
        }
    }

    /// <summary>Rebuilds the cards in the given order and selects the one with this id (none when null or unknown).</summary>
    public void SetLayouts(IReadOnlyList<ZoneLayout> layouts, string? selectedId)
    {
        var cards = new ObservableCollection<LayoutCard>(layouts.Select(l => new LayoutCard(l.Id, l.Name, l)));
        RunSilent(() =>
        {
            ItemsSource = cards;
            SelectedIndex = IndexOf(selectedId);
        });
        ScrollSelectedIntoView();
    }

    /// <summary>Selects the card with this id without raising SelectionChanged and scrolls it into view.</summary>
    public void SelectLayout(string? id)
    {
        RunSilent(() => SelectedIndex = IndexOf(id));
        ScrollSelectedIntoView();
    }

    /// <summary>Replaces the card with the same id (unsaved edits show live), keeping the selection and the scroll position.</summary>
    public void UpdateLayout(ZoneLayout layout)
    {
        if (ItemsSource is not ObservableCollection<LayoutCard> cards)
        {
            return;
        }
        int index = IndexOf(layout.Id);
        if (index < 0)
        {
            return;
        }
        int selected = SelectedIndex;
        var scroller = FindScroller();
        double offset = scroller?.HorizontalOffset ?? 0;
        RunSilent(() =>
        {
            cards[index] = new LayoutCard(layout.Id, layout.Name, layout);
            SelectedIndex = selected;
        });
        if (scroller is not null)
        {
            UpdateLayout();
            scroller.ScrollToHorizontalOffset(offset);
        }
    }

    protected override void OnSelectionChanged(SelectionChangedEventArgs e)
    {
        if (_silent)
        {
            return;
        }
        base.OnSelectionChanged(e);
        ScrollSelectedIntoView();
    }

    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        base.OnPreviewMouseWheel(e);
        if (e.Delta == 0 || FindScroller() is not { } scroller)
        {
            return;
        }
        double notches = e.Delta / 120.0;
        scroller.ScrollToHorizontalOffset(scroller.HorizontalOffset - notches * WheelStep);
        e.Handled = true;
    }

    private static DataTemplate BuildCardTemplate()
    {
        var preview = new FrameworkElementFactory(typeof(ZoneMiniPreview));
        preview.SetBinding(ZoneMiniPreview.LayoutProperty, new Binding(nameof(LayoutCard.Layout)));

        var name = new FrameworkElementFactory(typeof(TextBlock));
        name.SetValue(TextBlock.FontSizeProperty, 12.0);
        name.SetValue(TextBlock.TextAlignmentProperty, TextAlignment.Center);
        name.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        name.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 4, 0, 0));
        name.SetBinding(TextBlock.TextProperty, new Binding(nameof(LayoutCard.Name)));
        name.SetBinding(FrameworkElement.ToolTipProperty, new Binding(nameof(LayoutCard.Name)));

        var stack = new FrameworkElementFactory(typeof(StackPanel));
        stack.SetValue(StackPanel.OrientationProperty, Orientation.Vertical);
        stack.AppendChild(preview);
        stack.AppendChild(name);
        return new DataTemplate { VisualTree = stack };
    }

    private int IndexOf(string? id)
    {
        if (id is null || ItemsSource is not ObservableCollection<LayoutCard> cards)
        {
            return -1;
        }
        for (int i = 0; i < cards.Count; i++)
        {
            if (cards[i].Id == id)
            {
                return i;
            }
        }
        return -1;
    }

    private void RunSilent(Action action)
    {
        _silent = true;
        try
        {
            action();
        }
        finally
        {
            _silent = false;
        }
    }

    private void ScrollSelectedIntoView()
    {
        if (SelectedItem is not null)
        {
            ScrollIntoView(SelectedItem);
        }
    }

    // The ListBox template (default or themed) wraps its items in one ScrollViewer whose part name is not
    // guaranteed: walk the visual tree after applying the template and cache the result.
    private ScrollViewer? FindScroller()
    {
        if (_scroller is not null)
        {
            return _scroller;
        }
        ApplyTemplate();
        _scroller = FindDescendant(this);
        return _scroller;
    }

    private static ScrollViewer? FindDescendant(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer viewer)
            {
                return viewer;
            }
            if (FindDescendant(child) is { } found)
            {
                return found;
            }
        }
        return null;
    }
}
