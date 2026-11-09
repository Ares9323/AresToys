using System.Windows;
using Wpf.Ui.Controls;

namespace AresToys.App.Views;

/// <summary>Small reusable single-line text prompt (name a layout preset, rename one, …). The
/// caller passes an already-localized window title + field label; <see cref="Result"/> is the
/// trimmed input on OK, or null on Cancel / empty input.</summary>
public partial class TextPromptDialog : FluentWindow
{
    public TextPromptDialog(string title, string label, string initialText = "")
    {
        InitializeComponent();
        Title = title;
        LabelText.Text = label;
        InputBox.Text = initialText;
        Loaded += (_, _) =>
        {
            InputBox.Focus();
            InputBox.SelectAll();
        };
    }

    /// <summary>Trimmed input when confirmed with a non-empty value; null when cancelled.</summary>
    public string? Result { get; private set; }

    private void OnOkClicked(object sender, RoutedEventArgs e)
    {
        var text = (InputBox.Text ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            InputBox.Focus();
            return;
        }
        Result = text;
        DialogResult = true;
        Close();
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
