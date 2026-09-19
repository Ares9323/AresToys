using System.Linq;
using System.Text.Json.Nodes;
using AresToys.App.Services.Launcher;
using AresToys.App.Services.PipelineTasks;
using AresToys.App.ViewModels;
using Xunit;

namespace AresToys.App.Tests.Launcher;

/// <summary>The workflow editor writes config under the keys the catalog declares, and the task
/// reads config under the keys it hard-codes. Nothing in the compiler ties the two together, so
/// a rename on one side would silently produce a step whose controls do nothing. These tests are
/// that tie.</summary>
public sealed class LaunchAppCatalogContractTests
{
    private static WorkflowActionDescriptor Descriptor =>
        WorkflowActionCatalog.All.Single(d => d.TaskId == LaunchAppTask.TaskId);

    [Theory]
    [InlineData("path")]
    [InlineData("args")]
    [InlineData("workingDir")]
    [InlineData("windowMode")]
    public void TheEditorOffersTheStringParametersTheTaskReads(string key)
    {
        Assert.Contains(Descriptor.StringParameters ?? [], p => p.Key == key);
    }

    [Fact]
    public void TheEditorOffersTheRunAsAdministratorToggle()
    {
        Assert.Contains(Descriptor.BoolParameters ?? [], p => p.Key == "runAsAdmin");
    }

    [Fact]
    public void PickingAShortcutForThePathUnwrapsIt()
    {
        var path = (Descriptor.StringParameters ?? []).Single(p => p.Key == "path");
        Assert.True(path.UnwrapShortcut);
        Assert.Equal(StringPickerKind.File, path.Picker);
    }

    [Fact]
    public void TheDefaultConfigCarriesEveryKeyTheStepCanSet()
    {
        var config = JsonNode.Parse(Descriptor.DefaultConfigJson!)!.AsObject();

        Assert.True(config.ContainsKey("windowMode"));
        Assert.True(config.ContainsKey("runAsAdmin"));
        // Defaults must be the neutral ones, so adding the step changes no behaviour until the
        // user asks for it.
        Assert.Equal(nameof(LauncherWindowMode.Normal), (string?)config["windowMode"]);
        Assert.False((bool?)config["runAsAdmin"]);
    }

    [Fact]
    public void EveryWindowModeTheDropdownOffersIsOneTheTaskUnderstands()
    {
        // The dropdown is populated from Enum.GetNames<LauncherWindowMode>() (see App.xaml.cs);
        // each name has to survive the round-trip into a real window style.
        foreach (var name in Enum.GetNames<LauncherWindowMode>())
        {
            var psi = LaunchAppTask.BuildStartInfo(@"C:\Windows\notepad.exe", "", "", name, false);
            var expected = name switch
            {
                nameof(LauncherWindowMode.Maximized) => System.Diagnostics.ProcessWindowStyle.Maximized,
                nameof(LauncherWindowMode.Minimized) => System.Diagnostics.ProcessWindowStyle.Minimized,
                nameof(LauncherWindowMode.Hidden) => System.Diagnostics.ProcessWindowStyle.Hidden,
                _ => System.Diagnostics.ProcessWindowStyle.Normal,
            };
            Assert.Equal(expected, psi.WindowStyle);
        }
    }

    [Fact]
    public void EveryWindowModeHasALocalisedLabelInBothLanguages()
    {
        // LocalizeOptionsAsEnum resolves EnumValue_<name>; a missing key silently falls back to
        // the raw English name, which is exactly the regression this catches.
        foreach (var name in Enum.GetNames<LauncherWindowMode>())
        {
            foreach (var culture in new[] { "en", "it" })
            {
                var value = AresToys.App.Resources.Strings.ResourceManager.GetString(
                    "EnumValue_" + name, new System.Globalization.CultureInfo(culture));
                Assert.False(string.IsNullOrWhiteSpace(value), $"EnumValue_{name} missing for '{culture}'");
            }
        }
    }
}
