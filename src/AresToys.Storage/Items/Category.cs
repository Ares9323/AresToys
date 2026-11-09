namespace AresToys.Storage.Items;

/// <summary>One user-defined clipboard category (CopyQ-style "tab"). The default <c>"Clipboard"</c>
/// bucket exists in every install; users add/rename/remove others through the Categories
/// settings page. <see cref="MaxItems"/> = 0 disables the cap; <see cref="AutoCleanupAfter"/>
/// = 0 disables auto-purge. The unit of <see cref="AutoCleanupAfter"/> is currently MINUTES —
/// the column name is generic so a future change of unit doesn't need another rename.</summary>
public sealed record Category(
    string Name,
    string? Icon,
    int SortOrder,
    int MaxItems = 0,
    int AutoCleanupAfter = 0)
{
    public const string Default = "Clipboard";
}
