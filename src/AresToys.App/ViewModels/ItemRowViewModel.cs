using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using AresToys.Core.Domain;
using AresToys.Storage.Items;

namespace AresToys.App.ViewModels;

public sealed class ItemRowViewModel : INotifyPropertyChanged
{
    public ItemRowViewModel(ItemRecord record, int displayIndex, bool showSnippetWithLabel = false)
    {
        Id = record.Id;
        Kind = record.Kind;
        CapturedAt = record.CreatedAt;
        Pinned = record.Pinned;
        SourceProcess = record.SourceProcess ?? string.Empty;
        Preview = BuildPreview(record);
        Thumbnail = record.Thumbnail?.ToArray();
        DisplayIndex = displayIndex;
        _label = record.Label;
        _trigger = record.Trigger;
        _showSnippetWithLabel = showSnippetWithLabel;
    }

    public long Id { get; }
    public ItemKind Kind { get; }
    public DateTimeOffset CapturedAt { get; }
    public bool Pinned { get; }
    public string SourceProcess { get; }
    public string Preview { get; }
    /// <summary>Pre-generated PNG thumbnail bytes for image items, null otherwise.</summary>
    public byte[]? Thumbnail { get; }
    public bool HasThumbnail => Thumbnail is { Length: > 0 };

    /// <summary>0-based position in the popup. Used to render a Ctrl+N hint badge for rows 0..8.</summary>
    public int DisplayIndex { get; }
    public string IndexBadge => DisplayIndex < 9 ? (DisplayIndex + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty;
    public bool HasIndexBadge => DisplayIndex < 9;

    public string KindLabel => Kind.ToString();
    public string Age => FormatAge(DateTimeOffset.UtcNow - CapturedAt);

    private string? _label;
    /// <summary>Optional CopyQ-style "Notes" string. Replaces <see cref="Preview"/> in the
    /// row title when set. Mutated by the rename gesture (right-click / F2 / preview pane);
    /// raises PropertyChanged for the dependent flags so the row re-renders in place.</summary>
    public string? Label
    {
        get => _label;
        set
        {
            if (_label == value) return;
            _label = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasLabel));
            OnPropertyChanged(nameof(TitleText));
            OnPropertyChanged(nameof(ShowSnippet));
            OnPropertyChanged(nameof(IsLabelEmpty));
        }
    }
    public bool HasLabel => !string.IsNullOrWhiteSpace(_label);
    public bool IsLabelEmpty => string.IsNullOrEmpty(_label);

    /// <summary>What the primary text in the row renders. Falls back to <see cref="Preview"/>
    /// when no label is set, so an un-labeled row keeps the legacy behaviour.</summary>
    public string TitleText => HasLabel ? _label! : Preview;

    /// <summary>Alias of <see cref="Preview"/> for the secondary line shown under a labeled
    /// title when the user enables "Show content snippet under label" in App Settings.</summary>
    public string SnippetText => Preview;

    private bool _showSnippetWithLabel;
    public bool ShowSnippetWithLabel
    {
        get => _showSnippetWithLabel;
        set
        {
            if (_showSnippetWithLabel == value) return;
            _showSnippetWithLabel = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowSnippet));
        }
    }
    /// <summary>Visibility gate for the secondary snippet line: only shown when the row has a
    /// label AND the global setting opts in. Plain rows keep a single-line layout.</summary>
    public bool ShowSnippet => HasLabel && _showSnippetWithLabel;

    private string? _trigger;
    /// <summary>Optional key-sequence trigger that maps this item to the Key Sequences module.
    /// When non-empty, typing this sequence outside the app opens the overlay listing this entry
    /// among the candidates. The value is the literal sequence string ([a-zA-Z0-9_]+), not a hash.</summary>
    public string? Trigger
    {
        get => _trigger;
        set
        {
            var normalised = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (_trigger == normalised) return;
            _trigger = normalised;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasTrigger));
        }
    }
    public bool HasTrigger => !string.IsNullOrWhiteSpace(_trigger);

    private bool _isRenaming;
    /// <summary>True while the row is in inline-rename mode (TextBox swapped in for the title
    /// TextBlock via DataTrigger). Set by the right-click "Rename label" menu and by F2 on the
    /// selected row; cleared on Enter (commit) or Esc / LostFocus (cancel).</summary>
    public bool IsRenaming
    {
        get => _isRenaming;
        set
        {
            if (_isRenaming == value) return;
            _isRenaming = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private static string BuildPreview(ItemRecord record)
    {
        if (!string.IsNullOrWhiteSpace(record.SearchText))
        {
            // Older rows (pre-fix) stored raw CF_HTML / RTF text into SearchText. Sanitize at display
            // time so the popup/list don't show "Version:0.9 StartHTML:..." gibberish.
            return Truncate(SanitizeSearchText(record.SearchText!, record.Kind), 200);
        }
        if (record.Kind is ItemKind.Image) return "[image]";
        if (record.Kind is ItemKind.Video) return "[video]";
        if (record.Kind is ItemKind.Files) return "[files]";
        if (record.Payload.Length == 0) return string.Empty;
        try { return Truncate(Encoding.UTF8.GetString(record.Payload.Span), 200); }
        catch { return "[binary]"; }
    }

    private static string SanitizeSearchText(string text, ItemKind kind)
    {
        if (kind == ItemKind.Html && text.Contains("StartHTML:", StringComparison.OrdinalIgnoreCase))
        {
            var firstTag = text.IndexOf('<');
            if (firstTag > 0) text = text[firstTag..];
            text = System.Text.RegularExpressions.Regex.Replace(text, "<[^>]+>", " ");
            text = System.Net.WebUtility.HtmlDecode(text);
            text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
        }
        else if (kind == ItemKind.Rtf && text.StartsWith(@"{\rtf", StringComparison.OrdinalIgnoreCase))
        {
            text = System.Text.RegularExpressions.Regex.Replace(text, @"\\'[0-9a-fA-F]{2}", " ");
            text = System.Text.RegularExpressions.Regex.Replace(text, @"\\[a-zA-Z]+-?\d* ?", " ");
            text = text.Replace("{", " ").Replace("}", " ");
            text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
        }
        return text;
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "…";

    private static string FormatAge(TimeSpan delta)
    {
        if (delta.TotalSeconds < 60) return $"{(int)delta.TotalSeconds}s";
        if (delta.TotalMinutes < 60) return $"{(int)delta.TotalMinutes}m";
        if (delta.TotalHours < 24) return $"{(int)delta.TotalHours}h";
        return $"{(int)delta.TotalDays}d";
    }
}
