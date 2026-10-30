using System.Security.Cryptography;

namespace AresToys.AI.Dictation;

/// <summary>The speech model file: where it lives, whether it is there, and the one download of it. The model is
/// whisper.cpp's multilingual "small" quantised to 5 bit (about 190 MB), fetched once on first use.</summary>
public static class DictationModel
{
    public const string FileName = "ggml-small-q5_1.bin";
    public const string Url = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/" + FileName;
    // The X-Linked-ETag the URL answers with, which is the file's SHA-256.
    public const string Sha256 = "ae85e4a935d7a567bd102fe55afc16bb595bdb618e11b2fc7591bc08120411bb";

    public static string DefaultPath(string dataFolder) => Path.Combine(dataFolder, "models", FileName);

    public static bool IsPresent(string path) => File.Exists(path);

    /// <summary>Downloads the model to <paramref name="path"/>. The bytes go to a ".part" file first and only a file with
    /// the expected hash is moved into place, so an interrupted or corrupt download never leaves a model that fails to load.
    /// <paramref name="progress"/> receives 0..1.</summary>
    public static async Task DownloadAsync(string path, HttpClient http, IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var part = path + ".part";
        try
        {
            using (var response = await http.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? 0;
                await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var target = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                var buffer = new byte[81920];
                long done = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    done += read;
                    if (total > 0) progress?.Report((double)done / total);
                }
            }

            if (!await HashMatchesAsync(part, ct).ConfigureAwait(false))
                throw new InvalidDataException("The downloaded speech model does not match its checksum.");

            File.Move(part, path, overwrite: true);
            progress?.Report(1);
        }
        finally
        {
            try { if (File.Exists(part)) File.Delete(part); } catch (IOException) { /* a later download starts the part file over */ }
        }
    }

    private static async Task<bool> HashMatchesAsync(string file, CancellationToken ct)
    {
        await using var stream = File.OpenRead(file);
        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return string.Equals(Convert.ToHexString(hash), Sha256, StringComparison.OrdinalIgnoreCase);
    }
}
