using System.Text.RegularExpressions;

namespace AresToys.App.Services.KeySequences;

/// <summary>
/// A bound key-sequence trigger: when the user types <see cref="Sequence"/> the matcher emits this
/// binding, and the dispatcher resolves <see cref="Target"/> to either a clipboard paste
/// (<see cref="ReplaceWithItem"/>) or a workflow run (<see cref="RunWorkflow"/>). See the design
/// spec at <c>docs/superpowers/specs/2026-05-16-key-sequences-design.md</c>.
/// </summary>
public sealed record SequenceBinding
{
    /// <summary>Max sequence length. Anything past 64 chars almost certainly isn't a deliberate
    /// trigger — capping here matches the tracker's rolling-buffer cap and keeps the matcher
    /// dictionary keys bounded.</summary>
    public const int MaxLength = 64;

    private static readonly Regex AllowedPattern = new(@"^[a-zA-Z0-9_]+$", RegexOptions.Compiled);

    public string Sequence { get; }
    public SequenceTarget Target { get; }

    public SequenceBinding(string sequence, SequenceTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (string.IsNullOrEmpty(sequence))
            throw new ArgumentException("Sequence must be non-empty.", nameof(sequence));
        if (sequence.Length > MaxLength)
            throw new ArgumentException($"Sequence exceeds {MaxLength} chars.", nameof(sequence));
        if (!AllowedPattern.IsMatch(sequence))
            throw new ArgumentException("Sequence must match [a-zA-Z0-9_]+.", nameof(sequence));
        Sequence = sequence;
        Target = target;
    }
}

/// <summary>Discriminated union of binding dispatch destinations. Closed hierarchy — only the two
/// derived records below are valid. Pattern-match exhaustively at the dispatch site.</summary>
public abstract record SequenceTarget;

/// <summary>Paste the clipboard item with the given storage id. The dispatcher resolves the item
/// via <see cref="AresToys.Storage.Items.IItemStore"/> and pushes it through <c>AutoPaster</c>.</summary>
public sealed record ReplaceWithItem(long ItemId) : SequenceTarget;

/// <summary>Run the workflow (pipeline profile) with the given string id via
/// <see cref="WorkflowRunner"/>. The workflow id is a string (not Guid) to match
/// <c>IPipelineProfileStore</c>.</summary>
public sealed record RunWorkflow(string WorkflowId) : SequenceTarget
{
    public string WorkflowId { get; } = !string.IsNullOrEmpty(WorkflowId)
        ? WorkflowId
        : throw new ArgumentException("WorkflowId must be non-empty.", nameof(WorkflowId));
}
