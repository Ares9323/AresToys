using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace AresToys.ShellMenu;

/// <summary>The two entry points dllhost calls on an in-process COM server, and the class factory they hand out.</summary>
internal static unsafe class Exports
{
    /// <summary>The CLSID of each menu entry and the registry verb it mirrors; AppxManifest.xml names the same ones.</summary>
    private static readonly Dictionary<Guid, VerbSource> Commands = new()
    {
        [new Guid("3CF8552D-F822-4105-A28D-B0AB7443049C")] = VerbSource.AresToysItems,
        [new Guid("46AA68D6-AC05-419F-AB0C-CF53BED3D892")] = VerbSource.AresToysBackground,
        // In Package\NewPlus\AppxManifest.xml, the second package.
        [new Guid("D66809E6-0223-423E-9B30-C06804512E90")] = VerbSource.NewPlus,
    };

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
