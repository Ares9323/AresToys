using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace AresToys.App.Services.Peek;

/// <summary>The COM side of a Windows preview handler, shared by the in-process path of <see cref="PeekShellPreview"/>
/// (Office handlers, which run as their own local server) and by the Low integrity helper
/// (<see cref="PeekHandlerHost"/>): the interfaces, the rectangle and the way a handler is given its file.</summary>
internal static class PeekHandlerCom
{
    internal const uint StgmRead = 0;
    private const int EAccessDenied = unchecked((int)0x80030005);
    private const int ENotImpl = unchecked((int)0x80004001);

    /// <summary>Gives the handler its file through the first initialisation interface it offers: stream, item, file.
    /// False when it offers none. A stream is opened over the file and returned in <paramref name="stream"/>: the caller
    /// disposes it when the handler is unloaded.</summary>
    internal static bool Initialize(object handler, string path, out FileStream? stream)
    {
        stream = null;
        if (handler is IInitializeWithStream withStream)
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            withStream.Initialize(new ComStream(stream), StgmRead);
        }
        else if (handler is IInitializeWithItem withItem)
        {
            var itemIid = typeof(IShellItem).GUID;
            Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(path, 0, ref itemIid, out var item));
            withItem.Initialize(item, StgmRead);
        }
        else if (handler is IInitializeWithFile withFile)
        {
            withFile.Initialize(path, StgmRead);
        }
        else
        {
            return false;
        }

        return true;
    }

    /// <summary>A read only IStream over a file stream, handed to handlers that initialise from a stream.</summary>
    private sealed class ComStream(Stream stream) : IStream
    {
        public void Read(byte[] pv, int cb, nint pcbRead)
        {
            var read = stream.Read(pv, 0, cb);
            if (pcbRead != 0)
            {
                Marshal.WriteInt32(pcbRead, read);
            }
        }

        public void Seek(long dlibMove, int dwOrigin, nint plibNewPosition)
        {
            // STREAM_SEEK_SET, _CUR and _END have the values of SeekOrigin.Begin, Current and End.
            var position = stream.Seek(dlibMove, (SeekOrigin)dwOrigin);
            if (plibNewPosition != 0)
            {
                Marshal.WriteInt64(plibNewPosition, position);
            }
        }

        public void Stat(out STATSTG pstatstg, int grfStatFlag)
        {
            pstatstg = new STATSTG { type = 2, cbSize = stream.Length, grfMode = (int)StgmRead };
        }

        public void Write(byte[] pv, int cb, nint pcbWritten) => Marshal.ThrowExceptionForHR(EAccessDenied);

        public void SetSize(long libNewSize) => Marshal.ThrowExceptionForHR(EAccessDenied);

        public void CopyTo(IStream pstm, long cb, nint pcbRead, nint pcbWritten) => Marshal.ThrowExceptionForHR(ENotImpl);

        public void Commit(int grfCommitFlags) => Marshal.ThrowExceptionForHR(ENotImpl);

        public void Revert() => Marshal.ThrowExceptionForHR(ENotImpl);

        public void LockRegion(long libOffset, long cb, int dwLockType) => Marshal.ThrowExceptionForHR(ENotImpl);

        public void UnlockRegion(long libOffset, long cb, int dwLockType) => Marshal.ThrowExceptionForHR(ENotImpl);

        public void Clone(out IStream ppstm)
        {
            Marshal.ThrowExceptionForHR(ENotImpl);
            ppstm = this;
        }
    }

    [ComImport, Guid("8895b1c6-b41f-4c1c-a562-0d564250836f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPreviewHandler
    {
        void SetWindow(nint hwnd, ref Rect32 rect);

        void SetRect(ref Rect32 rect);

        void DoPreview();

        void Unload();

        void SetFocus();

        void QueryFocus(out nint phwnd);

        [PreserveSig]
        int TranslateAccelerator(nint pmsg);
    }

    [ComImport, Guid("b824b49d-22ac-4161-ac8a-9916e8fa3f7f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IInitializeWithStream
    {
        void Initialize(IStream pstream, uint grfMode);
    }

    [ComImport, Guid("7f73be3f-fb79-493c-a6c7-7ee14e245841"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IInitializeWithItem
    {
        void Initialize(IShellItem psi, uint grfMode);
    }

    [ComImport, Guid("b7d14566-0509-4cce-a71f-0a554233bd9b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IInitializeWithFile
    {
        void Initialize([MarshalAs(UnmanagedType.LPWStr)] string pszFilePath, uint grfMode);
    }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellItem
    {
        void BindToHandler(nint pbc, ref Guid bhid, ref Guid riid, out nint ppv);

        void GetParent(out IShellItem ppsi);

        void GetDisplayName(uint sigdnName, out nint ppszName);

        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);

        void Compare(IShellItem psi, uint hint, out int piOrder);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect32
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string pszPath, nint pbc, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);
}
