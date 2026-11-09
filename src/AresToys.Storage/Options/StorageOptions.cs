namespace AresToys.Storage.Options;

public sealed class StorageOptions
{
    /// <summary>
    /// Absolute root directory for the database file and blobs.
    /// If null, the path resolver chooses based on portable/installer detection.
    /// </summary>
    public string? RootDirectoryOverride { get; set; }

    /// <summary>SQLite database file name (under the root directory).</summary>
    public string DatabaseFileName { get; set; } = "arestoys.db";

    /// <summary>Sub-directory under root for blob files.</summary>
    public string BlobSubdirectory { get; set; } = "blobs";

    /// <summary>Threshold in bytes above which item content goes to a blob file instead of the SQLite payload column.</summary>
    public long BlobThresholdBytes { get; set; } = 100 * 1024;

    /// <summary>Default rotation policy used when no override is provided to the rotation service.</summary>
    public RotationPolicyOptions Rotation { get; set; } = new();

    public sealed class RotationPolicyOptions
    {
        public int MaxItems { get; set; } = 1000;
        public TimeSpan MaxAge { get; set; } = TimeSpan.FromDays(30);
        public long MaxTotalBytes { get; set; } = 500L * 1024 * 1024;
        public TimeSpan SoftDeleteGracePeriod { get; set; } = TimeSpan.FromHours(24);
    }
}
