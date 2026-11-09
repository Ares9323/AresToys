using AresToys.App.Services.NewPlus;
using Xunit;

namespace AresToys.App.Tests;

public sealed class NewPlusDialogAddressTests
{
    private static readonly (string Name, string Path)[] Known =
    [
        ("Desktop", @"C:\Users\Ares\Desktop"),
        ("Scrivania", @"C:\Users\Ares\Desktop"),
        ("Documents", @"C:\Users\Ares\Documents"),
    ];

    private static readonly HashSet<string> Existing = new(StringComparer.OrdinalIgnoreCase)
    {
        @"C:\Users\Ares\Downloads", @"C:\Users\Ares\Desktop", @"C:\Users\Ares\Desktop\sub",
        @"C:\Users\Ares\Documents", @"C:\Users\Ares\Documents\Work", @"\\nas\share",
    };

    private static string? Resolve(string text) => NewPlusFolderShortcut.ResolveAddress(text, () => Known, Existing.Contains);

    [Theory]
    [InlineData(@"Address: C:\Users\Ares\Downloads", @"C:\Users\Ares\Downloads")]
    [InlineData(@"Indirizzo: \\nas\share", @"\\nas\share")]
    [InlineData("Address: Desktop", @"C:\Users\Ares\Desktop")]
    [InlineData("Indirizzo: Scrivania", @"C:\Users\Ares\Desktop")]
    [InlineData(@"Address: Desktop\sub", @"C:\Users\Ares\Desktop\sub")]
    [InlineData(@"Address: Libraries\Documents\Work", @"C:\Users\Ares\Documents\Work")]
    [InlineData("Address: This PC", null)]
    [InlineData("Navigation buttons", null)]
    [InlineData(@"Address: Desktop\missing", null)]
    public void Resolves_the_folder_an_address_bar_names(string text, string? expected)
        => Assert.Equal(expected, Resolve(text));
}
