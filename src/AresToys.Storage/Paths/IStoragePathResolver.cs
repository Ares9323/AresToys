namespace AresToys.Storage.Paths;

public interface IStoragePathResolver
{
    /// <summary>Absolute root directory used by Storage. Created if missing.</summary>
    string ResolveRoot();

    /// <summary>Absolute path to the SQLite database file. Parent directory is created if missing.</summary>
    string ResolveDatabasePath();

    /// <summary>Absolute path to the blob root directory. Created if missing.</summary>
    string ResolveBlobRoot();
}
