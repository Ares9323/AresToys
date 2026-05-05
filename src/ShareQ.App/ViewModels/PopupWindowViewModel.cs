using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using ShareQ.App.Services;
using ShareQ.Core.Domain;
using ShareQ.Storage.Items;

namespace ShareQ.App.ViewModels;

public sealed partial class PopupWindowViewModel : ObservableObject, IDisposable
{
    private readonly IItemStore _items;
    private readonly ICategoryStore _categories;
    private readonly IServiceProvider _services;
    private long _previewLoadToken;
    private string? _selectedItemBlobRef;

    public PopupWindowViewModel(IItemStore items, ICategoryStore categories, IServiceProvider services)
    {
        _items = items;
        _categories = categories;
        _services = services;
        Rows = [];
        Categories = [];
        _items.ItemsChanged += OnItemsChanged;
        _categories.Changed += OnCategoriesChanged;
        _ = ReloadCategoriesAsync();
    }

    public void Dispose()
    {
        _items.ItemsChanged -= OnItemsChanged;
        _categories.Changed -= OnCategoriesChanged;
    }

    private void OnCategoriesChanged(object? sender, EventArgs e)
        => Application.Current?.Dispatcher.InvokeAsync(() => _ = ReloadCategoriesAsync());

    private async Task ReloadCategoriesAsync()
    {
        var list = await _categories.ListAsync(CancellationToken.None).ConfigureAwait(true);
        Categories.Clear();
        MovableCategories.Clear();
        foreach (var c in list)
        {
            var tab = new CategoryTab(c.Name, c.Name, c.Icon, IsActive: c.Name == ActiveCategory);
            Categories.Add(tab);
            MovableCategories.Add(tab);
        }
        // No synthetic "All" any more — default to the first real category (always
        // "Clipboard" on a fresh DB thanks to Migration001InitialSchema) when the user
        // hasn't picked one yet, or when the previously-active category was deleted.
        if (Categories.Count > 0 && (ActiveCategory is null || !Categories.Any(t => t.Name == ActiveCategory)))
        {
            ActiveCategory = Categories[0].Name;
        }
    }

    private void OnItemsChanged(object? sender, ItemsChangedEventArgs e)
    {
        // Marshal to UI thread; Refresh updates the ObservableCollection.
        Application.Current?.Dispatcher.InvokeAsync(() => _ = RefreshAsync(CancellationToken.None));
    }

    public ObservableCollection<ItemRowViewModel> Rows { get; }
    public ObservableCollection<CategoryTab> Categories { get; }
    /// <summary>Categories without the synthetic "All" entry — feeds the right-click "Move to"
    /// submenu where the only meaningful targets are real, persistent buckets.</summary>
    public ObservableCollection<CategoryTab> MovableCategories { get; } = [];

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private ItemRowViewModel? _selectedRow;

    [ObservableProperty]
    private ItemKind? _kindFilter;

    /// <summary>Currently selected category. <c>null</c> = "All" tab (no filter).</summary>
    [ObservableProperty]
    private string? _activeCategory;

    partial void OnActiveCategoryChanged(string? value)
    {
        // Reflect the new active flag in the tab strip + reload items.
        for (var i = 0; i < Categories.Count; i++)
        {
            var t = Categories[i];
            Categories[i] = t with { IsActive = t.Name == value };
        }
        _ = RefreshAsync(CancellationToken.None);
    }

    public bool IsAllFilter => KindFilter is null;
    public bool IsTextFilter => KindFilter == ItemKind.Text;
    public bool IsImageFilter => KindFilter == ItemKind.Image;

    partial void OnKindFilterChanged(ItemKind? value)
    {
        OnPropertyChanged(nameof(IsAllFilter));
        OnPropertyChanged(nameof(IsTextFilter));
        OnPropertyChanged(nameof(IsImageFilter));
        _ = RefreshAsync(CancellationToken.None);
    }

    // Preview state for the side panel. Code-behind on the clipboard window watches Rtf/Html
    // bytes since those formats can't be data-bound directly into RichTextBox / WebBrowser.
    [ObservableProperty] private PreviewKind _previewKind = PreviewKind.None;
    [ObservableProperty] private string? _previewText;
    [ObservableProperty] private byte[]? _previewImageBytes;
    [ObservableProperty] private byte[]? _previewRtfBytes;
    [ObservableProperty] private string? _previewHtml;
    [ObservableProperty] private string? _previewMeta;

    public bool IsTextPreview => PreviewKind == PreviewKind.Text;
    public bool IsHtmlPreview => PreviewKind == PreviewKind.Html;
    public bool IsRtfPreview => PreviewKind == PreviewKind.Rtf;
    public bool IsImagePreview => PreviewKind == PreviewKind.Image;
    public bool HasPreview => PreviewKind != PreviewKind.None;

    partial void OnPreviewKindChanged(PreviewKind value)
    {
        OnPropertyChanged(nameof(IsTextPreview));
        OnPropertyChanged(nameof(IsHtmlPreview));
        OnPropertyChanged(nameof(IsRtfPreview));
        OnPropertyChanged(nameof(IsImagePreview));
        OnPropertyChanged(nameof(HasPreview));
    }

    partial void OnSelectedRowChanged(ItemRowViewModel? value)
    {
        // Cancel any in-flight load and start fresh.
        var token = System.Threading.Interlocked.Increment(ref _previewLoadToken);
        _ = LoadPreviewAsync(value, token);
    }

    private async Task LoadPreviewAsync(ItemRowViewModel? row, long token)
    {
        if (row is null)
        {
            _selectedItemBlobRef = null;
            ApplyPreview(PreviewKind.None, null, null, null, null, null);
            NotifyCommandsCanExecuteChanged();
            return;
        }

        var record = await _items.GetByIdAsync(row.Id, CancellationToken.None).ConfigureAwait(true);
        if (token != System.Threading.Interlocked.Read(ref _previewLoadToken)) return;
        if (record is null) { ApplyPreview(PreviewKind.None, null, null, null, null, null); return; }
        _selectedItemBlobRef = record.BlobRef;

        var payload = record.Payload;
        var meta = $"{record.Kind} · {row.SourceProcess} · {row.Age}";
        switch (record.Kind)
        {
            case ItemKind.Text:
                ApplyPreview(PreviewKind.Text, Encoding.UTF8.GetString(payload.Span), null, null, null, meta);
                break;
            case ItemKind.Html:
                ApplyPreview(PreviewKind.Html, null, null, null, Encoding.UTF8.GetString(payload.Span), meta);
                break;
            case ItemKind.Rtf:
                ApplyPreview(PreviewKind.Rtf, null, null, payload.ToArray(), null, meta);
                break;
            case ItemKind.Image:
                ApplyPreview(PreviewKind.Image, null, payload.ToArray(), null, null, meta);
                break;
            case ItemKind.Files:
                ApplyPreview(PreviewKind.Text, Encoding.UTF8.GetString(payload.Span), null, null, null, meta);
                break;
            default:
                ApplyPreview(PreviewKind.None, null, null, null, null, meta);
                break;
        }
        NotifyCommandsCanExecuteChanged();
    }

    private void NotifyCommandsCanExecuteChanged()
    {
        TogglePinSelectedCommand.NotifyCanExecuteChanged();
        DeleteSelectedCommand.NotifyCanExecuteChanged();
        PasteSelectedCommand.NotifyCanExecuteChanged();
        OpenInEditorCommand.NotifyCanExecuteChanged();
        OpenInExplorerCommand.NotifyCanExecuteChanged();
        OpenInBrowserCommand.NotifyCanExecuteChanged();
        CaptureWebpageCommand.NotifyCanExecuteChanged();
        // CanCaptureWebpage = IsUrlSelected && WebView2 available — the second factor is a
        // process-lifetime constant, but the predicate fans out through every selection
        // change because it composes IsUrlSelected.
        // The XAML toolbar binds Visibility to these flags so the icons disappear (rather
        // than just disabling) when they don't apply to the current selection — matches the
        // behaviour the user expects from a discoverable shortcut bar.
        OnPropertyChanged(nameof(IsImageSelected));
        OnPropertyChanged(nameof(HasFileOnDisk));
        OnPropertyChanged(nameof(IsUrlSelected));
        OnPropertyChanged(nameof(IsTextSelected));
        OnPropertyChanged(nameof(CanCaptureWebpage));
    }

    /// <summary>True when the current selection is an image — gates the "Open in editor"
    /// affordance (text / file rows have nothing to edit in the image annotation editor).</summary>
    public bool IsImageSelected => SelectedRow?.Kind == ItemKind.Image;

    /// <summary>True when the current selection holds text-shaped content — gates the
    /// "Generate QR code…" affordance (toolbar + context menu). A QR code carries a textual
    /// payload, so on Image / Video / Files rows the affordance has nothing to encode and
    /// is hidden.</summary>
    public bool IsTextSelected =>
        SelectedRow?.Kind is ItemKind.Text or ItemKind.Html or ItemKind.Rtf;

    /// <summary>True when the selected item has a saved file on disk (BlobRef populated by
    /// the SaveToFile pipeline step). Gates the "Show in explorer" affordance.</summary>
    public bool HasFileOnDisk => !string.IsNullOrEmpty(_selectedItemBlobRef);

    /// <summary>True when the selected item resolves to a parseable http(s) URL — gates the
    /// "Open in browser" affordance. Covers two cases: a plain-text row whose entire payload
    /// is the URL (manual paste / "copy as plain text"), and an HTML row whose first
    /// <c>href</c> is an http(s) link (browser "Copy link" puts HTML on the clipboard, which
    /// the reader prefers over plain text).</summary>
    public bool IsUrlSelected => ResolveSelectedUrl() is not null;

    /// <summary>True when (a) the selection has a URL and (b) the WebView2 Runtime is
    /// installed. Gates the "Capture webpage" affordance separately from "Open URL in
    /// browser" — the latter only needs Process.Start and works without any runtime, but the
    /// full-page screenshot needs a hosted browser. On machines without WebView2 the button
    /// hides and the user can install the runtime via the tray menu's fallback entry.</summary>
    public bool CanCaptureWebpage =>
        IsUrlSelected
        && (_services.GetService(typeof(WebView2AvailabilityService)) is WebView2AvailabilityService svc
            && svc.IsAvailable);

    private string? ResolveSelectedUrl()
    {
        if (SelectedRow is null) return null;

        if (SelectedRow.Kind == ItemKind.Text && !string.IsNullOrWhiteSpace(PreviewText))
        {
            var trimmed = PreviewText.Trim();
            if (trimmed.Length is > 0 and <= 4096
                && Uri.TryCreate(trimmed, UriKind.Absolute, out var parsed)
                && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps))
            {
                return parsed.AbsoluteUri;
            }
        }

        if (SelectedRow.Kind == ItemKind.Html && !string.IsNullOrWhiteSpace(PreviewHtml))
        {
            // Browsers wrap the copied selection in CF_HTML — between StartFragment /
            // EndFragment is a tiny anchor element whose href is the URL we want. Match the
            // first href and validate it as an absolute http(s) URI.
            var match = System.Text.RegularExpressions.Regex.Match(
                PreviewHtml,
                @"href\s*=\s*[""']([^""']+)[""']",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (match.Success
                && Uri.TryCreate(match.Groups[1].Value, UriKind.Absolute, out var parsed)
                && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps))
            {
                return parsed.AbsoluteUri;
            }
        }

        return null;
    }

    private void ApplyPreview(PreviewKind kind, string? text, byte[]? image, byte[]? rtf, string? html, string? meta)
    {
        PreviewKind = kind;
        PreviewText = text;
        PreviewImageBytes = image;
        PreviewRtfBytes = rtf;
        PreviewHtml = html;
        PreviewMeta = meta;
    }

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        // Skip payload decryption for the list view — only metadata + SearchText is needed for the
        // row preview. Payload (decrypted via DPAPI) is fetched on-demand via GetByIdAsync when the
        // user actually pastes / opens an item.
        var query = new ItemQuery(
            Limit: 500,
            Search: NormalizeSearch(SearchText),
            Kind: KindFilter,
            IncludePayload: false,
            Category: ActiveCategory);
        var previousId = SelectedRow?.Id;
        var loaded = await _items.ListAsync(query, cancellationToken).ConfigureAwait(false);
        Rows.Clear();
        for (var i = 0; i < loaded.Count; i++)
        {
            Rows.Add(new ItemRowViewModel(loaded[i], displayIndex: i));
        }
        // Preserve selection across reloads when the same id is still present.
        if (previousId is { } id) SelectedRow = Rows.FirstOrDefault(r => r.Id == id);
        SelectedRow ??= Rows.FirstOrDefault();
        NotifyCommandsCanExecuteChanged();
    }

    partial void OnSearchTextChanged(string value)
    {
        _ = RefreshAsync(CancellationToken.None);
    }

    [RelayCommand]
    private void MoveSelection(int delta)
    {
        if (Rows.Count == 0) return;
        var index = SelectedRow is null ? 0 : Rows.IndexOf(SelectedRow);
        index = Math.Clamp(index + delta, 0, Rows.Count - 1);
        SelectedRow = Rows[index];
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task TogglePinSelectedAsync()
    {
        if (SelectedRow is not { } row) return;
        var keepId = row.Id;
        await _items.SetPinnedAsync(row.Id, !row.Pinned, CancellationToken.None).ConfigureAwait(true);
        // ItemsChanged event will refresh; re-select the same id.
        await RefreshAsync(CancellationToken.None).ConfigureAwait(true);
        SelectedRow = Rows.FirstOrDefault(r => r.Id == keepId) ?? Rows.FirstOrDefault();
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task DeleteSelectedAsync()
    {
        if (SelectedRow is not { } row) return;
        var idx = Rows.IndexOf(row);
        await _items.SoftDeleteAsync(row.Id, CancellationToken.None).ConfigureAwait(true);
        await RefreshAsync(CancellationToken.None).ConfigureAwait(true);
        // Restore selection near the deleted row's position rather than jumping to top.
        if (Rows.Count > 0)
        {
            var newIdx = Math.Min(idx, Rows.Count - 1);
            if (newIdx >= 0) SelectedRow = Rows[newIdx];
        }
    }

    /// <summary>Raised after <see cref="PasteSelectedCommand"/> finishes (regardless of which
    /// surface invoked it — Enter, Ctrl+digits, toolbar button). The clipboard window listens
    /// for this so it can hide itself after a successful paste; subscribers must marshal to
    /// the UI thread themselves.</summary>
    public event EventHandler? PasteCompleted;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task PasteSelectedAsync()
    {
        if (SelectedRow is not { } row) return;
        var paster = _services.GetRequiredService<AutoPaster>();
        await paster.PasteAsync(row.Id, CancellationToken.None).ConfigureAwait(false);
        PasteCompleted?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand(CanExecute = nameof(IsImageSelected))]
    private async Task OpenInEditorAsync()
    {
        if (SelectedRow is null || SelectedRow.Kind != ItemKind.Image) return;
        var launcher = _services.GetRequiredService<EditorLauncher>();
        await launcher.OpenAsync(SelectedRow.Id, CancellationToken.None).ConfigureAwait(false);
    }

    [RelayCommand(CanExecute = nameof(IsUrlSelected))]
    private void OpenInBrowser()
    {
        var url = ResolveSelectedUrl();
        if (url is null) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch
        {
            // Browser launch failed (no default handler, sandboxed context, etc.) — silent
            // since the user can fall back to copy-paste; surfacing a dialog would be noise.
        }
    }

    /// <summary>Render the selected text item's URL through the headless WebView2 capture
    /// pipeline and feed the resulting PNG into the standard webpage-capture profile (save +
    /// history + clipboard + upload + toast). The CaptureWebpageTask short-circuits when it
    /// finds payload bytes already in the bag, so the prompt never shows — we set them via
    /// <see cref="ManualUploadService.IngestBytesAsync"/> before running the profile.</summary>
    [RelayCommand(CanExecute = nameof(CanCaptureWebpage))]
    private async Task CaptureWebpageAsync()
    {
        var url = ResolveSelectedUrl();
        if (url is null) return;
        var capture = _services.GetService<WebpageCaptureService>();
        var ingest = _services.GetService<ManualUploadService>();
        if (capture is null || ingest is null) return;
        try
        {
            var bytes = await capture.CaptureAsync(url, CancellationToken.None).ConfigureAwait(true);
            if (bytes is null || bytes.Length == 0) return;
            await ingest.IngestBytesAsync(
                bytes,
                "png",
                ItemKind.Image,
                $"Webpage {url}",
                ShareQ.Pipeline.Profiles.DefaultPipelineProfiles.WebpageCaptureId,
                CancellationToken.None).ConfigureAwait(true);
        }
        catch
        {
            // Capture failures already log inside WebpageCaptureService; the rest of the
            // pipeline (toast / upload errors) surfaces its own user feedback. Silent here so
            // a stray exception doesn't crash the popup.
        }
    }

    [RelayCommand(CanExecute = nameof(HasFileOnDisk))]
    private void OpenInExplorer()
    {
        var path = _selectedItemBlobRef;
        if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"/select,\"{path}\"",
            UseShellExecute = true
        });
    }

    [RelayCommand]
    private async Task ClearAllAsync()
    {
        // Scope the wipe to the active category — when the synthetic "All" tab is selected
        // (ActiveCategory == null) we fall through to the global wipe. Pinned items are
        // always preserved by the store.
        var scope = ActiveCategory ?? "all categories";
        var result = MessageBox.Show(
            $"Delete every non-pinned item from \"{scope}\"? Pinned items will be kept.",
            "Clear history",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel);
        if (result != MessageBoxResult.OK) return;
        await _items.ClearAllExceptPinnedAsync(ActiveCategory, CancellationToken.None).ConfigureAwait(false);
    }

    [RelayCommand]
    private void SetFilterAll() => KindFilter = null;
    [RelayCommand]
    private void SetFilterText() => KindFilter = ItemKind.Text;
    [RelayCommand]
    private void SetFilterImage() => KindFilter = ItemKind.Image;

    /// <summary>Click handler for a tab in the category strip — switches the active filter.
    /// Pass null/empty to select the synthetic "All" tab.</summary>
    [RelayCommand]
    private void SelectCategory(string? name)
        => ActiveCategory = string.IsNullOrEmpty(name) ? null : name;

    /// <summary>Move the selected item into the named category (right-click → Move to → …).</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task MoveSelectedToCategoryAsync(string? name)
    {
        if (SelectedRow is not { } row || string.IsNullOrEmpty(name)) return;
        await _items.SetCategoryAsync(row.Id, name, CancellationToken.None).ConfigureAwait(true);
        await RefreshAsync(CancellationToken.None).ConfigureAwait(true);
    }

    /// <summary>Copy the selected item into the named category — same payload, fresh row.
    /// The original stays where it is (unlike Move). Implemented by re-reading the source
    /// record (payload included) then inserting a NewItem under the target category.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task CopySelectedToCategoryAsync(string? name)
    {
        if (SelectedRow is not { } row || string.IsNullOrEmpty(name)) return;
        var record = await _items.GetByIdAsync(row.Id, CancellationToken.None).ConfigureAwait(true);
        if (record is null) return;
        var clone = new NewItem(
            Kind: record.Kind,
            Source: record.Source,
            CreatedAt: DateTimeOffset.UtcNow,
            Payload: record.Payload,
            PayloadSize: record.PayloadSize,
            Pinned: false,                               // a copy starts unpinned — the original keeps its pin
            SourceProcess: record.SourceProcess,
            SourceWindow: record.SourceWindow,
            BlobRef: record.BlobRef,
            UploadedUrl: record.UploadedUrl,
            UploaderId: record.UploaderId,
            SearchText: record.SearchText,
            Category: name);
        await _items.AddAsync(clone, CancellationToken.None).ConfigureAwait(true);
        // ItemsChanged event triggers RefreshAsync via the existing handler.
    }

    private bool HasSelection() => SelectedRow is not null;

    private static string? NormalizeSearch(string text)
    {
        text = text.Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }
}

public enum PreviewKind { None, Text, Html, Rtf, Image }

/// <summary>One entry in the popup's category tab strip. <see cref="Name"/> = null marks the
/// synthetic "All" tab that ignores the category filter; otherwise it's the category's
/// stored name. <see cref="DisplayName"/> is what the tab button shows (mirrors Name for
/// real categories, "All" for the synthetic one).</summary>
public sealed record CategoryTab(string? Name, string DisplayName, string? Icon, bool IsActive);
