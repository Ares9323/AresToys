using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace AresToys.ShellMenu;

/// <summary>The two entry points dllhost calls on an in-process COM server, and the class factory they hand out.</summary>
internal static unsafe class Exports
{
    /// <summary>The slot packages' CLSIDs, slot 1 first: files and folders, then the folder background. The csproj's
    /// _Slot items name the same ones (Package\Slot\AppxManifest.xml is their template).</summary>
    private static readonly (string Items, string Background)[] SlotClsids =
    [
        ("057823C3-9F2E-4A8B-B795-FD44E2421C8B", "CACEC6F7-08AF-4B6F-8C1D-5EADA6E09D03"),
        ("69F3035C-D182-414A-9240-86BDB6E4A6D4", "68E93DC8-95AB-4785-B4D8-EB189A427EC1"),
        ("AF13E83B-C60C-40EC-AA66-CF4F25231A87", "4ECBC814-8F11-49B5-A0D4-1EE472A366BE"),
        ("892C049A-E41F-492B-857A-4A8E9BEAA8C3", "C0B91EA2-1332-4BB5-9D2E-1ED1291952D2"),
        ("55183CA2-2055-44D2-B816-0787C2FD7096", "FD06F9EF-D36E-46A2-995B-115E1DB9BBB4"),
        ("4C1CAFC7-590F-449A-B8B6-F79BA3504F19", "C527D6F2-2811-4FD9-A87B-D0CC4036E3F8"),
        ("E3F8DC07-4A5E-40F7-9316-E84978DAF0E2", "B292A32E-07BB-483F-AEA6-999646874BDD"),
        ("BB53DCF2-90EE-49CB-867A-A3FBC96E4E7D", "47F8E263-CB0B-4C9B-9223-BEEC8699D8BA"),
    ];

    /// <summary>The CLSID of each menu entry and the registry verb it mirrors; the AppxManifest.xml files name the same
    /// ones. After SlotClsids: static fields initialize in declaration order.</summary>
    private static readonly Dictionary<Guid, VerbSource> Commands = BuildCommands();

    private static Dictionary<Guid, VerbSource> BuildCommands()
    {
        var commands = new Dictionary<Guid, VerbSource>
        {
            [new Guid("3CF8552D-F822-4105-A28D-B0AB7443049C")] = VerbSource.AresToysItems,
            [new Guid("46AA68D6-AC05-419F-AB0C-CF53BED3D892")] = VerbSource.AresToysBackground,
            // In Package\NewPlus\AppxManifest.xml, the second package.
            [new Guid("D66809E6-0223-423E-9B30-C06804512E90")] = VerbSource.NewPlus,
        };
        for (var i = 0; i < SlotClsids.Length; i++)
        {
            commands[new Guid(SlotClsids[i].Items)] = VerbSource.SlotItems(i + 1);
            commands[new Guid(SlotClsids[i].Background)] = VerbSource.SlotBackground(i + 1);
        }

        return commands;
    }

    internal static readonly StrategyBasedComWrappers ComWrappers = new();

    [UnmanagedCallersOnly(EntryPoint = "DllGetClassObject")]
    private static int DllGetClassObject(Guid* clsid, Guid* riid, nint* ppv)
    {
        if (ppv is null) return HResult.E_POINTER;
        *ppv = 0;
        if (clsid is null || riid is null) return HResult.E_POINTER;
        if (!Commands.TryGetValue(*clsid, out var source)) return HResult.CLASS_E_CLASSNOTAVAILABLE;
        try
        {
            return QueryInterface(new ClassFactory(source), *riid, ppv);
        }
        catch (Exception ex)
        {
            return ex.HResult;
        }
    }

    // Explorer keeps the surrogate alive only while it uses the menu; refusing to unload keeps it simple and safe.
    [UnmanagedCallersOnly(EntryPoint = "DllCanUnloadNow")]
    private static int DllCanUnloadNow() => HResult.S_FALSE;

    internal static int QueryInterface(object instance, Guid riid, nint* ppv)
    {
        var unknown = ComWrappers.GetOrCreateComInterfaceForObject(instance, CreateComInterfaceFlags.None);
        try
        {
            return Marshal.QueryInterface(unknown, in riid, out *ppv);
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }
}

[GeneratedComClass]
internal sealed unsafe partial class ClassFactory(VerbSource source) : IClassFactory
{
    public int CreateInstance(nint outer, in Guid riid, out nint instance)
    {
        instance = 0;
        if (outer != 0) return HResult.CLASS_E_NOAGGREGATION;
        try
        {
            nint result;
            var hr = Exports.QueryInterface(new RegistryVerbCommand(source), riid, &result);
            instance = result;
            return hr;
        }
        catch (Exception ex)
        {
            return ex.HResult;
        }
    }

    public int LockServer(bool lockServer) => HResult.S_OK;
}
