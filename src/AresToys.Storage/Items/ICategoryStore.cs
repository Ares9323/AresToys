namespace AresToys.Storage.Items;

public interface ICategoryStore
{
    Task<IReadOnlyList<Category>> ListAsync(CancellationToken cancellationToken);
    Task<Category?> GetAsync(string name, CancellationToken cancellationToken);

    /// <summary>Insert a new category. Throws if the name already exists.</summary>
    Task AddAsync(Category category, CancellationToken cancellationToken);

    Task UpdateAsync(Category category, CancellationToken cancellationToken);

    /// <summary>Rename a category and migrate every item that referenced the old name to the
    /// new one in a single transaction. Refused for the default <see cref="Category.Default"/>
    /// bucket.</summary>
    Task RenameAsync(string oldName, string newName, CancellationToken cancellationToken);

    /// <summary>Delete a category and re-route every item it owned back to
    /// <see cref="Category.Default"/>. Refused for the default itself — there's nowhere safe
    /// to dump its items.</summary>
    Task DeleteAsync(string name, CancellationToken cancellationToken);

    /// <summary>Persist a new sort order. Pass the names in display order.</summary>
    Task ReorderAsync(IReadOnlyList<string> orderedNames, CancellationToken cancellationToken);

    /// <summary>Raised after any mutation. Subscribers (the popup tab bar, the settings list)
    /// reload from <see cref="ListAsync"/> to refresh.</summary>
    event EventHandler? Changed;
}
