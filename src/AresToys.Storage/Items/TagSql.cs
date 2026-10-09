namespace AresToys.Storage.Items;

/// <summary>SQL fragments shared by <see cref="ItemStore"/> and <see cref="SqliteTagStore"/> to
/// keep the denormalised <c>items.tag_text</c> column (the FTS5 tag column) in sync with the
/// <c>item_tags</c> join. Done in C# rather than with SQLite triggers on <c>item_tags</c>: a
/// trigger would also fire on the cascade of an item's hard delete and UPDATE the row that is
/// being deleted, racing the <c>items_ad</c> FTS trigger.</summary>
internal static class TagSql
{
    /// <summary>Space-joined tag names of the item in the outer <c>items</c> row, NULL when
    /// it has none.</summary>
    private const string TagTextOfRow =
        "(SELECT group_concat(t.name, ' ') FROM item_tags it JOIN tags t ON t.id = it.tag_id WHERE it.item_id = items.id)";

    /// <summary>Rebuild tag_text for one item. Parameter: <c>$item</c>.</summary>
    public const string RefreshItem =
        "UPDATE items SET tag_text = " + TagTextOfRow + " WHERE id = $item;";

    /// <summary>Rebuild tag_text for every item carrying a tag (after a rename). Parameter:
    /// <c>$tag</c>.</summary>
    public const string RefreshItemsOfTag =
        "UPDATE items SET tag_text = " + TagTextOfRow + " WHERE id IN (SELECT item_id FROM item_tags WHERE tag_id = $tag);";

    /// <summary>Rebuild tag_text for every item carrying a tag as if that tag were already
    /// gone. Run right before deleting the tag, while the join rows still say which items to
    /// touch. Parameter: <c>$tag</c>.</summary>
    public const string RefreshItemsOfTagExcludingIt =
        "UPDATE items SET tag_text = (SELECT group_concat(t.name, ' ') FROM item_tags it JOIN tags t ON t.id = it.tag_id WHERE it.item_id = items.id AND it.tag_id <> $tag)"
        + " WHERE id IN (SELECT item_id FROM item_tags WHERE tag_id = $tag);";
}
