using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using AresToys.App.Resources;
using AresToys.App.Services.Hotkeys;
using AresToys.Hotkeys;

namespace AresToys.App.Views;

/// <summary>Picks a key and its modifiers from lists, for the keys a capture dialog cannot record
/// (F13-F24, media and browser keys, keys the keyboard in use does not have). The result is the same
/// combo string the capture dialog writes, so <c>PressKeyTask</c> reads both alike.</summary>
public partial class KeyPickerWindow : Wpf.Ui.Controls.FluentWindow
{
    private sealed record KeyEntry(string Label, uint Vk, string Group);

    public KeyPickerWindow(string? currentCombo)
    {
        InitializeComponent();

        var view = new ListCollectionView(BuildKeys());
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(KeyEntry.Group)));
        KeyList.ItemsSource = view;
        // Typing narrows the list; no spaces or case needed, so "f24", "F 24" and "volume" all find their key.
        view.Filter = o => o is KeyEntry k && Matches(k, SearchBox.Text);
        SearchBox.TextChanged += (_, _) =>
        {
            view.Refresh();
            if (KeyList.SelectedItem is null) KeyList.SelectedItem = view.Cast<KeyEntry>().FirstOrDefault();
        };
        SearchBox.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Down && KeyList.Items.Count > 0) { KeyList.Focus(); }
            else if (e.Key == System.Windows.Input.Key.Enter && OkButton.IsEnabled) { DialogResult = true; Close(); }
        };
        Loaded += (_, _) => SearchBox.Focus();

        // Start from what the field holds, so editing a combo does not begin from scratch.
        if (KeyComboParser.Parse(currentCombo) is { } parsed)
        {
            CtrlBox.IsChecked = (parsed.Modifiers & HotkeyModifiers.Control) != 0;
            ShiftBox.IsChecked = (parsed.Modifiers & HotkeyModifiers.Shift) != 0;
            AltBox.IsChecked = (parsed.Modifiers & HotkeyModifiers.Alt) != 0;
            WinBox.IsChecked = (parsed.Modifiers & HotkeyModifiers.Win) != 0;
            KeyList.SelectedItem = view.Cast<KeyEntry>().FirstOrDefault(k => k.Vk == parsed.VirtualKey);
            if (KeyList.SelectedItem is not null) KeyList.ScrollIntoView(KeyList.SelectedItem);
        }

        foreach (var box in new[] { CtrlBox, ShiftBox, AltBox, WinBox })
        {
            box.Checked += (_, _) => UpdatePreview();
            box.Unchecked += (_, _) => UpdatePreview();
        }
        KeyList.SelectionChanged += (_, _) => UpdatePreview();
        OkButton.Click += (_, _) => { DialogResult = true; Close(); };
        UpdatePreview();
    }

    public HotkeyModifiers Modifiers => (CtrlBox.IsChecked == true ? HotkeyModifiers.Control : 0)
        | (ShiftBox.IsChecked == true ? HotkeyModifiers.Shift : 0)
        | (AltBox.IsChecked == true ? HotkeyModifiers.Alt : 0)
        | (WinBox.IsChecked == true ? HotkeyModifiers.Win : 0);

    public uint VirtualKey => (KeyList.SelectedItem as KeyEntry)?.Vk ?? 0;

    public string Combo => VirtualKey == 0 ? string.Empty : HotkeyDisplay.Format(Modifiers, VirtualKey);

    private static bool Matches(KeyEntry key, string search)
    {
        static string Squash(string t) => t.Replace(" ", string.Empty).ToLowerInvariant();
        var needle = Squash(search ?? string.Empty);
        return needle.Length == 0 || Squash(key.Label).Contains(needle) || Squash(key.Group).Contains(needle);
    }

    private void UpdatePreview()
    {
        ComboPreview.Text = Combo;
        OkButton.IsEnabled = VirtualKey != 0;
    }

    private static List<KeyEntry> BuildKeys()
    {
        var keys = new List<KeyEntry>();
        void Add(string group, uint vk) => keys.Add(new KeyEntry(HotkeyDisplay.Format(HotkeyModifiers.None, vk), vk, group));

        for (uint vk = 0x70; vk <= 0x87; vk++) Add(Strings.KeyPicker_GroupFunction, vk);
        for (uint vk = 'A'; vk <= 'Z'; vk++) Add(Strings.KeyPicker_GroupLetters, vk);
        for (uint vk = '0'; vk <= '9'; vk++) Add(Strings.KeyPicker_GroupDigits, vk);
        foreach (var vk in new uint[] { 0x1B, 0x09, 0x0D, 0x20, 0x08, 0x2D, 0x2E, 0x24, 0x23, 0x21, 0x22, 0x25, 0x26, 0x27, 0x28,
                                        0x2C, 0x13, 0x91, 0x90, 0x14, 0x5D })
            Add(Strings.KeyPicker_GroupNavigation, vk);
        for (uint vk = 0x60; vk <= 0x6F; vk++) Add(Strings.KeyPicker_GroupNumpad, vk);
        foreach (var vk in new uint[] { 0xBA, 0xBB, 0xBC, 0xBD, 0xBE, 0xBF, 0xC0, 0xDB, 0xDC, 0xDD, 0xDE, 0xE2 })
            Add(Strings.KeyPicker_GroupPunctuation, vk);
        foreach (var (vk, _) in HotkeyDisplay.MediaKeys) Add(Strings.KeyPicker_GroupMedia, vk);
        foreach (var (vk, _) in HotkeyDisplay.MouseButtons) Add(Strings.KeyPicker_GroupMouse, vk);
        return keys;
    }
}
