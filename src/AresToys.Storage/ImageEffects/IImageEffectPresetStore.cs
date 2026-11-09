using AresToys.ImageEffects;

namespace AresToys.Storage.ImageEffects;

/// <summary>Persistence for user-defined image effect presets. Presets are addressed by their
/// stable <see cref="EffectPreset.Id"/>; the store deserialises the chain on read so callers
/// always get a fully-bound <see cref="EffectPreset"/> ready to apply.</summary>
public interface IImageEffectPresetStore
{
    /// <summary>Every preset, ordered by user-defined <c>sort_order</c> (with <c>name</c> as
    /// the secondary key for stable display).</summary>
    Task<IReadOnlyList<EffectPreset>> ListAsync(CancellationToken cancellationToken);

    Task<EffectPreset?> GetAsync(string id, CancellationToken cancellationToken);

    /// <summary>Insert or update by <see cref="EffectPreset.Id"/>. <paramref name="sortOrder"/>
    /// only affects the order returned by <see cref="ListAsync"/>; pass null to leave the
    /// existing order untouched (or 0 for a brand-new row).</summary>
    Task UpsertAsync(EffectPreset preset, int? sortOrder, CancellationToken cancellationToken);

    Task<bool> DeleteAsync(string id, CancellationToken cancellationToken);

    /// <summary>Replace the entire ordering. The list must contain every preset id in display
    /// order; ids not in the list keep their old <c>sort_order</c>.</summary>
    Task ReorderAsync(IReadOnlyList<string> orderedIds, CancellationToken cancellationToken);

    /// <summary>Raised after any mutation (upsert / delete / reorder). Subscribers reload via
    /// <see cref="ListAsync"/>; we don't ship granular events because the workload is tiny.</summary>
    event EventHandler? Changed;
}
