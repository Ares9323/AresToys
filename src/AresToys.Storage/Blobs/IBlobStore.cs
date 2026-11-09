namespace AresToys.Storage.Blobs;

public interface IBlobStore
{
    /// <summary>Persists <paramref name="content"/> and returns the relative blob ref to store in <c>items.blob_ref</c>.</summary>
    Task<string> AddAsync(ReadOnlyMemory<byte> content, string extension, DateTimeOffset timestamp, CancellationToken cancellationToken);

    /// <summary>Reads the bytes referenced by <paramref name="blobRef"/>. Throws <see cref="FileNotFoundException"/> if missing.</summary>
    Task<byte[]> ReadAllAsync(string blobRef, CancellationToken cancellationToken);

    /// <summary>Deletes the blob; idempotent (returns false if it did not exist).</summary>
    Task<bool> DeleteAsync(string blobRef, CancellationToken cancellationToken);

    /// <summary>Lists every blob ref currently on disk under the blob root. Use for orphan reconciliation.</summary>
    IAsyncEnumerable<string> EnumerateAllAsync(CancellationToken cancellationToken);
}
