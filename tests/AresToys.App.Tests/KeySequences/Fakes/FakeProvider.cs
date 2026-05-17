using AresToys.App.Services.KeySequences;

namespace AresToys.App.Tests.KeySequences.Fakes;

/// <summary>Minimal <see cref="ISequenceBindingProvider"/> for store tests. Exposes a mutable
/// <see cref="Bindings"/> list and a <see cref="RaiseChanged"/> helper so the test can simulate
/// any sequence of provider events the store has to react to.</summary>
internal sealed class FakeProvider : ISequenceBindingProvider
{
    public List<SequenceBinding> Bindings { get; } = new();

    public event EventHandler? BindingsChanged;

    public IReadOnlyList<SequenceBinding> GetBindings() => Bindings.ToList();

    public void RaiseChanged() => BindingsChanged?.Invoke(this, EventArgs.Empty);

    public bool HasSubscribers => BindingsChanged is not null;
}
