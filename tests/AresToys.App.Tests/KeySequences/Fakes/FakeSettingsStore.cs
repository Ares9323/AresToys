using System.Runtime.CompilerServices;
using AresToys.Storage.Settings;

namespace AresToys.App.Tests.KeySequences.Fakes;

/// <summary>In-memory <see cref="ISettingsStore"/>. Only Get/Set/Remove are wired — EnumerateAsync
/// throws because no test exercises it (it'd be dead code; if a future test needs it, add an
/// impl rather than letting silent default behavior mask a missed assertion).</summary>
internal sealed class FakeSettingsStore : ISettingsStore
{
    public Dictionary<string, string> Backing { get; } = new(StringComparer.Ordinal);

    public Task<string?> GetAsync(string key, CancellationToken cancellationToken)
    {
        return Task.FromResult(Backing.TryGetValue(key, out var v) ? v : null);
    }

    public Task SetAsync(string key, string value, bool sensitive, CancellationToken cancellationToken)
    {
        Backing[key] = value;
        return Task.CompletedTask;
    }

    public Task<bool> RemoveAsync(string key, CancellationToken cancellationToken)
    {
        return Task.FromResult(Backing.Remove(key));
    }

    public async IAsyncEnumerable<SettingEntry> EnumerateAsync(bool includeSensitive = false,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        throw new NotSupportedException("EnumerateAsync is not used by these tests.");
#pragma warning disable CS0162 // unreachable, satisfies the iterator contract
        yield break;
#pragma warning restore CS0162
    }
}
