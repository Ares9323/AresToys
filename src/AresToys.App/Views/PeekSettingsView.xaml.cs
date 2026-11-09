using System.Windows.Controls;

namespace AresToys.App.Views;

/// <summary>The Peek sidebar section: module switch, "Open in" programs and extension associations. All the logic
/// is in <see cref="ViewModels.PeekSettingsViewModel"/> and the module switch in <see cref="ViewModels.SettingsViewModel"/>.</summary>
public partial class PeekSettingsView : UserControl
{
    public PeekSettingsView() => InitializeComponent();
}
