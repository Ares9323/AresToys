using System.Collections.Frozen;
using System.Reflection;

namespace AresToys.ImageEffects;

/// <summary>Reflection-based catalogue of every concrete <see cref="ImageEffect"/> in this
/// assembly. Adding a new effect means dropping a new class — no manual registration. Each
/// entry is constructible (parameter-less ctor) so consumers can `Create(id)` to get a fresh
/// instance for a preset slot. The descriptor list is built once at first access, then frozen
/// for cheap repeat lookups.</summary>
public sealed class ImageEffectRegistry
{
    private static readonly Lazy<ImageEffectRegistry> _default = new(() => new ImageEffectRegistry(typeof(ImageEffect).Assembly));
    public static ImageEffectRegistry Default => _default.Value;

    private readonly FrozenDictionary<string, Type> _byId;
    private readonly FrozenDictionary<string, ImageEffectDescriptor> _descriptors;

    public ImageEffectRegistry(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        var byId = new Dictionary<string, Type>(StringComparer.Ordinal);
        var descriptors = new Dictionary<string, ImageEffectDescriptor>(StringComparer.Ordinal);

        foreach (var type in assembly.GetTypes())
        {
            if (type.IsAbstract || !typeof(ImageEffect).IsAssignableFrom(type)) continue;
            if (type.GetConstructor(Type.EmptyTypes) is null) continue;

            // Construct a probe instance once just to read Id / Name / Category. The probe is
            // discarded; consumers always go through Create() for a fresh, mutable instance.
            var probe = (ImageEffect)Activator.CreateInstance(type)!;
            if (byId.TryGetValue(probe.Id, out var existing))
                throw new InvalidOperationException($"Duplicate ImageEffect id '{probe.Id}' on {type.FullName} and {existing.FullName}");

            byId.Add(probe.Id, type);
            descriptors.Add(probe.Id, new ImageEffectDescriptor(probe.Id, probe.Name, probe.Category, type));
        }

        _byId = byId.ToFrozenDictionary(StringComparer.Ordinal);
        _descriptors = descriptors.ToFrozenDictionary(StringComparer.Ordinal);
    }

    /// <summary>Materialise a fresh instance of the effect identified by <paramref name="id"/>.
    /// Returns null when no effect matches — callers handling .sxie imports must tolerate this
    /// because a future-version preset may reference effects we haven't ported yet.</summary>
    public ImageEffect? Create(string id)
    {
        if (!_byId.TryGetValue(id, out var type)) return null;
        return (ImageEffect)Activator.CreateInstance(type)!;
    }

    public bool Contains(string id) => _byId.ContainsKey(id);

    public IReadOnlyCollection<ImageEffectDescriptor> All => _descriptors.Values;

    public IEnumerable<ImageEffectDescriptor> ByCategory(ImageEffectCategory category) =>
        _descriptors.Values.Where(d => d.Category == category);
}

public sealed record ImageEffectDescriptor(string Id, string Name, ImageEffectCategory Category, Type ClrType);
