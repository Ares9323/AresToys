using AresToys.AI.Dictation;
using AresToys.Storage.Settings;

namespace AresToys.App.Services.Dictation;

/// <summary>Options of the dictation task, stored under <c>dictation.</c>. A missing or unparsable value falls back
/// to the default.</summary>
public sealed record DictationSettings(string Language, string Device, int IdleUnloadMinutes, bool Enabled = false, string Processor = DictationSettings.ProcessorAuto, string ModelPath = "")
{
    public const string KeyEnabled = "dictation.enabled";
    public const string KeyProcessor = "dictation.processor";
    public const string KeyModelPath = "dictation.model_path";

    /// <summary>The graphics card when the machine has one that can run it, else the processor.</summary>
    public const string ProcessorAuto = "auto";
    public const string ProcessorCpu = "cpu";
    public const string KeyLanguage = "dictation.language";
    public const string KeyDevice = "dictation.device";
    public const string KeyIdleUnloadMinutes = "dictation.idle_unload_minutes";

    public static DictationSettings Default { get; } = new(DictationLanguages.Default, string.Empty, 5);

    public static async Task<DictationSettings> LoadAsync(ISettingsStore store, CancellationToken ct)
    {
        var d = Default;
        var language = await store.GetAsync(KeyLanguage, ct).ConfigureAwait(false);
        var device = await store.GetAsync(KeyDevice, ct).ConfigureAwait(false);
        var idle = await store.GetAsync(KeyIdleUnloadMinutes, ct).ConfigureAwait(false);
        var enabled = await store.GetAsync(KeyEnabled, ct).ConfigureAwait(false);
        var processor = await store.GetAsync(KeyProcessor, ct).ConfigureAwait(false);
        var modelPath = await store.GetAsync(KeyModelPath, ct).ConfigureAwait(false);
        return new DictationSettings(
            string.IsNullOrWhiteSpace(language) ? d.Language : language.Trim(),
            device?.Trim() ?? d.Device,
            int.TryParse(idle, out var minutes) && minutes >= 0 ? minutes : d.IdleUnloadMinutes,
            enabled == "1",
            processor == ProcessorCpu ? ProcessorCpu : ProcessorAuto,
            modelPath?.Trim() ?? string.Empty);
    }

    public async Task SaveAsync(ISettingsStore store, CancellationToken ct)
    {
        await store.SetAsync(KeyEnabled, Enabled ? "1" : "0", false, ct).ConfigureAwait(false);
        await store.SetAsync(KeyProcessor, Processor, false, ct).ConfigureAwait(false);
        await store.SetAsync(KeyModelPath, ModelPath, false, ct).ConfigureAwait(false);
        await store.SetAsync(KeyLanguage, Language, false, ct).ConfigureAwait(false);
        await store.SetAsync(KeyDevice, Device, false, ct).ConfigureAwait(false);
        await store.SetAsync(KeyIdleUnloadMinutes, IdleUnloadMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture), false, ct).ConfigureAwait(false);
    }
}
