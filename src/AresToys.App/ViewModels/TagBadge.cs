using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using AresToys.Storage.Items;

namespace AresToys.App.ViewModels;

/// <summary>Display model of one clipboard tag (issue #4): the small chip in a history row,
/// the filter chip under the search bar, the swatch in Settings. A tag without a colour renders
/// with the neutral theme surface (the XAML falls back to theme brushes when
/// <see cref="HasColor"/> is false); a coloured tag gets its own fill plus a black or white
/// text brush picked for contrast.</summary>
public sealed class TagBadge
{
    public TagBadge(Tag tag)
    {
        Id = tag.Id;
        Name = tag.Name;
        Color = tag.Color;
        ItemCount = tag.ItemCount;
        if (TryParse(tag.Color, out var c))
        {
            ColorBrush = Freeze(new SolidColorBrush(c));
            // Relative luminance (sRGB weights, no gamma: plenty for a pick between two inks).
            var luminance = (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;
            TextBrush = luminance > 0.55 ? Brushes.Black : Brushes.White;
        }
    }

    public long Id { get; }
    public string Name { get; }
    /// <summary><c>#RRGGBB</c> or null (neutral).</summary>
    public string? Color { get; }
    public int ItemCount { get; }
    public bool HasColor => ColorBrush is not null;
    public Brush? ColorBrush { get; }
    public Brush? TextBrush { get; }

    private static bool TryParse(string? hex, out System.Windows.Media.Color color)
    {
        color = default;
        if (string.IsNullOrEmpty(hex)) return false;
        try
        {
            color = (System.Windows.Media.Color)ColorConverter.ConvertFromString(hex);
            return true;
        }
        catch (FormatException) { return false; }
    }

    private static SolidColorBrush Freeze(SolidColorBrush b)
    {
        b.Freeze();
        return b;
    }
}

/// <summary>One toggle chip in the clipboard window's tag filter strip. <see cref="IsActive"/>
/// is bound two-way to the chip's ToggleButton; the VM reads the active set on every toggle.</summary>
public sealed partial class TagFilterChip : ObservableObject
{
    public TagFilterChip(TagBadge badge, bool isActive)
    {
        Badge = badge;
        _isActive = isActive;
    }

    public TagBadge Badge { get; }

    [ObservableProperty]
    private bool _isActive;
}
