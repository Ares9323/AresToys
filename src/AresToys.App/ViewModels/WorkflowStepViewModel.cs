using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AresToys.App.ViewModels;

public sealed partial class WorkflowStepViewModel : ObservableObject
{
    private readonly Action<WorkflowStepViewModel, bool> _onEnabledChanged;
    private readonly Action<WorkflowStepViewModel, int> _onMove;
    private readonly Action<WorkflowStepViewModel> _onRemove;
    private readonly Action<WorkflowStepViewModel>? _onDuplicate;
    private readonly Action<WorkflowStepViewModel, int>? _onParameterChanged;
    private readonly Action<WorkflowStepViewModel, string, bool>? _onBoolParameterChanged;
    private readonly Action<WorkflowStepViewModel, string, string>? _onStringParameterChanged;
    private bool _suppress;

    public WorkflowStepViewModel(
        int storageIndex,
        string taskId,
        string displayName,
        string? description,
        string? category,
        bool initiallyEnabled,
        IntParameter? parameter,
        int parameterValue,
        IReadOnlyList<BoolParameter>? boolParameters,
        IReadOnlyDictionary<string, bool>? boolParameterValues,
        IReadOnlyList<StringParameter>? stringParameters,
        IReadOnlyDictionary<string, string>? stringParameterValues,
        Action<WorkflowStepViewModel, bool> onEnabledChanged,
        Action<WorkflowStepViewModel, int> onMove,
        Action<WorkflowStepViewModel> onRemove,
        Action<WorkflowStepViewModel, int>? onParameterChanged,
        Action<WorkflowStepViewModel, string, bool>? onBoolParameterChanged = null,
        Action<WorkflowStepViewModel, string, string>? onStringParameterChanged = null,
        IReadOnlyList<WorkflowPort>? inputs = null,
        IReadOnlyList<WorkflowPort>? outputs = null,
        Action<WorkflowStepViewModel>? onDuplicate = null)
    {
        StorageIndex = storageIndex;
        TaskId = taskId;
        DisplayName = displayName;
        Description = description;
        Category = category;
        Parameter = parameter;
        Inputs = (inputs ?? Array.Empty<WorkflowPort>())
            .Select(p => new WorkflowPortEntry(p, isInput: true)).ToArray();
        Outputs = (outputs ?? Array.Empty<WorkflowPort>())
            .Select(p => new WorkflowPortEntry(p, isInput: false)).ToArray();
        _onEnabledChanged = onEnabledChanged;
        _onMove = onMove;
        _onRemove = onRemove;
        _onDuplicate = onDuplicate;
        _onParameterChanged = onParameterChanged;
        _onBoolParameterChanged = onBoolParameterChanged;
        _onStringParameterChanged = onStringParameterChanged;
        _suppress = true;
        IsEnabled = initiallyEnabled;
        ParameterValue = parameter is null ? 0 : Math.Clamp(parameterValue, parameter.Min, parameter.Max);

        // Build the bool-parameter row VMs once. Each one captures its key + a callback that
        // forwards changes back to the editor for persistence into step.Config[key].
        BoolParameters = new ObservableCollection<BoolParameterEntry>();
        if (boolParameters is not null)
        {
            foreach (var bp in boolParameters)
            {
                var initial = boolParameterValues is not null && boolParameterValues.TryGetValue(bp.Key, out var v)
                    ? v : bp.DefaultValue;
                BoolParameters.Add(new BoolParameterEntry(bp.Key, bp.Label, initial,
                    (key, value) => _onBoolParameterChanged?.Invoke(this, key, value)));
            }
        }

        // String parameters mirror the bool path: one VM entry per declared parameter, captures
        // its key + persistence callback. Used for paths / args / shell commands on launch tasks.
        StringParameters = new ObservableCollection<StringParameterEntry>();
        if (stringParameters is not null)
        {
            foreach (var sp in stringParameters)
            {
                var initial = stringParameterValues is not null && stringParameterValues.TryGetValue(sp.Key, out var v)
                    ? v : sp.DefaultValue;
                // Resolve the dropdown source for parameters that declare an OptionsKey
                // (e.g. "image_effect_presets" → live preset list). Provider is registered in
                // App.xaml.cs and may legitimately be missing under unit tests — null Options
                // simply falls back to a plain TextBox.
                IReadOnlyList<string>? options = null;
                if (sp.OptionsKey is not null
                    && WorkflowActionCatalog.OptionsProviders.TryGetValue(sp.OptionsKey, out var provider))
                {
                    try { options = provider(); }
                    catch { options = Array.Empty<string>(); }
                }
                StringParameters.Add(new StringParameterEntry(sp.Key, sp.Label, sp.Placeholder, initial,
                    sp.Picker, options, sp.IsEditable, sp.LocalizeOptionsAsEnum, sp.LocalizeOptionsAsLauncherKey,
                    sp.LocalizeOptionsAsColorFormat, sp.LocalizeOptionsAsSettingsTab,
                    (key, value) => _onStringParameterChanged?.Invoke(this, key, value)));
            }
        }
        _suppress = false;
    }

    /// <summary>Index of this step in the underlying profile.Steps list (mutated as steps are
    /// added / removed / reordered, kept in sync by <see cref="WorkflowEditorViewModel"/>).</summary>
    public int StorageIndex { get; set; }
    public string TaskId { get; }
    public string DisplayName { get; }
    public string? Description { get; }
    public string? Category { get; }

    /// <summary>The integer parameter shape for this step (null = no inline input).</summary>
    public IntParameter? Parameter { get; }
    public bool HasParameter => Parameter is not null;
    public ObservableCollection<BoolParameterEntry> BoolParameters { get; }
    public bool HasBoolParameters => BoolParameters.Count > 0;
    public ObservableCollection<StringParameterEntry> StringParameters { get; }
    public bool HasStringParameters => StringParameters.Count > 0;
    /// <summary>Bag-port pills rendered above the step row (consumed types).</summary>
    public IReadOnlyList<WorkflowPortEntry> Inputs { get; }
    public bool HasInputs => Inputs.Count > 0;
    /// <summary>Bag-port pills rendered below the step row (produced types).</summary>
    public IReadOnlyList<WorkflowPortEntry> Outputs { get; }
    public bool HasOutputs => Outputs.Count > 0;
    public string? ParameterLabel => Parameter?.Label;
    public int ParameterMin => Parameter?.Min ?? 0;
    public int ParameterMax => Parameter?.Max ?? 0;

    [ObservableProperty]
    private bool _isEnabled;

    [ObservableProperty]
    private bool _canMoveUp;

    [ObservableProperty]
    private bool _canMoveDown;

    [ObservableProperty]
    private int _parameterValue;

    /// <summary>True while this row is being dragged. UI dims the row so the user can see what
    /// they picked up; cleared by the editor when the drag operation completes.</summary>
    [ObservableProperty]
    private bool _isDragSource;

    /// <summary>True when the drag's drop position is just above this row — UI shows a single
    /// insertion line in the gap above this row. "Drop after row N" is rendered as
    /// "above row N+1" so we never need a second indicator per row; the visual is always one
    /// line in one gap.</summary>
    [ObservableProperty]
    private bool _isDropTargetAbove;

    partial void OnIsEnabledChanged(bool value)
    {
        if (_suppress) return;
        _onEnabledChanged(this, value);
    }

    partial void OnParameterValueChanged(int value)
    {
        if (_suppress) return;
        if (Parameter is null) return;
        var clamped = Math.Clamp(value, Parameter.Min, Parameter.Max);
        if (clamped != value)
        {
            _suppress = true;
            ParameterValue = clamped;
            _suppress = false;
        }
        _onParameterChanged?.Invoke(this, clamped);
    }

    [RelayCommand]
    private void MoveUp() => _onMove(this, -1);

    [RelayCommand]
    private void MoveDown() => _onMove(this, 1);

    [RelayCommand]
    private void Remove() => _onRemove(this);

    /// <summary>Insert a clone of this step immediately below the current one. The editor
    /// (which holds the storage list + the catalog descriptor) builds the new PipelineStep —
    /// this VM just signals intent.</summary>
    [RelayCommand]
    private void Duplicate() => _onDuplicate?.Invoke(this);

    [RelayCommand]
    private void DecrementParameter()
    {
        if (Parameter is null) return;
        var next = ParameterValue - 1;
        if (next < Parameter.Min) return;
        ParameterValue = next; // OnParameterValueChanged persists.
    }

    [RelayCommand]
    private void IncrementParameter()
    {
        if (Parameter is null) return;
        var next = ParameterValue + 1;
        if (next > Parameter.Max) return;
        ParameterValue = next;
    }
}

/// <summary>One bag-port segment rendered as a thin coloured strip ABOVE (Inputs) or BELOW
/// (Outputs) the step card in the workflow editor. <see cref="Background"/> matches the legend
/// (azzurro / rosa / giallo) and is precomputed + frozen so XAML can bind Border.Background
/// directly without a converter. <see cref="Tooltip"/> is what the strip shows on hover —
/// "Input: Payload" or "Output: Text".</summary>
public sealed class WorkflowPortEntry
{
    private static readonly System.Windows.Media.Brush PayloadBrush = Freeze("#26539D"); // azzurro
    private static readonly System.Windows.Media.Brush TextBrush    = Freeze("#9F3167"); // rosa
    private static readonly System.Windows.Media.Brush ColorBrush   = Freeze("#D3A107"); // giallo

    private static System.Windows.Media.Brush Freeze(string hex)
    {
        var b = (System.Windows.Media.SolidColorBrush)new System.Windows.Media.BrushConverter().ConvertFromString(hex)!;
        b.Freeze();
        return b;
    }

    public WorkflowPortEntry(WorkflowPort port, bool isInput)
    {
        Port = port;
        var label = port switch
        {
            WorkflowPort.Payload => "Payload",
            WorkflowPort.Text    => "Text",
            WorkflowPort.Color   => "Color",
            _ => port.ToString(),
        };
        Tooltip = (isInput ? "Input: " : "Output: ") + label;
        Background = port switch
        {
            WorkflowPort.Payload => PayloadBrush,
            WorkflowPort.Text    => TextBrush,
            WorkflowPort.Color   => ColorBrush,
            _ => System.Windows.Media.Brushes.Gray,
        };
    }

    public WorkflowPort Port { get; }
    public string Tooltip { get; }
    public System.Windows.Media.Brush Background { get; }
}

/// <summary>One row's worth of bool-config state for a step. Two-way bindable so a CheckBox
/// in the workflow editor commits the change immediately, and the supplied callback persists
/// the value into step.Config[<see cref="Key"/>].</summary>
public sealed partial class BoolParameterEntry : ObservableObject
{
    private readonly Action<string, bool> _onChanged;
    private bool _suppress;

    public BoolParameterEntry(string key, string label, bool initialValue, Action<string, bool> onChanged)
    {
        Key = key;
        Label = label;
        _onChanged = onChanged;
        _suppress = true;
        IsChecked = initialValue;
        _suppress = false;
    }

    public string Key { get; }
    public string Label { get; }

    [ObservableProperty]
    private bool _isChecked;

    partial void OnIsCheckedChanged(bool value)
    {
        if (_suppress) return;
        _onChanged(Key, value);
    }
}

/// <summary>One row's worth of string-config state for a step (path, args, shell command).
/// Two-way bindable so a TextBox in the workflow editor commits the change immediately, and
/// the supplied callback persists the value into step.Config[<see cref="Key"/>]. Picker kind
/// drives Show* flags + Browse* commands so the UI can opt into file/folder dialogs without
/// each row needing its own code-behind handler.</summary>
public sealed partial class StringParameterEntry : ObservableObject
{
    private readonly Action<string, string> _onChanged;
    private bool _suppress;

    public StringParameterEntry(
        string key,
        string label,
        string? placeholder,
        string initialValue,
        StringPickerKind picker,
        IReadOnlyList<string>? options,
        bool isEditable,
        bool localizeOptionsAsEnum,
        bool localizeOptionsAsLauncherKey,
        bool localizeOptionsAsColorFormat,
        bool localizeOptionsAsSettingsTab,
        Action<string, string> onChanged)
    {
        Key = key;
        Label = label;
        Placeholder = placeholder;
        Picker = picker;
        IsEditable = isEditable;
        // Build OptionEntries with Raw/Display pairs. When LocalizeOptionsAsEnum is true the
        // Display passes through ImageEffectLocalizer (EnumValue_<raw>) — Crop / Rectangle /
        // … render in the active culture. LocalizeOptionsAsLauncherKey instead routes through
        // KeyboardLayoutMapper so the user sees the glyph their keycaps print (italian
        // ";"→"Ò", "/"→"-", etc.) while the Raw value stays US-canonical. The empty entry
        // keeps an explicit "(use last)" label so the user understands what selecting it
        // does — without that, an empty row looks like a UI glitch.
        if (options is { Count: > 0 })
        {
            var list = new List<OptionEntry>(options.Count);
            foreach (var raw in options)
            {
                string display;
                if (string.IsNullOrEmpty(raw))
                {
                    display = placeholder ?? "(use last)";
                }
                else if (localizeOptionsAsEnum)
                {
                    display = Services.ImageEffectLocalizer.LocalizeEnumValue(raw, raw);
                }
                else if (localizeOptionsAsLauncherKey)
                {
                    display = Services.Launcher.KeyboardLayoutMapper.GetDisplayChar(raw);
                }
                else if (localizeOptionsAsColorFormat)
                {
                    display = Services.ColorFormatLabels.LabelFor(raw);
                }
                else if (localizeOptionsAsSettingsTab)
                {
                    display = Services.SettingsTabLabels.LabelFor(raw);
                }
                else
                {
                    display = raw;
                }
                list.Add(new OptionEntry(raw, display));
            }
            OptionEntries = list;
        }
        _onChanged = onChanged;
        _suppress = true;
        Value = initialValue;
        _suppress = false;
    }

    public string Key { get; }
    public string Label { get; }
    public string? Placeholder { get; }
    public StringPickerKind Picker { get; }
    public bool IsEditable { get; }
    /// <summary>Pre-built (Raw, Display) pairs the ComboBox renders. Display is what the user
    /// sees, Raw is what gets written back to <see cref="Value"/> when the row is selected.</summary>
    public IReadOnlyList<OptionEntry>? OptionEntries { get; }
    public bool HasOptions => OptionEntries is { Count: > 0 };
    public bool ShowFileButton => Picker is StringPickerKind.File or StringPickerKind.FileOrFolder;
    public bool ShowFolderButton => Picker is StringPickerKind.Folder or StringPickerKind.FileOrFolder;
    public bool ShowHotkeyCaptureButton => Picker is StringPickerKind.HotkeyCapture;

    public sealed record OptionEntry(string Raw, string Display);

    [ObservableProperty]
    private string _value = string.Empty;

    partial void OnValueChanged(string value)
    {
        if (_suppress) return;
        _onChanged(Key, value);
    }

    /// <summary>Open a file-open dialog seeded at the current value's folder (when valid). On
    /// confirm, the chosen path replaces <see cref="Value"/> — which fires OnValueChanged →
    /// persistence callback, same path as a manual edit.</summary>
    [RelayCommand]
    private void BrowseFile()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = $"Pick file for {Label}",
            CheckFileExists = true,
            Multiselect = false,
        };
        SeedInitialDirectory(dlg);
        if (dlg.ShowDialog() == true)
        {
            Value = dlg.FileName;
        }
    }

    /// <summary>Open a folder-open dialog. Uses the .NET 8+ <c>OpenFolderDialog</c> which is the
    /// modern Win32 IFileDialog flavour — no FolderBrowserDialog (that's WinForms-era) and no
    /// shim around the file picker.</summary>
    [RelayCommand]
    private void BrowseFolder()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = $"Pick folder for {Label}",
        };
        if (!string.IsNullOrWhiteSpace(Value))
        {
            try
            {
                var expanded = Environment.ExpandEnvironmentVariables(Value);
                if (System.IO.Directory.Exists(expanded))
                {
                    dlg.InitialDirectory = expanded;
                }
                else
                {
                    var parent = System.IO.Path.GetDirectoryName(expanded);
                    if (!string.IsNullOrEmpty(parent) && System.IO.Directory.Exists(parent))
                        dlg.InitialDirectory = parent;
                }
            }
            catch { /* fall back to dialog default */ }
        }
        if (dlg.ShowDialog() == true)
        {
            Value = dlg.FolderName;
        }
    }

    private void SeedInitialDirectory(Microsoft.Win32.OpenFileDialog dlg)
    {
        if (string.IsNullOrWhiteSpace(Value)) return;
        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(Value);
            var dir = System.IO.Directory.Exists(expanded)
                ? expanded
                : System.IO.Path.GetDirectoryName(expanded);
            if (!string.IsNullOrEmpty(dir) && System.IO.Directory.Exists(dir))
            {
                dlg.InitialDirectory = dir;
            }
        }
        catch { /* fall back to dialog default */ }
    }

    /// <summary>Open the same <c>HotkeyCaptureWindow</c> the Settings → Hotkeys list uses for
    /// rebind, then write the captured combo back as a human-readable string
    /// (<c>"Ctrl + Shift + T"</c>) via <see cref="Services.Hotkeys.HotkeyDisplay.Format"/> — the
    /// PressKeyTask config parser round-trips the same format. Clear-binding from the dialog
    /// writes an empty string so the task skips at runtime; Cancel leaves the value untouched.</summary>
    [RelayCommand]
    private void CaptureHotkey()
    {
        var dialog = new Views.HotkeyCaptureWindow(canReset: false);
        if (System.Windows.Application.Current?.MainWindow is { } owner && owner.IsVisible)
            dialog.Owner = owner;
        var ok = dialog.ShowDialog();
        if (ok != true) return;
        if (dialog.ClearRequested)
        {
            Value = string.Empty;
            return;
        }
        if (dialog.CapturedVirtualKey == 0) return;
        Value = Services.Hotkeys.HotkeyDisplay.Format(dialog.CapturedModifiers, dialog.CapturedVirtualKey);
    }
}
