using System.Globalization;
using System.Security.Cryptography;
using AresToys.Storage.Paths;

namespace AresToys.Storage.Blobs;

public sealed class FileSystemBlobStore : IBlobStore
{
    private readonly IStoragePathResolver _paths;

    public FileSystemBlobStore(IStoragePathResolver paths)
    {
        _paths = paths;
    }

    public async Task<string> AddAsync(
        ReadOnlyMemory<byte> content,
        string extension,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(extension);
        if (extension[0] == '.') extension = extension[1..];

        var hash = HexHash(content.Span);
        var relative = Path.Combine(
            timestamp.UtcDateTime.Year.ToString("D4", CultureInfo.InvariantCulture),
            timestamp.UtcDateTime.Month.ToString("D2", CultureInfo.InvariantCulture),
            timestamp.UtcDateTime.Day.ToString("D2", CultureInfo.InvariantCulture),
            $"{hash}.{extension}");

        var fullPath = Path.Combine(_paths.ResolveBlobRoot(), relative);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        await File.WriteAllBytesAsync(fullPath, content.ToArray(), cancellationToken).ConfigureAwait(false);
        return relative.Replace('\\', '/');
    }

    public async Task<byte[]> ReadAllAsync(string blobRef, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(blobRef);
        var fullPath = Path.Combine(_paths.ResolveBlobRoot(), blobRef.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"Blob '{blobRef}' not found.", fullPath);
        return await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
    }

    public Task<bool> DeleteAsync(string blobRef, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(blobRef);
        var fullPath = Path.Combine(_paths.ResolveBlobRoot(), blobRef.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(fullPath)) return Task.FromResult(false);
        File.Delete(fullPath);
        return Task.FromResult(true);
    }

    public async IAsyncEnumerable<string> EnumerateAllAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var root = _paths.ResolveBlobRoot();
        if (!Directory.Exists(root)) yield break;

        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            yield return relative;
            await Task.Yield();
        }
    }

    private static string HexHash(ReadOnlySpan<byte> content)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(content, hash);
        return Convert.ToHexString(hash[..6]).ToLowerInvariant();
    }
}
