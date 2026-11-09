using System.IO;
using System.Windows.Media.Imaging;
using Microsoft.VisualBasic.FileIO;
using SkiaSharp;

namespace AresToys.App.Services.ImageResize;

/// <summary>What to do when the target file name is already taken.</summary>
public enum CollisionPolicy
{
    /// <summary>Add " (n)" before the extension until the name is free.</summary>
    Uniquify,
    /// <summary>Replace the existing file.</summary>
    Overwrite,
    /// <summary>Leave the existing file alone and write nothing.</summary>
    Skip,
}

/// <summary>Owns all disk writing for resized images: destination folder, collision policy,
/// the modified date and the replace-original sequence. Pure file system work, no UI.</summary>
public static class ImageFileWriter
{
    /// <summary>Reads the config value "on_collision" (uniquify, overwrite, skip). Unknown or
    /// missing values mean <see cref="CollisionPolicy.Uniquify"/>, the only policy that can never
    /// lose data.</summary>
    public static CollisionPolicy ParsePolicy(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "overwrite" => CollisionPolicy.Overwrite,
        "skip" => CollisionPolicy.Skip,
        _ => CollisionPolicy.Uniquify,
    };

    /// <summary>Folder that receives the result for <paramref name="sourcePath"/>: the source
    /// folder when <paramref name="outputFolder"/> is empty, that folder when it is absolute,
    /// otherwise the value taken relative to the source folder.</summary>
    public static string ResolveDirectory(string sourcePath, string? outputFolder)
    {
        var sourceDir = Path.GetDirectoryName(Path.GetFullPath(sourcePath)) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(outputFolder)) return sourceDir;
        return Path.GetFullPath(Path.Combine(sourceDir, outputFolder.Trim()));
    }

    /// <summary>Writes <paramref name="data"/> as <c>directory\fileNameWithoutExtension.extension</c>
    /// following <paramref name="policy"/>, creating the directory when needed. Returns the path
    /// written, or null when the policy is <see cref="CollisionPolicy.Skip"/> and the file exists.
    /// A name that is already taken is never overwritten unless the policy says so. When
    /// <paramref name="keepDateFrom"/> is set the result gets that file's modified date.</summary>
    public static string? WriteNew(string directory, string fileNameWithoutExtension, string extension,
        byte[] data, CollisionPolicy policy, string? keepDateFrom)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, FileNameTemplate.Sanitize(fileNameWithoutExtension) + "." + extension.TrimStart('.'));

        FileMode mode;
        switch (policy)
        {
            case CollisionPolicy.Overwrite:
                mode = FileMode.Create;
                break;
            case CollisionPolicy.Skip:
                if (File.Exists(path)) return null;
                mode = FileMode.CreateNew;
                break;
            default:
                path = FileNameTemplate.Uniquify(path);
                mode = FileMode.CreateNew;
                break;
        }

        // CreateNew makes the "never overwrites" promise hold even if another process creates the
        // same name between the check above and the write: that surfaces as an IOException.
        var modified = keepDateFrom is null ? (DateTime?)null : File.GetLastWriteTimeUtc(keepDateFrom);
        using (var fs = new FileStream(path, mode, FileAccess.Write, FileShare.None))
        {
            fs.Write(data, 0, data.Length);
        }
        if (modified is { } m) File.SetLastWriteTimeUtc(path, m);
        return path;
    }

    /// <summary>Replaces <paramref name="sourcePath"/> with the new image. The result is written
    /// to a temp file next to the original first, the original then goes to the Recycle Bin (never
    /// deleted permanently), and the result is moved to the original name with the new extension.
    /// If the original cannot be sent to the Recycle Bin the temp file is removed and an
    /// <see cref="IOException"/> is thrown with the original untouched. Returns the final path.</summary>
    public static string ReplaceOriginal(string sourcePath, string extension, byte[] data, bool keepDate)
    {
        var fullSource = Path.GetFullPath(sourcePath);
        var dir = Path.GetDirectoryName(fullSource)!;

        if (!CanRecycle(fullSource))
        {
            throw new IOException("The original is on a drive without a Recycle Bin (network or removable), so it will not be replaced.");
        }

        var ext = "." + extension.TrimStart('.');
        var target = string.Equals(Path.GetExtension(fullSource), ext, StringComparison.OrdinalIgnoreCase)
            ? fullSource
            : Path.Combine(dir, Path.GetFileNameWithoutExtension(fullSource) + ext);

        var modified = keepDate ? File.GetLastWriteTimeUtc(fullSource) : (DateTime?)null;
        var temp = Path.Combine(dir, "." + Guid.NewGuid().ToString("N") + ".resize.tmp");
        try
        {
            using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                fs.Write(data, 0, data.Length);
            }
            if (modified is { } m) File.SetLastWriteTimeUtc(temp, m);

            FileSystem.DeleteFile(fullSource, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
        }
        catch
        {
            // Nothing of the original was lost: drop the half finished result and let the caller
            // report the original error.
            if (File.Exists(temp)) File.Delete(temp);
            throw;
        }

        // A different file may already own the target name when the extension changed: never
        // overwrite it.
        if (!string.Equals(target, fullSource, StringComparison.OrdinalIgnoreCase)) target = FileNameTemplate.Uniquify(target);
        try
        {
            File.Move(temp, target, overwrite: false);
        }
        catch (Exception ex)
        {
            throw new IOException($"The original is in the Recycle Bin and the resized image was left at '{temp}': {ex.Message}", ex);
        }
        return target;
    }

    /// <summary>Pixel size of an encoded image, for the {ow} and {oh} name tokens. False when the
    /// bytes cannot be read as an image by Skia or the Windows codecs.</summary>
    public static bool TryGetPixelSize(byte[] data, out int width, out int height)
    {
        using (var codec = SKCodec.Create(new SKMemoryStream(data)))
        {
            if (codec is not null)
            {
                width = codec.Info.Width;
                height = codec.Info.Height;
                return true;
            }
        }

        // TIFF output is encoded by WPF and Skia cannot read it back.
        try
        {
            using var ms = new MemoryStream(data, writable: false);
            var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            width = decoder.Frames[0].PixelWidth;
            height = decoder.Frames[0].PixelHeight;
            return true;
        }
        catch (Exception ex) when (ex is NotSupportedException or IOException or InvalidOperationException or ArgumentException)
        {
            width = 0;
            height = 0;
            return false;
        }
    }

    /// <summary>The Recycle Bin exists on fixed local drives only. A file on a network share or a
    /// removable drive would be deleted permanently, so those are refused.</summary>
    private static bool CanRecycle(string fullPath)
    {
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal)) return false;
        return new DriveInfo(root).DriveType == DriveType.Fixed;
    }
}
