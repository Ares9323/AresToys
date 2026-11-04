using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using AresToys.Storage.Settings;

namespace AresToys.App.Services.Peek;

/// <summary>A program Peek can open a file with. <see cref="Args"/> is a command line template where <c>%f</c> is
/// replaced by the quoted file path; empty means just <c>%f</c>. <see cref="Id"/> is a GUID string.</summary>
public sealed record PeekProgram(string Id, string Name, string Exe, string Args);

/// <summary>The "Open in" programs and the extension to program assignments. Association keys are lower case
/// extensions with the dot (<c>.jpg</c>) or <c>.*</c> as the fallback; values are program ids. An empty value or a
/// missing key means the system default program.</summary>
public sealed class PeekOpenWithConfig
{
    public List<PeekProgram> Programs { get; set; } = new();
    public Dictionary<string, string> Associations { get; set; } = new();
}

/// <summary>Reads and writes the "Open in" config in the settings store, key <see cref="SettingKey"/>. Nothing is
/// cached: a change made in Settings applies the next time the config is loaded.</summary>
public sealed class PeekOpenWithStore(ISettingsStore settings, ILogger<PeekOpenWithStore>? logger = null)
{
    public const string SettingKey = "peek.open_with";

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>The stored config; an empty one when the key is missing or its JSON is invalid (logged).</summary>
    public async Task<PeekOpenWithConfig> LoadAsync(CancellationToken ct = default)
    {
        var raw = await settings.GetAsync(SettingKey, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(raw)) return new PeekOpenWithConfig();
        try
        {
            var cfg = JsonSerializer.Deserialize<PeekOpenWithConfig>(raw, Json);
            if (cfg is null) return new PeekOpenWithConfig();
            cfg.Programs ??= new List<PeekProgram>();
            cfg.Associations ??= new Dictionary<string, string>();
            return cfg;
        }
        catch (JsonException ex)
        {
            logger?.LogWarning(ex, "Peek open with: the stored config is not valid JSON, using an empty one");
            return new PeekOpenWithConfig();
        }
    }

    public Task SaveAsync(PeekOpenWithConfig config, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        return settings.SetAsync(SettingKey, JsonSerializer.Serialize(config), sensitive: false, ct);
    }
}

public static class PeekOpenWithLauncher
{
    /// <summary>The programs for <paramref name="path"/>: the assigned one first (extension match, else <c>.*</c>),
    /// then all the others.</summary>
    public static IReadOnlyList<PeekProgram> GetProgramsFor(PeekOpenWithConfig cfg, string path)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        var assigned = FindAssigned(cfg, path);
        if (assigned is null) return cfg.Programs.ToList();
        var result = new List<PeekProgram> { assigned };
        result.AddRange(cfg.Programs.Where(p => !ReferenceEquals(p, assigned)));
        return result;
    }

    /// <summary>The program assigned to the extension of <paramref name="path"/> (or to <c>.*</c>); null when there is
    /// no assignment or it points to a program that no longer exists.</summary>
    public static PeekProgram? FindAssigned(PeekOpenWithConfig cfg, string path)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (!TryAssignment(cfg, ext, out var id) && !TryAssignment(cfg, ".*", out id)) return null;
        return cfg.Programs.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryAssignment(PeekOpenWithConfig cfg, string key, out string id)
    {
        id = "";
        return key.Length > 0
               && cfg.Associations.TryGetValue(key, out var value)
               && !string.IsNullOrEmpty(value)
               && (id = value) is not null;
    }

    /// <summary>Opens <paramref name="path"/>: with the system default program when <paramref name="program"/> is
    /// null, otherwise by starting its exe with the argument template. False when the start failed (the cause is
    /// logged when <paramref name="logger"/> is given).</summary>
    public static bool Launch(PeekProgram? program, string path, ILogger? logger = null)
    {
        try
        {
            ProcessStartInfo info;
            if (program is null)
            {
                info = new ProcessStartInfo(path) { UseShellExecute = true };
            }
            else
            {
                var quoted = "\"" + path + "\"";
                info = new ProcessStartInfo(program.Exe)
                {
                    Arguments = string.IsNullOrWhiteSpace(program.Args) ? quoted : program.Args.Replace("%f", quoted),
                    UseShellExecute = false,
                    WorkingDirectory = Path.GetDirectoryName(path) ?? "",
                };
            }
            Process.Start(info)?.Dispose();
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            logger?.LogWarning(ex, "Peek open with: opening {Path} with {Program} failed", path, program?.Name ?? "the default program");
            return false;
        }
    }
}
