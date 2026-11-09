namespace AresToys.Pipeline.Tasks;

/// <summary>
/// Publishes an already-stored item to the OS clipboard (the Ctrl+V target / Win+V history).
/// Implementation lives in the App layer because it needs WPF / Win32 clipboard APIs that the
/// Pipeline csproj deliberately doesn't reference. Used by <see cref="AddToHistoryTask"/> when
/// the user opts in via the "Also push to Windows clipboard" toggle — keeps the two surfaces in
/// sync without forcing every recording / capture to land in the OS clipboard too.
/// </summary>
public interface IItemClipboardPublisher
{
    Task PublishAsync(long itemId, CancellationToken cancellationToken);
}
