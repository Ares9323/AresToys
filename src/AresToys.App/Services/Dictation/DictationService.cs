using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Windows;
using Microsoft.Extensions.Logging;
using AresToys.AI.Dictation;
using AresToys.App.Resources;
using AresToys.Storage.Settings;

namespace AresToys.App.Services.Dictation;

/// <summary>Hold to talk, release to type. One take at a time: <see cref="RunTakeAsync"/> records while the key is held,
/// then transcribes locally and types the words into the window that has the focus. The speech model is downloaded
/// the first time (the take that finds it missing only fetches it).</summary>
public sealed class DictationService : IDisposable
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(30) };

    private readonly ISettingsStore _settings;
    private readonly ILogger<DictationService> _logger;
    private readonly MicRecorder _recorder = new();
    private readonly WhisperTranscriber _transcriber = new();
    private int _busy;

    public DictationService(ISettingsStore settings, ILogger<DictationService> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    /// <summary>Where the model is read from: the file the user chose, else the app's own models folder (where it is
    /// downloaded the first time).</summary>
    public static string ModelPath(string? chosen = null)
    {
        // A file the user pointed at wins, even when it is not there (the settings show it as missing).
        if (!string.IsNullOrWhiteSpace(chosen)) return chosen;

        return DictationModel.DefaultPath(System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AresToys-Data"));
    }

    /// <param name="isHeld">True while the user keeps the dictation key down.</param>
    public async Task RunTakeAsync(Func<bool> isHeld, CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;

        DictationPill? pill = null;
        try
        {
            var settings = await DictationSettings.LoadAsync(_settings, ct).ConfigureAwait(false);
            if (!settings.Enabled) return;
            _transcriber.IdleUnload = TimeSpan.FromMinutes(settings.IdleUnloadMinutes);
            _transcriber.CpuOnly = settings.Processor == DictationSettings.ProcessorCpu;
            var modelPath = ModelPath(settings.ModelPath);

            pill = await OnUi(() => new DictationPill(() => _recorder.Level)).ConfigureAwait(false);

            if (!DictationModel.IsPresent(modelPath))
            {
                // Downloading only makes sense for the default location; a file chosen by hand is the user's to fix.
                if (!string.IsNullOrWhiteSpace(settings.ModelPath)) throw new FileNotFoundException(modelPath);
                await DownloadModelAsync(pill, modelPath, ct).ConfigureAwait(false);
                return;
            }

            _recorder.Start(settings.Device);
            await OnUi(() => pill.ShowListening(Strings.Dictation_Listening)).ConfigureAwait(false);

            var started = Environment.TickCount64;
            while (isHeld() && Environment.TickCount64 - started < MicRecorder.MaxSeconds * 1000L && !ct.IsCancellationRequested)
            {
                await Task.Delay(30, CancellationToken.None).ConfigureAwait(false);
            }

            var samples = _recorder.Stop();
            await OnUi(() => pill.ShowPhase(Strings.Dictation_Transcribing)).ConfigureAwait(false);

            var text = await _transcriber.TranscribeAsync(samples, modelPath, DictationLanguages.CodeFor(settings.Language), ct).ConfigureAwait(false);
            _logger.LogInformation("Dictation: transcribed with {Runtime}", WhisperTranscriber.RuntimeName);
            await OnUi(pill.Dismiss).ConfigureAwait(false);
            pill = null;

            if (text.Length > 0) await OnUi(() => TextTyper.Type(text)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Dictation failed");
            _ = _recorder.Stop();
            if (pill is not null)
            {
                var shown = pill;
                await OnUi(() => shown.ShowPhase(Fill(Strings.Dictation_Failed, ex.Message))).ConfigureAwait(false);
                await Task.Delay(2500, CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            _ = _recorder.Stop();
            if (pill is not null) await OnUi(pill.Dismiss).ConfigureAwait(false);
            Volatile.Write(ref _busy, 0);
        }
    }

    private async Task DownloadModelAsync(DictationPill pill, string path, CancellationToken ct)
    {
        var last = -1;
        var progress = new Progress<double>(value =>
        {
            var percent = (int)(value * 100);
            if (percent == last) return;
            last = percent;
            pill.ShowPhase(Fill(Strings.Dictation_Downloading, percent));
        });
        await OnUi(() => pill.ShowPhase(Fill(Strings.Dictation_Downloading, 0))).ConfigureAwait(false);
        await DictationModel.DownloadAsync(path, Http, progress, ct).ConfigureAwait(false);
        await OnUi(() => pill.ShowPhase(Strings.Dictation_ModelReady)).ConfigureAwait(false);
        await Task.Delay(2500, CancellationToken.None).ConfigureAwait(false);
    }

    // The captions are localised templates with one {0}; a plain replace avoids a format call per caption.
    private static string Fill(string template, object value)
        => template.Replace("{0}", Convert.ToString(value, CultureInfo.CurrentCulture), StringComparison.Ordinal);

    private static Task OnUi(Action action) => Application.Current.Dispatcher.InvokeAsync(action).Task;

    private static Task<T> OnUi<T>(Func<T> func) => Application.Current.Dispatcher.InvokeAsync(func).Task;

    /// <summary>Frees the speech model now, e.g. when dictation is switched off.</summary>
    public void UnloadModel() => _transcriber.Unload();

    public void Dispose()
    {
        _recorder.Dispose();
        _transcriber.Dispose();
    }
}
