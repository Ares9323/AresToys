using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace AresToys.ShellMenu;

// The shell COM interfaces the Windows 11 context menu speaks, declared for the ComWrappers source generator
// (NativeAOT has no built-in COM). Every method keeps its HRESULT (PreserveSig) so E_NOTIMPL reaches Explorer as is.

internal static class HResult
{
    public const int S_OK = 0;
    public const int S_FALSE = 1;
    public const int E_NOTIMPL = unchecked((int)0x80004001);
    public const int E_NOINTERFACE = unchecked((int)0x80004002);
    public const int E_POINTER = unchecked((int)0x80004003);
    public const int E_FAIL = unchecked((int)0x80004005);
    public const int CLASS_E_NOAGGREGATION = unchecked((int)0x80040110);
    public const int CLASS_E_CLASSNOTAVAILABLE = unchecked((int)0x80040111);
}

[GeneratedComInterface]
[Guid("00000001-0000-0000-C000-000000000046")]
internal partial interface IClassFactory
{
    [PreserveSig]
    int CreateInstance(nint outer, in Guid riid, out nint instance);

    [PreserveSig]
    int LockServer([MarshalAs(UnmanagedType.Bool)] bool lockServer);
}

[GeneratedComInterface]
[Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
internal partial interface IShellItem
{
    [PreserveSig]
    int BindToHandler(nint bindContext, in Guid handler, in Guid riid, out nint result);

    [PreserveSig]
    int GetParent(out nint parent);

    [PreserveSig]
    int GetDisplayName(uint sigdnName, out nint name);

    [PreserveSig]
    int GetAttributes(uint mask, out uint attributes);

    [PreserveSig]
    int Compare(nint other, uint hint, out int order);
}

[GeneratedComInterface]
[Guid("b63ea76d-1f85-456f-a19c-48159efa858b")]
internal partial interface IShellItemArray
{
    [PreserveSig]
    int BindToHandler(nint bindContext, in Guid handler, in Guid riid, out nint result);

    [PreserveSig]
    int GetPropertyStore(int flags, in Guid riid, out nint result);

    [PreserveSig]
    int GetPropertyDescriptionList(nint key, in Guid riid, out nint result);

    [PreserveSig]
    int GetAttributes(int attribFlags, uint mask, out uint attributes);

    [PreserveSig]
    int GetCount(out uint count);

    [PreserveSig]
    int GetItemAt(uint index, out IShellItem? item);

    [PreserveSig]
    int EnumItems(out nint enumerator);
}

[GeneratedComInterface]
[Guid("a08ce4d0-fa25-44ab-b57c-c7b1c323e0b9")]
internal partial interface IExplorerCommand
{
    [PreserveSig]
    int GetTitle(IShellItemArray? items, out nint name);

    [PreserveSig]
    int GetIcon(IShellItemArray? items, out nint icon);

    [PreserveSig]
    int GetToolTip(IShellItemArray? items, out nint infoTip);

    [PreserveSig]
    int GetCanonicalName(out Guid commandName);

    [PreserveSig]
    int GetState(IShellItemArray? items, [MarshalAs(UnmanagedType.Bool)] bool okToBeSlow, out uint commandState);

    [PreserveSig]
    int Invoke(IShellItemArray? items, nint bindContext);

    [PreserveSig]
    int GetFlags(out uint flags);

    [PreserveSig]
    int EnumSubCommands(out IEnumExplorerCommand? enumerator);
}

[GeneratedComInterface]
[Guid("a88826f8-186f-4987-aade-ea0cef8fbfe8")]
internal unsafe partial interface IEnumExplorerCommand
{
    [PreserveSig]
    int Next(uint count, nint* commands, uint* fetched);

    [PreserveSig]
    int Skip(uint count);

    [PreserveSig]
    int Reset();

    [PreserveSig]
    int Clone(out IEnumExplorerCommand? enumerator);
}

[GeneratedComInterface]
[Guid("fc4801a3-2ba9-11cf-a229-00aa003d7352")]
internal partial interface IObjectWithSite
{
    [PreserveSig]
    int SetSite(nint site);

    [PreserveSig]
    int GetSite(in Guid riid, out nint site);
}

internal static class ExplorerCommandFlags
{
    public const uint Default = 0x00;
    public const uint HasSubCommands = 0x01;
    public const uint IsSeparator = 0x08;
}

internal static class ExplorerCommandState
{
    public const uint Enabled = 0x00;
    public const uint Disabled = 0x01;
    public const uint Hidden = 0x02;
}

internal static class Sigdn
{
    public const uint FileSysPath = 0x80058000;
}
