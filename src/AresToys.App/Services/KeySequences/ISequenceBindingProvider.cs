namespace AresToys.App.Services.KeySequences;

/// <summary>
/// Source of bindings that the <see cref="SequenceBindingStore"/> aggregates. Each provider owns
/// one domain: clipboard items (one provider) and workflow-triggers settings list (another).
/// Providers signal "my bindings changed; please rebuild" via <see cref="BindingsChanged"/> and
/// expose their current snapshot via <see cref="GetBindings"/>. They MUST be safe to call from
/// any thread (the store may rebuild on the UI dispatcher in response to a background change
/// event from the underlying store).
/// </summary>
public interface ISequenceBindingProvider
{
    IReadOnlyList<SequenceBinding> GetBindings();

    /// <summary>Raised when the provider's bindings change (item added/edited/deleted, settings
    /// list mutated, etc). The store re-aggregates from all providers on this event.</summary>
    event EventHandler? BindingsChanged;
}
