using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using AresToys.App.Services;
using AresToys.Core.Domain;
using AresToys.Storage.Items;

namespace AresToys.App.ViewModels;

public sealed partial class PopupWindowViewModel : ObservableObject, IDisposable
{
    private readonly IItemStore _items;
    private readonly ICategoryStore _categories;
    private readonly IServiceProvider _services;
    private long _previewLoadToken;
    private string? _selectedItemBlobRef;
    /// <summary>Monotonic version of the underlying item store as observed by this VM.
    /// Bumped by <see cref="OnItemsChanged"/> whenever the store reports a mutation.
    /// <see cref="RefreshAsync"/> snapshots it after a successful load; <see cref="PrepareAsync"/>
    /// compares the snapshot against the current value and skips redundant refreshes.</summary>
    private long _itemsVersion;
    private long _lastRefreshedVersion = -1;

    public PopupWindowViewModel(IItemStore items, ICategoryStore categories, IServiceProvider services, ModuleSettings modules)
    {
        _items = items;
        _categories = categories;
        _services = services;
        IsKeySequencesEnabled = modules.KeySequencesEnabled;
        Rows = [];
        Categories = [];
        _items.ItemsChanged += OnItemsChanged;
        _categories.Changed += OnCategoriesChanged;
        _ = ReloadCategoriesAsync();
    }

    /// <summary>Mirror of <see cref="ModuleSettings.KeySequencesEnabled"/> captured at construction
    /// time. Bound by <c>ClipboardWindow</c>'s preview pane to gate the Trigger sequence editor —
    /// the field doesn't make sense when the module is disabled (no listener installed, so the
    /// trigger string would just sit in the DB doing nothing).</summary>
    public bool IsKeySequencesEnabled { get; }

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
            // The default seeded bucket is named "Clipboard" in the DB (Migration001) and that
            // identity is what the storage layer references. For UI purposes we swap in the
            // localised label; user-renamed or user-created categories keep their stored name
            // since those reflect the user's own choice.
            var display = string.Equals(c.Name, AresToys.Storage.Items.Category.Default, StringComparison.Ordinal)
                ? Resources.Strings.ResourceManager.GetString(
                      "Clipboard_DefaultCategory",
                      Markup.LocalizedStrings.Instance.Culture ?? System.Globalization.CultureInfo.CurrentUICulture) ?? c.Name
                : c.Name;
            var tab = new CategoryTab(c.Name, display, c.Icon, IsActive: c.Name == ActiveCategory);
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
        // Bump the version BEFORE marshalling — the dispatcher hop has measurable latency and
        // a window-open during the gap should still see "data has changed" via PrepareAsync.
        System.Threading.Interlocked.Increment(ref _itemsVersion);
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

    /// <summary>Image-row chip — when true, non-text rows are visible (images, files, videos);
    /// when false they're all hidden. Persisted to <c>clipboard.show_images</c>. Default true.
    /// Files / videos ride along with this chip rather than getting their own — they're rare
    /// in normal clipboard usage and a third chip would just add noise.</summary>
    [ObservableProperty] private bool _showImages = true;
    /// <summary>Text-row chip — when true, text-shaped rows are visible (Text / Html / Rtf);
    /// when false they're all hidden. Persisted to <c>clipboard.show_text</c>. Covers HTML
    /// too because clipboard HTML (e.g. an &lt;img&gt; URL copied from a browser) is the same
    /// "textual content" semantically — gating it under Image would surprise the user. Both
    /// chips off = empty list.</summary>
    [ObservableProperty] private bool _showText = true;

    /// <summary>"Sticky" / pinned mode — when on, the clipboard window stays open after a paste
    /// (single Enter / Ctrl+digit / double-click) so the user can paste several entries in a
    /// row without re-summoning the popup. Win+V (the toggle) and Esc still dismiss it; click-
    /// outside is already non-dismissing, so this flag only gates the paste-completed close.
    /// Persisted to <c>clipboard.pinned</c>.</summary>
    [ObservableProperty] private bool _isPinned;

    /// <summary>When true, a labelled row also shows its content snippet on a dimmer secondary
    /// line below the label — gives the user the option to see both at once instead of having
    /// to select the row to read the body. Default false (label-only matches CopyQ). Persisted
    /// to <c>clipboard.show_snippet_with_label</c>.</summary>
    [ObservableProperty] private bool _showSnippetWithLabel;

    private bool _typeFiltersLoaded;
    private const string ShowImagesKey = "clipboard.show_images";
    private const string ShowTextKey = "clipboard.show_text";
    private const string PinnedKey = "clipboard.pinned";
    private const string ShowSnippetWithLabelKey = "clipboard.show_snippet_with_label";

    partial void OnShowImagesChanged(bool value)
    {
        PersistTypeFilter(ShowImagesKey, value);
        // Chip toggle is a filter applied INSIDE RefreshAsync — without this kick the visible
        // list wouldn't update until something else (search, category) triggered a refresh.
        if (_typeFiltersLoaded) _ = RefreshAsync(CancellationToken.None);
    }
    partial void OnShowTextChanged(bool value)
    {
        PersistTypeFilter(ShowTextKey, value);
        if (_typeFiltersLoaded) _ = RefreshAsync(CancellationToken.None);
    }
    partial void OnIsPinnedChanged(bool value) => PersistFlag(PinnedKey, value);

    partial void OnShowSnippetWithLabelChanged(bool value)
    {
        PersistFlag(ShowSnippetWithLabelKey, value);
        // Push the new pref into every existing row VM so the secondary line appears /
        // disappears immediately — without this kick the toggle wouldn't reflect until a
        // RefreshAsync rebuilt the collection (search edit, category change, etc.).
        foreach (var row in Rows) row.ShowSnippetWithLabel = value;
    }

    private void PersistFlag(string key, bool value)
    {
        if (!_typeFiltersLoaded) return;
        var settings = _services.GetService<AresToys.Storage.Settings.ISettingsStore>();
        if (settings is null) return;
        _ = settings.SetAsync(key, value ? "1" : "0", sensitive: false, CancellationToken.None);
    }

    private void PersistTypeFilter(string key, bool value)
    {
        if (!_typeFiltersLoaded) return;
        var settings = _services.GetService<AresToys.Storage.Settings.ISettingsStore>();
        if (settings is null) return;
        _ = settings.SetAsync(key, value ? "1" : "0", sensitive: false, CancellationToken.None);
        _ = RefreshAsync(CancellationToken.None);
    }

    /// <summary>Pull the persisted chip state once at window-show time. Called from the view's
    /// IsVisibleChanged hook — the VM constructor runs in DI before any settings can be read
    /// reliably, so we defer until the popup actually opens.</summary>
    public async Task LoadTypeFiltersAsync(CancellationToken cancellationToken)
    {
        if (_typeFiltersLoaded) return;
        var settings = _services.GetService<AresToys.Storage.Settings.ISettingsStore>();
        if (settings is null) { _typeFiltersLoaded = true; return; }
        var rawImg = await settings.GetAsync(ShowImagesKey, cancellationToken).ConfigureAwait(true);
        var rawText = await settings.GetAsync(ShowTextKey, cancellationToken).ConfigureAwait(true);
        var rawPinned = await settings.GetAsync(PinnedKey, cancellationToken).ConfigureAwait(true);
        var rawSnippet = await settings.GetAsync(ShowSnippetWithLabelKey, cancellationToken).ConfigureAwait(true);
        // Filter chips default true (fresh DB shows everything); pinned defaults false (the
        // popup behaves as before until the user opts in). show-snippet-with-label defaults
        // false — matches CopyQ where a "Notes"-labeled item shows only the label.
        ShowImages = rawImg != "0";
        ShowText = rawText != "0";
        IsPinned = rawPinned == "1";
        ShowSnippetWithLabel = rawSnippet == "1";
        _typeFiltersLoaded = true;
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
    public bool IsVideoPreview => PreviewKind == PreviewKind.Video;
    public bool HasPreview => PreviewKind != PreviewKind.None;

    /// <summary>Disk path passed to the <c>MediaElement</c> in the preview pane when the
    /// selected item is a video / animated GIF and we have a file on disk to play. Empty / null
    /// when the preview isn't a video — the MediaElement's Source binding goes to null and the
    /// control unloads itself.</summary>
    [ObservableProperty] private string? _previewVideoPath;

    /// <summary>True when WPF's <c>MediaElement</c> failed to decode the current
    /// <see cref="PreviewVideoPath"/>. Drives the bottom-bar swap in ClipboardWindow: the normal
    /// Play/Pause + seek + timecode strip hides, replaced by a "Preview unavailable" warning +
    /// <em>Open with default viewer</em> button. Most commonly fires on WebM / VP9 / VP8 (no
    /// codec in WMF without Web Media Extensions); also on Win10/11 N or KN editions without the
    /// Media Feature Pack. Reset to false when PreviewVideoPath changes (new selection).</summary>
    [ObservableProperty] private bool _isVideoPreviewFailed;

    partial void OnPreviewVideoPathChanged(string? value) => IsVideoPreviewFailed = false;

    partial void OnPreviewKindChanged(PreviewKind value)
    {
        OnPropertyChanged(nameof(IsTextPreview));
        OnPropertyChanged(nameof(IsHtmlPreview));
        OnPropertyChanged(nameof(IsRtfPreview));
        OnPropertyChanged(nameof(IsImagePreview));
        OnPropertyChanged(nameof(IsVideoPreview));
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
            case ItemKind.Video:
                // Recording coordinator writes mp4 + gif both as ItemKind.Video. When the file is
                // still on disk (BlobRef), route to the MediaElement-backed video preview so the
                // user can hit Play/Pause + Loop. Falls back to a static ffmpeg-generated thumb
                // if the file moved/got deleted, or to no preview at all if ffmpeg isn't around.
                if (!string.IsNullOrEmpty(record.BlobRef) && System.IO.File.Exists(record.BlobRef))
                {
                    ApplyVideoPreview(record.BlobRef, meta + " · " + System.IO.Path.GetFileName(record.BlobRef));
                    break;
                }
                // No live file: try a still thumbnail via ffmpeg (might have been cached at a
                // previous render). If even that fails — no preview.
                if (!string.IsNullOrEmpty(record.BlobRef))
                {
                    var thumbSvc = _services.GetService(typeof(AresToys.App.Services.Recording.VideoThumbnailService))
                        as AresToys.App.Services.Recording.VideoThumbnailService;
                    if (thumbSvc is not null)
                    {
                        var thumb = await thumbSvc.GenerateAsync(record.Id, record.BlobRef, CancellationToken.None).ConfigureAwait(true);
                        if (token != System.Threading.Interlocked.Read(ref _previewLoadToken)) return;
                        if (thumb is { Length: > 0 })
                        {
                            ApplyPreview(PreviewKind.Image, null, thumb, null, null, meta + " · " + System.IO.Path.GetFileName(record.BlobRef));
                            break;
                        }
                    }
                }
                ApplyPreview(PreviewKind.None, null, null, null, null, meta);
                break;
            case ItemKind.Files:
                // Files clipboard items store the path list as newline-joined UTF-8 (see
                // ClipboardIngestionService.MapToNewItem). When the (first) path points at an
                // image file we have on disk, render the image in the preview pane — matches the
                // expectation of "I just copied a photo from Explorer, show me what it is."
                // Otherwise keep the legacy text preview of the path(s) so the user can copy /
                // read them.
                var pathsText = Encoding.UTF8.GetString(payload.Span);
                var firstPath = pathsText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
                if (firstPath is not null && System.IO.File.Exists(firstPath))
                {
                    // Video / animated GIF → MediaElement (play/pause controls in the preview pane).
                    if (IsVideoPath(firstPath))
                    {
                        ApplyVideoPreview(firstPath, meta + " · " + System.IO.Path.GetFileName(firstPath));
                        break;
                    }
                    // Still image → read bytes into the image preview (bounded by size cap so a
                    // multi-hundred-MB raw doesn't freeze the UI).
                    if (IsImagePath(firstPath))
                    {
                        var info = new System.IO.FileInfo(firstPath);
                        if (info.Length <= MaxFilePreviewBytes)
                        {
                            try
                            {
                                var imageBytes = await System.IO.File.ReadAllBytesAsync(firstPath, CancellationToken.None).ConfigureAwait(true);
                                if (token != System.Threading.Interlocked.Read(ref _previewLoadToken)) return;
                                ApplyPreview(PreviewKind.Image, null, imageBytes, null, null, meta + " · " + System.IO.Path.GetFileName(firstPath));
                                break;
                            }
                            catch { /* fall through to text preview on any IO failure */ }
                        }
                    }
                }
                ApplyPreview(PreviewKind.Text, pathsText, null, null, null, meta);
                break;
            default:
                ApplyPreview(PreviewKind.None, null, null, null, null, meta);
                break;
        }
        NotifyCommandsCanExecuteChanged();
    }

    /// <summary>Soft cap on the file size the Files-preview path will try to load into the
    /// preview pane. Above this we skip the image render and fall back to the text path list —
    /// avoids hanging the UI on a 100MB raw camera shot the user happened to copy.</summary>
    private const long MaxFilePreviewBytes = 20 * 1024 * 1024;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".webp", ".tif", ".tiff", ".ico",
    };

    /// <summary>Extensions we route to the video preview channel. Includes everything ffmpeg can
    /// generate a still-frame thumbnail for — even containers WPF's <c>MediaElement</c> (Windows
    /// Media Foundation) can't actually play back. On a playback failure the
    /// <c>OnPreviewVideoFailed</c> handler automatically swaps in the ffmpeg thumbnail via the
    /// still-image channel so the user still sees the first frame; tags like .webm, .mkv, .flv
    /// land there on standard Windows installs without the optional codec packs.</summary>
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mov", ".gif", ".apng",
        ".webm", ".mkv", ".avi", ".wmv", ".flv",
        ".mpg", ".mpeg", ".m2ts", ".mts", ".vob",
        ".3gp", ".3g2", ".ogv", ".rm", ".rmvb",
        // .ts deliberately NOT here: the extension collides with TypeScript source code
        // (and Qt translation source). MPEG-2 Transport Stream captures usually arrive as
        // .m2ts (Blu-ray) or .mts (AVCHD camcorder) which are unambiguous.
    };

    private static bool IsImagePath(string path)
    {
        var ext = System.IO.Path.GetExtension(path);
        return !string.IsNullOrEmpty(ext) && ImageExtensions.Contains(ext);
    }

    private static bool IsVideoPath(string path)
    {
        var ext = System.IO.Path.GetExtension(path);
        return !string.IsNullOrEmpty(ext) && VideoExtensions.Contains(ext);
    }

    private void NotifyCommandsCanExecuteChanged()
    {
        TogglePinSelectedCommand.NotifyCanExecuteChanged();
        DeleteSelectedCommand.NotifyCanExecuteChanged();
        PasteSelectedCommand.NotifyCanExecuteChanged();
        OpenInEditorCommand.NotifyCanExecuteChanged();
        OpenInExternalEditorCommand.NotifyCanExecuteChanged();
        OpenInExplorerCommand.NotifyCanExecuteChanged();
        CopyPathToClipboardCommand.NotifyCanExecuteChanged();
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

    /// <summary>True when the selected item has a real file currently on disk (BlobRef
    /// populated by a SaveToFile pipeline step AND the file still exists). Gates the "Show in
    /// explorer" + "Copy path to clipboard" affordances. The File.Exists check is sync I/O
    /// but only runs once per selection change (the property is re-evaluated via
    /// OnPropertyChanged(nameof(HasFileOnDisk)) when SelectedRow flips), so the cost is a
    /// single stat() per row click — negligible for the user's clicking cadence. Without the
    /// existence check we'd offer "Copy path" for clipboard-only images whose BlobRef points
    /// to a transient path that was never written, or to a file that's since been deleted.</summary>
    public bool HasFileOnDisk =>
        !string.IsNullOrEmpty(_selectedItemBlobRef) && System.IO.File.Exists(_selectedItemBlobRef);

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
        // Whenever the preview switches to a non-video kind, blank the video path so the
        // MediaElement releases its file handle + stops any in-flight playback.
        if (kind != PreviewKind.Video) PreviewVideoPath = null;
    }

    /// <summary>Switch the preview pane to the video / animated-GIF playback channel pointing at
    /// the file on disk. The MediaElement bound to <see cref="PreviewVideoPath"/> handles loading
    /// + auto-loop; the code-behind wires the Play/Pause button.</summary>
    private void ApplyVideoPreview(string path, string? meta)
    {
        PreviewText = null;
        PreviewImageBytes = null;
        PreviewRtfBytes = null;
        PreviewHtml = null;
        PreviewMeta = meta;
        PreviewVideoPath = path;
        PreviewKind = PreviewKind.Video;
    }

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        // Snapshot the version BEFORE the load — if a save races us we'll miss the bump on
        // this pass but PrepareAsync (or the next OnItemsChanged) picks it up. Reading after
        // would let a concurrent change look already-applied to us.
        var versionAtStart = System.Threading.Interlocked.Read(ref _itemsVersion);
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
        // Apply the type-chip filter after the query. ShowImages gates Image / Files / Video
        // (anything visual or binary); ShowText gates Text / Html / Rtf. Both off = empty list,
        // which is what the user expects ("hide everything"). Doing it post-query keeps
        // ItemQuery as a single-Kind filter; the Limit=500 ceiling we already work under makes
        // a SQL-level Kinds list unnecessary for v0.1.0.
        var displayIndex = 0;
        Rows.Clear();
        for (var i = 0; i < loaded.Count; i++)
        {
            var record = loaded[i];
            var isImageLike = record.Kind is ItemKind.Image or ItemKind.Files or ItemKind.Video;
            var isTextLike = record.Kind is ItemKind.Text or ItemKind.Html or ItemKind.Rtf;
            if (isImageLike && !ShowImages) continue;
            if (isTextLike && !ShowText) continue;
            Rows.Add(new ItemRowViewModel(record, displayIndex: displayIndex++, showSnippetWithLabel: ShowSnippetWithLabel));
        }
        // Preserve selection across reloads when the same id is still present.
        if (previousId is { } id) SelectedRow = Rows.FirstOrDefault(r => r.Id == id);
        SelectedRow ??= Rows.FirstOrDefault();
        NotifyCommandsCanExecuteChanged();
        _lastRefreshedVersion = versionAtStart;
    }

    /// <summary>Hosts (OpenClipboardWindowTask, future tray entry-points) await this BEFORE
    /// <see cref="System.Windows.Window.Show"/> so the row list is already populated when the
    /// window paints — no Clear+Refill flash. Skips the SQLite query entirely when the store
    /// hasn't bumped its version since the last refresh (the common case: VM is singleton and
    /// stays subscribed to <c>ItemsChanged</c> while the window is hidden, so <see cref="Rows"/>
    /// is already current). Also runs the one-shot type-filter hydration on first call.</summary>
    public async Task PrepareAsync(CancellationToken cancellationToken)
    {
        await LoadTypeFiltersAsync(cancellationToken).ConfigureAwait(true);
        var current = System.Threading.Interlocked.Read(ref _itemsVersion);
        if (current != _lastRefreshedVersion)
        {
            await RefreshAsync(cancellationToken).ConfigureAwait(true);
        }
    }

    private System.Windows.Threading.DispatcherTimer? _searchDebounce;

    partial void OnSearchTextChanged(string value)
    {
        // Debounce per-keystroke search to avoid spamming the SQLite query (and Rows.Clear+
        // refill) per character. 200ms feels instant for typing but coalesces a burst into a
        // single refresh. DispatcherTimer is fine here — the VM is constructed on the UI
        // thread and OnSearchTextChanged fires from WPF data binding (also UI thread).
        if (_searchDebounce is null)
        {
            _searchDebounce = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(200)
            };
            _searchDebounce.Tick += (_, _) =>
            {
                _searchDebounce!.Stop();
                _ = RefreshAsync(CancellationToken.None);
            };
        }
        _searchDebounce.Stop();
        _searchDebounce.Start();
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

    /// <summary>Shift+Enter / explicit "paste path" variant: paste the on-disk file path of the
    /// selected item as plain text instead of the file itself. Used when the user wants the path
    /// (e.g. into a code editor / terminal) rather than the file contents. Falls back to the
    /// regular paste behaviour silently when no path is available (Text items, deleted source).</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task PasteSelectedAsPathAsync()
    {
        if (SelectedRow is not { } row) return;
        var paster = _services.GetRequiredService<AutoPaster>();
        await paster.PastePathAsTextAsync(row.Id, CancellationToken.None).ConfigureAwait(false);
        PasteCompleted?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand(CanExecute = nameof(IsImageSelected))]
    private async Task OpenInEditorAsync()
    {
        if (SelectedRow is null || SelectedRow.Kind != ItemKind.Image) return;
        var launcher = _services.GetRequiredService<EditorLauncher>();
        await launcher.OpenAsync(SelectedRow.Id, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>Open a text-shaped row in the external editor (VSCode / Notepad / whatever
    /// the user picked in Settings → Capture → External editor command, or Windows default
    /// for .txt when the setting is empty). Edits sync back into the SQLite store via the
    /// service's FileSystemWatcher — the popup list refreshes automatically through
    /// ItemsChanged. Image rows have their own in-app editor (<see cref="OpenInEditorAsync"/>);
    /// this is the text-row counterpart.</summary>
    [RelayCommand(CanExecute = nameof(IsTextSelected))]
    private async Task OpenInExternalEditorAsync()
    {
        if (SelectedRow is null) return;
        var editor = _services.GetService<ExternalTextEditorService>();
        if (editor is null) return;
        await editor.EditAsync(SelectedRow.Id, CancellationToken.None).ConfigureAwait(false);
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
                AresToys.Pipeline.Profiles.DefaultPipelineProfiles.WebpageCaptureId,
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

    /// <summary>Copy the on-disk file path of the selected item to the clipboard. Visible only
    /// when the row has a populated BlobRef (image / video / file with SaveToFile applied).
    /// Doesn't validate File.Exists at copy time — copying a stale path is acceptable behavior;
    /// the user can paste it into Explorer's address bar to see "the location where this used
    /// to live" without us second-guessing them.</summary>
    [RelayCommand(CanExecute = nameof(HasFileOnDisk))]
    private void CopyPathToClipboard()
    {
        var path = _selectedItemBlobRef;
        if (string.IsNullOrEmpty(path)) return;
        try { System.Windows.Clipboard.SetText(path); }
        catch { /* clipboard locked by another app — best-effort, no toast for an internal op */ }
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

    /// <summary>Persist an updated label for a specific row. Called by both the inline-rename
    /// commit path (right-click → Rename / F2 → Enter) and the preview pane's label TextBox
    /// (LostFocus / Enter). Pushes through <see cref="IItemStore.SetLabelAsync"/> which
    /// normalises empty / oversized input at the storage boundary and fires <c>ItemsChanged</c>;
    /// the standard subscriber rebuilds the affected row in the bound collection.</summary>
    public async Task CommitLabelAsync(long itemId, string? newLabel)
    {
        await _items.SetLabelAsync(itemId, newLabel, CancellationToken.None).ConfigureAwait(true);
        // ItemsChanged → OnItemsChanged → RefreshAsync rebuilds the row VM with the new label,
        // so no in-place mutation is needed here.
    }

    /// <summary>Persist an updated trigger sequence for the Key Sequences module. Empty / whitespace
    /// clears the trigger. The storage layer raises <c>ItemsChanged</c> which the
    /// <c>ClipboardSequenceProvider</c> observes to rebuild the matcher index.</summary>
    public async Task CommitTriggerAsync(long itemId, string? newTrigger)
    {
        await _items.SetTriggerAsync(itemId, newTrigger, CancellationToken.None).ConfigureAwait(true);
    }

    /// <summary>Move a specific item (by id) into the named category. Called by the
    /// drag-to-category-tab handler on <see cref="Views.ClipboardWindow"/>; the right-click
    /// "Move to" menu uses <see cref="MoveSelectedToCategoryAsync"/> which operates on
    /// <see cref="SelectedRow"/>. Same underlying SQL UPDATE → <c>ItemsChanged</c> path; the
    /// event handler rebuilds the visible rows.</summary>
    public async Task MoveItemToCategoryAsync(long itemId, string category)
    {
        if (string.IsNullOrEmpty(category)) return;
        await _items.SetCategoryAsync(itemId, category, CancellationToken.None).ConfigureAwait(true);
    }

    /// <summary>Single-step chevron move on the pinned strip — swap the item with its
    /// neighbour in the indicated direction (-1 = up, +1 = down). No-op when there's no
    /// neighbour (top / bottom of the strip) or when the item isn't pinned. Snapshot the
    /// current pinned order from <see cref="Rows"/>, apply the swap, persist the whole
    /// sequence with <see cref="IItemStore.ReorderPinnedAsync"/>.</summary>
    public async Task MovePinnedAsync(long itemId, int direction)
    {
        if (direction is not (-1 or 1)) return;
        var pinnedIds = Rows.Where(r => r.Pinned).Select(r => r.Id).ToList();
        var idx = pinnedIds.IndexOf(itemId);
        if (idx < 0) return;
        var target = idx + direction;
        if (target < 0 || target >= pinnedIds.Count) return;
        (pinnedIds[idx], pinnedIds[target]) = (pinnedIds[target], pinnedIds[idx]);
        await _items.ReorderPinnedAsync(pinnedIds, CancellationToken.None).ConfigureAwait(true);
    }

    /// <summary>Drag-drop reorder on the pinned strip — move the source item to the
    /// position currently held by the target (insert-before semantics). When source and
    /// target are the same or both unpinned, no-op. Only operates within the pinned strip;
    /// dropping an unpinned row on a pinned one is ignored upstream by the handler.</summary>
    public async Task ReorderPinnedAsync(long sourceId, long targetId)
    {
        if (sourceId == targetId) return;
        var pinnedIds = Rows.Where(r => r.Pinned).Select(r => r.Id).ToList();
        var sourceIdx = pinnedIds.IndexOf(sourceId);
        var targetIdx = pinnedIds.IndexOf(targetId);
        if (sourceIdx < 0 || targetIdx < 0) return;
        pinnedIds.RemoveAt(sourceIdx);
        // After the remove the target index shifts down by 1 when source was earlier in the
        // list. Otherwise it stays put. Either way, sourceId lands AT the slot the target
        // visually occupies before the drop (insert-before semantics).
        var insertAt = sourceIdx < targetIdx ? targetIdx - 1 : targetIdx;
        pinnedIds.Insert(insertAt, sourceId);
        await _items.ReorderPinnedAsync(pinnedIds, CancellationToken.None).ConfigureAwait(true);
    }

    private bool HasSelection() => SelectedRow is not null;

    private static string? NormalizeSearch(string text)
    {
        text = text.Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }
}

public enum PreviewKind { None, Text, Html, Rtf, Image, Video }

/// <summary>One entry in the popup's category tab strip. <see cref="Name"/> = null marks the
/// synthetic "All" tab that ignores the category filter; otherwise it's the category's
/// stored name. <see cref="DisplayName"/> is what the tab button shows (mirrors Name for
/// real categories, "All" for the synthetic one).</summary>
public sealed record CategoryTab(string? Name, string DisplayName, string? Icon, bool IsActive);
