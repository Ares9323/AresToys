using System.Collections.ObjectModel;
using AresToys.Editor.Model;
using AresToys.Editor.Tools;

namespace AresToys.Editor.ViewModels;

/// <summary>Owns, for every styled tool, the style its next shape gets (always the last one the
/// user set) and a Recent list of the styles that were actually placed. Changing the current
/// style never touches Recent: only <see cref="Remember"/> does, and the editor calls it when a
/// shape is placed.</summary>
public sealed class ToolStyleBook
{
    /// <summary>Most entries one tool's Recent list keeps.</summary>
    public const int MaxRecent = 10;

    private static readonly EditorTool[] StyledTools =
    [
        EditorTool.Rectangle, EditorTool.Arrow, EditorTool.Line, EditorTool.Ellipse, EditorTool.Freehand,
        EditorTool.Text, EditorTool.StepCounter, EditorTool.Blur, EditorTool.Pixelate, EditorTool.Spotlight
    ];

    private sealed class Slot
    {
        public required ToolStyle Current { get; set; }
        public List<ToolStyle> Recent { get; } = [];
        public ReadOnlyCollection<ToolStyle> RecentView { get; }

        public Slot() => RecentView = new ReadOnlyCollection<ToolStyle>(Recent);
    }

    private readonly Dictionary<EditorTool, Slot> _slots = [];

    public ToolStyleBook()
    {
        foreach (var tool in StyledTools)
            _slots[tool] = new Slot { Current = ToolStyle.InitialFor(tool) };
    }

    /// <summary>Raised with the tool whose current style or Recent list changed.</summary>
    public event Action<EditorTool>? Changed;

    /// <summary>The tools that carry a style (every placement tool; not Select, Crop or SmartEraser).</summary>
    public static IReadOnlyList<EditorTool> Tools => StyledTools;

    public static bool IsStyled(EditorTool tool) => Array.IndexOf(StyledTools, tool) >= 0;

    public ToolStyle Current(EditorTool tool) => SlotFor(tool).Current;

    /// <summary>Make <paramref name="style"/> the tool's current style. Does nothing, and raises
    /// nothing, when it equals the current one.</summary>
    public void SetCurrent(EditorTool tool, ToolStyle style)
    {
        ArgumentNullException.ThrowIfNull(style);
        var slot = SlotFor(tool);
        if (slot.Current.Equals(style)) return;
        slot.Current = style;
        Changed?.Invoke(tool);
    }

    /// <summary>The tool's Recent list, newest first. A live read-only view: it reflects later changes.</summary>
    public IReadOnlyList<ToolStyle> Recent(EditorTool tool) => SlotFor(tool).RecentView;

    /// <summary>Record that <paramref name="style"/> was used to place a shape: it goes on top of
    /// the Recent list (an equal entry further down moves up, an equal top entry stays), and the
    /// list keeps at most <see cref="MaxRecent"/> entries.</summary>
    public void Remember(EditorTool tool, ToolStyle style)
    {
        ArgumentNullException.ThrowIfNull(style);
        var recent = SlotFor(tool).Recent;
        if (recent.Count > 0 && recent[0].Equals(style)) return;
        var existing = recent.FindIndex(s => s.Equals(style));
        if (existing > 0) recent.RemoveAt(existing);
        recent.Insert(0, style);
        if (recent.Count > MaxRecent) recent.RemoveRange(MaxRecent, recent.Count - MaxRecent);
        Changed?.Invoke(tool);
    }

    /// <summary>Delete one Recent entry. An index outside the list does nothing.</summary>
    public void RemoveRecent(EditorTool tool, int index)
    {
        var recent = SlotFor(tool).Recent;
        if (index < 0 || index >= recent.Count) return;
        recent.RemoveAt(index);
        Changed?.Invoke(tool);
    }

    /// <summary>Replace a tool's Recent list wholesale (loading from disk). Raises nothing: the
    /// book is not observed yet when it is being filled.</summary>
    internal void LoadRecent(EditorTool tool, IEnumerable<ToolStyle> styles)
    {
        var recent = SlotFor(tool).Recent;
        recent.Clear();
        foreach (var s in styles)
        {
            if (recent.Count >= MaxRecent) break;
            if (!recent.Contains(s)) recent.Add(s);
        }
    }

    private Slot SlotFor(EditorTool tool) =>
        _slots.TryGetValue(tool, out var slot)
            ? slot
            : throw new ArgumentOutOfRangeException(nameof(tool), tool, "This tool has no style.");
}
