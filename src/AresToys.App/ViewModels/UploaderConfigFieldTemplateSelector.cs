using System.Windows;
using System.Windows.Controls;

namespace AresToys.App.ViewModels;

/// <summary>Picks the right XAML template per field VM type. Lives in the VM namespace because
/// it's purely about VM type discrimination — the templates themselves are declared in
/// <c>UploaderConfigDialog.xaml</c> and passed in as properties so the selector stays UI-agnostic
/// and won't pull view types into the VM project if it ever gets split out.</summary>
public sealed class UploaderConfigFieldTemplateSelector : DataTemplateSelector
{
    public DataTemplate? StringTemplate { get; set; }
    public DataTemplate? SensitiveStringTemplate { get; set; }
    public DataTemplate? BoolTemplate { get; set; }
    public DataTemplate? DropdownTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object? item, DependencyObject container) => item switch
    {
        StringFieldViewModel s when s.Sensitive => SensitiveStringTemplate,
        StringFieldViewModel                    => StringTemplate,
        BoolFieldViewModel                      => BoolTemplate,
        DropdownFieldViewModel                  => DropdownTemplate,
        _ => base.SelectTemplate(item, container),
    };
}
