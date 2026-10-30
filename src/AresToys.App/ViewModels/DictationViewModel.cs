using System.Globalization;
using System.IO;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AresToys.AI.Dictation;
using AresToys.App.Resources;
using AresToys.App.Services.Dictation;
using AresToys.Storage.Settings;

namespace AresToys.App.ViewModels;

/// <summary>Backs the Settings → Dictation tab: spoken language, microphone, how long the speech model stays in
/// memory after a take, and the model file itself. Changes are saved as they are made.</summary>
public sealed partial class DictationViewModel : ObservableObject
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(30) };

    private readonly ISettingsStore _store;
    private readonly DictationService _service;
    private bool _loading;

    public DictationViewModel(ISettingsStore store, DictationService service)
    {
        _store = store;
        _service = service;
        Languages = DictationLanguages.All.Select(l => new Choice(l.Key, l.Label)).ToList();
        _selectedLanguage = Languages[0];
        _selectedProcessor = Processors[0];
        Devices = [];
        _selectedDevice = new Choice(string.Empty, Strings.Dictation_DefaultMicrophone);
    }

    public sealed record Choice(string Key, string Label);

    public IReadOnlyList<Choice> Languages { get; }

    public IReadOnlyList<Choice> Processors { get; } =
    [
        new Choice(DictationSettings.ProcessorAuto, Strings.Dictation_ProcessorAuto),
        new Choice(DictationSettings.ProcessorCpu, Strings.Dictation_ProcessorCpu),
    ];

    [ObservableProperty] private Choice _selectedProcessor = null!;

    /// <summary>The model file chosen by hand; empty uses the default place.</summary>
    [ObservableProperty] private string _modelPath = string.Empty;

    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private IReadOnlyList<Choice> _devices;
    [ObservableProperty] private Choice _selectedLanguage;
    [ObservableProperty] private Choice _selectedDevice;
    [ObservableProperty] private int _idleUnloadMinutes = DictationSettings.Default.IdleUnloadMinutes;
    [ObservableProperty] private string _modelStatus = string.Empty;

    [NotifyCanExecuteChangedFor(nameof(DownloadModelCommand))]
    [ObservableProperty] private bool _canDownload;

    public async Task LoadAsync()
    {
        var s = await DictationSettings.LoadAsync(_store, CancellationToken.None).ConfigureAwait(true);

        var devices = new List<Choice> { new(string.Empty, Strings.Dictation_DefaultMicrophone) };
        devices.AddRange(MicRecorder.Devices().Select(name => new Choice(name, name)));
        // A microphone that is no longer plugged in stays listed, so the saved choice is not silently replaced.
        if (s.Device.Length > 0 && devices.All(d => d.Key != s.Device)) devices.Add(new Choice(s.Device, s.Device));

        _loading = true;
        try
        {
            Enabled = s.Enabled;
            SelectedProcessor = Processors.First(p => p.Key == s.Processor);
            ModelPath = s.ModelPath;
            Devices = devices;
            SelectedDevice = devices.First(d => d.Key == s.Device);
            SelectedLanguage = Languages.FirstOrDefault(l => l.Key == s.Language) ?? Languages[0];
            IdleUnloadMinutes = s.IdleUnloadMinutes;
        }
        finally
        {
            _loading = false;
        }

        RefreshModelStatus();
    }

    partial void OnEnabledChanged(bool value)
    {
        if (!value) _service.UnloadModel();
        Persist();
    }

    partial void OnSelectedProcessorChanged(Choice value) => Persist();

    partial void OnModelPathChanged(string value)
    {
        Persist();
        if (!_loading) RefreshModelStatus();
    }

    [RelayCommand]
    private void BrowseModel()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = Strings.Dictation_ModelFileFilter,
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() == true) ModelPath = dialog.FileName;
    }

    [RelayCommand]
    private void UseDefaultModel() => ModelPath = string.Empty;

    partial void OnSelectedLanguageChanged(Choice value) => Persist();
    partial void OnSelectedDeviceChanged(Choice value) => Persist();
    partial void OnIdleUnloadMinutesChanged(int value) => Persist();

    private void Persist()
    {
        if (_loading || SelectedLanguage is null || SelectedDevice is null) return;
        var settings = new DictationSettings(SelectedLanguage.Key, SelectedDevice.Key, IdleUnloadMinutes, Enabled, SelectedProcessor.Key, ModelPath.Trim());
        _ = settings.SaveAsync(_store, CancellationToken.None);
    }

    private void RefreshModelStatus()
    {
        var chosen = ModelPath.Trim();
        var path = DictationService.ModelPath(chosen);
        var present = DictationModel.IsPresent(path);
        ModelStatus = present
            ? Strings.Dictation_ModelFound.Replace("{0}", path, StringComparison.Ordinal)
            : chosen.Length > 0
                ? Strings.Dictation_ModelChosenMissing.Replace("{0}", path, StringComparison.Ordinal)
                : Strings.Dictation_ModelMissing;
        // The download goes to the default place only; a file picked by hand is not replaced.
        CanDownload = !present && chosen.Length == 0;
    }

    [RelayCommand(CanExecute = nameof(CanDownload))]
    private async Task DownloadModelAsync()
    {
        CanDownload = false;
        try
        {
            var path = DictationService.ModelPath(null);
            var progress = new Progress<double>(value =>
                ModelStatus = Strings.Dictation_Downloading.Replace("{0}", ((int)(value * 100)).ToString(CultureInfo.CurrentCulture), StringComparison.Ordinal));
            await DictationModel.DownloadAsync(path, Http, progress, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or TaskCanceledException)
        {
            ModelStatus = Strings.Dictation_Failed.Replace("{0}", ex.Message, StringComparison.Ordinal);
            CanDownload = true;
            return;
        }

        RefreshModelStatus();
    }
}
