using AresToys.App.Services.Launcher;
using Xunit;

namespace AresToys.App.Tests.Launcher;

/// <summary>Smoke test for the shell round-trip in <see cref="PackagedAppPath.TryGetDisplayName"/>.
/// Environment-dependent by nature — it needs a packaged app actually installed — so it verifies
/// the contract only when the shell resolves the id, and no-ops otherwise rather than failing on
/// a machine without Calculator. The pure shape/normalisation logic is covered exhaustively in
/// <see cref="PackagedAppPathTests"/>.</summary>
public sealed class PackagedAppDisplayNameTests
{
    private const string Calculator = "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App";

    [Fact]
    public void ResolvesAFriendlyNameForAnInstalledPackagedApp()
    {
        var name = PackagedAppPath.TryGetDisplayName(Calculator);
        if (name is null) return;   // app not installed on this machine — nothing to assert

        // The point of the lookup: a human label, not the id we fed in.
        Assert.NotEqual(Calculator, name);
        Assert.DoesNotContain("!", name, StringComparison.Ordinal);
        Assert.DoesNotContain("_8wekyb3d8bbwe", name, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(name));
    }

    [Fact]
    public void GarbageIdsResolveToNullRatherThanThrowing()
    {
        Assert.Null(PackagedAppPath.TryGetDisplayName("NotAnApp_zzzzzzzzzzzzz!App"));
        Assert.Null(PackagedAppPath.TryGetDisplayName(@"C:\Windows\notepad.exe"));
        Assert.Null(PackagedAppPath.TryGetDisplayName(null));
        Assert.Null(PackagedAppPath.TryGetDisplayName(""));
    }
}
