using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using AresToys.App.Resources;
using AresToys.App.Services.Zones;

namespace AresToys.App.Views;

/// <summary>
/// One tile of <see cref="ZoneMonitorStrip"/>. <see cref="Number"/> is the 1-based monitor number (the one the
/// identify windows show), 0 for the combined span monitor, which shows no number and no star.
/// </summary>
internal sealed record MonitorTile(ZoneMonitor Monitor, int Number)
{
    private bool IsSpan => Monitor.Id == ZoneMonitors.SpanId;

    public string NumberText => IsSpan ? Strings.WindowSnapping_MonitorAll : Number.ToString(CultureInfo.CurrentCulture);

    public string StarText => !IsSpan && Monitor.IsPrimary ? "★" : string.Empty;

    public string Resolution => string.Format(CultureInfo.CurrentCulture, "{0} x {1}", Monitor.WorkArea.Width, Monitor.WorkArea.Height);

}

/// <summary>
/// The monitors of the layout editor as one horizontal row of tiles (number, star on the primary, resolution);
/// the selected tile is filled with the accent colour. Arrow keys and the mouse wheel (when the strip has keyboard
/// focus) move the selection. Rebuilding the tiles or selecting by id does not raise SelectionChanged: only user
/// changes do.
/// </summary>
internal sealed class ZoneMonitorStrip : ListBox
{
    private const string ItemStyleXaml = """
        <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
               xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
               TargetType="ListBoxItem">
            <Setter Property="Width" Value="150" />
            <Setter Property="Height" Value="74" />
            <Setter Property="Margin" Value="0,0,8,0" />
            <Setter Property="Padding" Value="0" />
            <Setter Property="Cursor" Value="Hand" />
            <Setter Property="Foreground" Value="{DynamicResource AccentForegroundBrush}" />
            <Setter Property="FocusVisualStyle" Value="{x:Null}" />
            <Setter Property="Template">
                <Setter.Value>
                    <ControlTemplate TargetType="ListBoxItem">
                        <Border x:Name="Bd" CornerRadius="6" BorderThickness="1"
                                Background="{DynamicResource Surface3Brush}"
                                BorderBrush="{DynamicResource InnerBorderBrush}">
                            <StackPanel VerticalAlignment="Center" HorizontalAlignment="Center">
                                <TextBlock HorizontalAlignment="Center" FontSize="20" FontWeight="SemiBold"
                                           Foreground="{TemplateBinding Foreground}">
                                    <Run Text="{Binding NumberText, Mode=OneWay}" /><Run Text="{Binding StarText, Mode=OneWay}"
                                         FontFamily="Segoe UI Symbol" />
                                </TextBlock>
                                <TextBlock HorizontalAlignment="Center" FontSize="12" Text="{Binding Resolution}"
                                           Foreground="{TemplateBinding Foreground}" />
                            </StackPanel>
                        </Border>
                        <ControlTemplate.Triggers>
                            <Trigger Property="IsMouseOver" Value="True">
                                <Setter TargetName="Bd" Property="BorderBrush" Value="{DynamicResource AccentForegroundBrush}" />
                            </Trigger>
                            <Trigger Property="IsSelected" Value="True">
                                <Setter TargetName="Bd" Property="Background" Value="{DynamicResource AccentBackgroundDarkBrush}" />
                                <Setter TargetName="Bd" Property="BorderBrush" Value="{DynamicResource AccentBackgroundDarkBrush}" />
                            </Trigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>
        """;

    private bool _silent;

    public ZoneMonitorStrip()
    {
        Background = System.Windows.Media.Brushes.Transparent;
        BorderThickness = new Thickness(0);
        Focusable = true;
        IsTabStop = true;
        SelectionMode = SelectionMode.Single;
        ScrollViewer.SetHorizontalScrollBarVisibility(this, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(this, ScrollBarVisibility.Disabled);
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        ItemsPanel = new ItemsPanelTemplate(panel);
        ItemContainerStyle = (Style)XamlReader.Parse(ItemStyleXaml);
    }

    /// <summary>The monitor of the selected tile, null when there are no tiles.</summary>
    public ZoneMonitor? SelectedMonitor => (SelectedItem as MonitorTile)?.Monitor;

    /// <summary>Rebuilds the tiles. Keeps the selected monitor when its id still exists, else selects the first.</summary>
    public void SetMonitors(IReadOnlyList<ZoneMonitor> monitors)
    {
        string? keep = SelectedMonitor?.Id;
        var tiles = new List<MonitorTile>(monitors.Count);
        for (int i = 0; i < monitors.Count; i++)
        {
            tiles.Add(new MonitorTile(monitors[i], monitors[i].Id == ZoneMonitors.SpanId ? 0 : i + 1));
        }
        _silent = true;
        try
        {
            ItemsSource = tiles;
            int index = keep is null ? -1 : tiles.FindIndex(t => t.Monitor.Id == keep);
            SelectedIndex = index >= 0 ? index : (tiles.Count > 0 ? 0 : -1);
        }
        finally
        {
            _silent = false;
        }
    }

    /// <summary>Selects the tile with this monitor id without raising SelectionChanged. Unknown ids are ignored.</summary>
    public void SelectMonitor(string id)
    {
        if (ItemsSource is not List<MonitorTile> tiles)
        {
            return;
        }
        int index = tiles.FindIndex(t => t.Monitor.Id == id);
        if (index < 0)
        {
            return;
        }
        _silent = true;
        try
        {
            SelectedIndex = index;
        }
        finally
        {
            _silent = false;
        }
    }

    protected override void OnSelectionChanged(SelectionChangedEventArgs e)
    {
        if (!_silent)
        {
            base.OnSelectionChanged(e);
        }
    }

    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        base.OnPreviewMouseWheel(e);
        if (!IsKeyboardFocusWithin || e.Delta == 0 || Items.Count == 0)
        {
            return;
        }
        int next = Math.Clamp(SelectedIndex + (e.Delta > 0 ? -1 : 1), 0, Items.Count - 1);
        if (next != SelectedIndex)
        {
            SelectedIndex = next;
        }
        e.Handled = true;
    }
}
