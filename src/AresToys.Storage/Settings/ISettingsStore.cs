namespace AresToys.Storage.Settings;

/// <summary>One key/value pair from <see cref="ISettingsStore"/>. Sensitive values are
/// already decrypted (the store handles unprotection when reading) but the flag is preserved
/// so callers — e.g. an exporter — can decide what to surface in user-facing artifacts.</summary>
public sealed record SettingEntry(string Key, string Value, bool IsSensitive);

public interface ISettingsStore
{
    Task<string?> GetAsync(string key, CancellationToken cancellationToken);
    Task SetAsync(string key, string value, bool sensitive, CancellationToken cancellationToken);
    Task<bool> RemoveAsync(string key, CancellationToken cancellationToken);

    /// <summary>Enumerate every persisted setting. <paramref name="includeSensitive"/> = false
    /// (the default) skips entries flagged sensitive at write-time — credentials, OAuth
    /// tokens, anything that shouldn't land in a portable backup. Plain values are returned
    /// as-is; sensitive values are unprotected before yield.</summary>
    IAsyncEnumerable<SettingEntry> EnumerateAsync(bool includeSensitive = false,
        CancellationToken cancellationToken = default);
}
