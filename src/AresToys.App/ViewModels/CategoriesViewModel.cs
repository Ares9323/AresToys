using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AresToys.Storage.Items;

namespace AresToys.App.ViewModels;

/// <summary>Settings → Categories &amp; tags tab. Lists every clipboard category with its name,
/// icon and retention caps; lets the user add, rename, remove, and reorder. The default
/// <see cref="Category.Default"/> bucket is read-only — it cannot be renamed or deleted (its
/// items would have nowhere to go). The same page hosts the tag editor (issue #4): create,
/// rename, recolour, delete; the dialogs live in MainWindow's code-behind.</summary>
public sealed partial class CategoriesViewModel : ObservableObject, IDisposable
{
    private readonly ICategoryStore _store;
    private readonly ITagStore _tags;
    private readonly IItemStore _items;

    public CategoriesViewModel(ICategoryStore store, ITagStore tags, IItemStore items)
    {
        _store = store;
        _tags = tags;
        _items = items;
        Categories = [];
        _store.Changed += OnStoreChanged;
        _tags.Changed += OnTagsChanged;
        _items.ItemsChanged += OnItemsChanged;
        _ = ReloadAsync();
        _ = ReloadTagsAsync();
    }

    public void Dispose()
    {
        _store.Changed -= OnStoreChanged;
        _tags.Changed -= OnTagsChanged;
        _items.ItemsChanged -= OnItemsChanged;
    }

    // ── Tags ────────────────────────────────────────────────────────────────────────

    /// <summary>Every tag ordered by name, with its item count.</summary>
    public ObservableCollection<TagBadge> Tags { get; } = [];

    public bool HasNoTags => Tags.Count == 0;

    private void OnTagsChanged(object? sender, EventArgs e)
        => Application.Current?.Dispatcher.InvokeAsync(() => _ = ReloadTagsAsync());

    /// <summary>Item counts move when items gain / lose tags or get deleted / restored. A fresh
    /// capture (Added) never carries tags, so skip the reload on the hot clipboard path.</summary>
    private void OnItemsChanged(object? sender, ItemsChangedEventArgs e)
    {
        if (e.Kind == ItemsChangeKind.Added) return;
        Application.Current?.Dispatcher.InvokeAsync(() => _ = ReloadTagsAsync());
    }

    public async Task ReloadTagsAsync()
    {
        var list = await _tags.ListAsync(CancellationToken.None).ConfigureAwait(true);
        Tags.Clear();
        foreach (var t in list) Tags.Add(new TagBadge(t));
        OnPropertyChanged(nameof(HasNoTags));
    }

    /// <summary>Name clash check for the edit dialog: true when a tag other than
    /// <paramref name="exceptId"/> already uses the name (case-insensitive).</summary>
    public bool IsTagNameTaken(string name, long? exceptId)
        => Tags.Any(t => t.Id != exceptId && string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

    public Task CreateTagAsync(string name, string? color)
        => _tags.GetOrCreateAsync(name, color, CancellationToken.None);

    public Task UpdateTagAsync(long id, string name, string? color)
        => _tags.UpdateAsync(id, name, color, CancellationToken.None);

    public Task DeleteTagAsync(long id)
        => _tags.DeleteAsync(id, CancellationToken.None);

    public ObservableCollection<CategoryRowViewModel> Categories { get; }

    [ObservableProperty]
    private string _newCategoryName = string.Empty;

    /// <summary>FontAwesome 'star' (). Used as the default icon when the user adds a
    /// category without explicitly opening the picker — guarantees every row in the list has a
    /// glyph instead of a blank cell, so the popup tab strip never shows a nameless box. The
    /// user can still change it via the Pick button before clicking Add.</summary>
    public const string DefaultIconGlyph = "";

    [ObservableProperty]
    private string _newCategoryIcon = DefaultIconGlyph;

    private void OnStoreChanged(object? sender, EventArgs e)
        => Application.Current?.Dispatcher.InvokeAsync(() => _ = ReloadAsync());

    public async Task ReloadAsync()
    {
        var list = await _store.ListAsync(CancellationToken.None).ConfigureAwait(true);
        Categories.Clear();
        foreach (var c in list)
        {
            var isDefault = string.Equals(c.Name, Category.Default, StringComparison.Ordinal);
            Categories.Add(new CategoryRowViewModel(c, isDefault, this));
        }
    }

    [RelayCommand]
    private async Task AddCategoryAsync()
    {
        var name = NewCategoryName.Trim();
        if (string.IsNullOrEmpty(name)) return;
        // Skip duplicates: storing two categories with the same name violates the PK and the
        // user would just get a noisy SQL exception. Silent no-op is friendlier.
        if (Categories.Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))) return;

        var sortOrder = Categories.Count;   // append at the end
        var icon = string.IsNullOrWhiteSpace(NewCategoryIcon) ? null : NewCategoryIcon.Trim();
        await _store.AddAsync(new Category(name, icon, sortOrder), CancellationToken.None).ConfigureAwait(true);
        NewCategoryName = string.Empty;
        NewCategoryIcon = DefaultIconGlyph;
    }

    public Task RenameAsync(string oldName, string newName)
        => _store.RenameAsync(oldName, newName, CancellationToken.None);

    public Task DeleteAsync(string name)
        => _store.DeleteAsync(name, CancellationToken.None);

    public Task UpdateAsync(Category category)
        => _store.UpdateAsync(category, CancellationToken.None);
}

public sealed partial class CategoryRowViewModel : ObservableObject
{
    private readonly CategoriesViewModel _owner;
    private readonly Category _original;

    public CategoryRowViewModel(Category category, bool isDefault, CategoriesViewModel owner)
    {
        _original = category;
        _owner = owner;
        IsDefault = isDefault;
        // The default ("Clipboard") row is read-only — we can safely show a localised label in
        // its Name field without ever writing it back to the DB. CanModify=false keeps the
        // TextBox disabled and the rename code-path is gated on the same flag, so the original
        // raw Name stays the storage identity.
        _name = isDefault
            ? Resources.Strings.ResourceManager.GetString(
                  "Clipboard_DefaultCategory",
                  Markup.LocalizedStrings.Instance.Culture ?? System.Globalization.CultureInfo.CurrentUICulture)
              ?? category.Name
            : category.Name;
        _icon = category.Icon ?? string.Empty;
        _maxItems = category.MaxItems;
        _autoCleanupAfter = category.AutoCleanupAfter;
    }

    public bool IsDefault { get; }
    /// <summary>Default ('Clipboard') can't be renamed or deleted — would orphan existing items
    /// and leave the system without a fallback bucket. Used by Name TextBox + Delete button.</summary>
    public bool CanModify => !IsDefault;
    /// <summary>Retention caps (MaxItems / AutoCleanupAfter) and the icon are valid even on the
    /// default row — there's no reason the user shouldn't tune how aggressively their main
    /// bucket trims itself. Used by the icon picker, NumberBoxes and the autosave hooks.</summary>
    public bool CanConfigure => true;

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private string _icon;

    [ObservableProperty]
    private int _maxItems;

    [ObservableProperty]
    private int _autoCleanupAfter;

    /// <summary>Auto-save trigger for icon changes — the picker dialog returns synchronously, so
    /// the user's expectation is "I picked it, it's saved". Re-uses the existing SaveAsync
    /// pipeline (with rename support) so behaviour stays identical to the manual Save button.
    /// Skipped on the default Clipboard row since it can't be modified.</summary>
    partial void OnIconChanged(string value)
    {
        if (!CanConfigure) return;
        _ = SaveAsync();
    }

    /// <summary>Commit the row to storage. Public so the XAML LostFocus / Enter handlers can
    /// trigger a save without waiting for the explicit Save button — the bound TextBox uses
    /// UpdateSourceTrigger=LostFocus so by the time we get called the property has already
    /// been pushed back from the editor. Two-step when renaming: rename first (re-routes
    /// items at the same time), then update icon/caps separately since RenameAsync only
    /// touches the name.</summary>
    [RelayCommand(CanExecute = nameof(CanConfigure))]
    public async Task SaveAsync()
    {
        // Rename only when allowed (custom rows). Default rows skip this branch entirely so
        // the row keeps its 'Clipboard' identity even if the user somehow tampered with Name.
        if (CanModify && !string.Equals(Name, _original.Name, StringComparison.Ordinal))
        {
            await _owner.RenameAsync(_original.Name, Name).ConfigureAwait(true);
        }
        var updated = new Category(Name, string.IsNullOrWhiteSpace(Icon) ? null : Icon.Trim(),
            _original.SortOrder, MaxItems, AutoCleanupAfter);
        await _owner.UpdateAsync(updated).ConfigureAwait(true);
    }

    /// <summary>Reset the row's user-tunable fields (icon + retention caps) to their seed
    /// defaults. Available on the default 'Clipboard' row in place of Delete (which would
    /// leave no fallback bucket). Default icon is the FontAwesome 'list' glyph () —
    /// the same value the migration seeds on a fresh install.</summary>
    [RelayCommand]
    private async Task ResetToDefaultsAsync()
    {
        Icon = "";
        MaxItems = 0;
        AutoCleanupAfter = 0;
        await SaveAsync().ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanModify))]
    private async Task DeleteAsync()
    {
        var ok = MessageBox.Show(
            $"Delete category '{_original.Name}'?\n\nItems in this category will be moved to '{Category.Default}'. Pinned items keep their pin.",
            "Delete category",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel);
        if (ok != MessageBoxResult.OK) return;
        await _owner.DeleteAsync(_original.Name).ConfigureAwait(true);
    }
}
