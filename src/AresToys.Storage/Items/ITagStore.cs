namespace AresToys.Storage.Items;

/// <summary>CRUD for tag definitions (issue #4). Assigning tags to items lives on
/// <see cref="IItemStore.AddTagAsync"/> / <see cref="IItemStore.RemoveTagAsync"/> so item
/// mutations keep raising <see cref="IItemStore.ItemsChanged"/>; a future automatic tagger
/// (issue #19) only needs <see cref="GetOrCreateAsync"/> plus <see cref="IItemStore.AddTagAsync"/>.</summary>
public interface ITagStore
{
    /// <summary>Every tag ordered by name (case-insensitive), with its live item count.</summary>
    Task<IReadOnlyList<Tag>> ListAsync(CancellationToken cancellationToken);

    Task<Tag?> GetAsync(long id, CancellationToken cancellationToken);

    /// <summary>Case-insensitive lookup on the normalised name.</summary>
    Task<Tag?> GetByNameAsync(string name, CancellationToken cancellationToken);

    /// <summary>Return the tag with this name (case-insensitive), creating it when missing.
    /// <paramref name="color"/> only applies to a newly created tag: an existing tag keeps
    /// its colour. Throws <see cref="ArgumentException"/> when the name is empty after
    /// normalisation (<see cref="TagRules.NormalizeName"/>).</summary>
    Task<Tag> GetOrCreateAsync(string name, string? color, CancellationToken cancellationToken);

    /// <summary>Rename and / or recolour a tag. Every item carrying it is re-indexed for search
    /// when the name changes. Returns false when the tag doesn't exist; throws
    /// <see cref="InvalidOperationException"/> when another tag already uses the name.</summary>
    Task<bool> UpdateAsync(long id, string name, string? color, CancellationToken cancellationToken);

    /// <summary>Delete a tag and remove it from every item (the join rows cascade). Returns
    /// false when the tag doesn't exist.</summary>
    Task<bool> DeleteAsync(long id, CancellationToken cancellationToken);

    /// <summary>Raised after any definition mutation (create / update / delete). Item-level
    /// assignments go through <see cref="IItemStore.ItemsChanged"/> instead. Subscribers reload
    /// from <see cref="ListAsync"/> and must marshal to the UI thread themselves.</summary>
    event EventHandler? Changed;
}
