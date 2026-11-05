using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace AresToys.App.Services.Peek;

/// <summary>A child window that hosts one Windows preview handler (the File Explorer preview pane's handlers: PDF,
/// Office, and whatever else registers one, such as the AresExplorerTools formats). The handler runs out of process
/// in prevhost.exe and draws into this window. One handler at a time: <see cref="Load"/> replaces it,
/// <see cref="Unload"/> ends it. UI thread (STA) only.
/// <para>Two ways to host. A handler that is a local server of its own (the Office ones, see
/// <see cref="PeekPreview.RunsAsLocalServer"/>) is created from this process, and the class factory of every one
/// used is kept for the process life because it makes the next load fast (<see cref="ReleaseFactories"/> lets them
/// go). Every other handler is a DLL that Windows runs in a prevhost.exe surrogate, which a Medium integrity
/// process cannot start: it is created by <see cref="PeekHandlerHost"/>, a helper copy of this exe running at Low
/// integrity, and the helper's window is reparented into this one. The helper starts with the first such load, is
/// reused for the next files and ends with <see cref="StopHelper"/> (when Peek hides) or when this process ends.</para></summary>
internal sealed class PeekShellPreview : HwndHost
{
    private const string ClassName = "AresToysPeekShellPreview";
    private const uint ClsctxLocalServer = 4;
    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private const uint SwpShowWindow = 0x0040;
    private const int SwHide = 0;
    private static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StartAllowance = TimeSpan.FromSeconds(3);
    private const int WsChild = 0x40000000;
    private const int WsVisible = 0x10000000;
    private const int WsClipSiblings = 0x04000000;
    private const int WsClipChildren = 0x02000000;
    private const int WsExLayered = 0x00080000;
    private const uint LwaAlpha = 2;
    private const int GclpHbrBackground = -10;
    private const uint ShgfiTypeName = 0x400;
    private const uint ShgfiUseFileAttributes = 0x10;
    private const uint FileAttributeNormal = 0x80;

    private static readonly Dictionary<Guid, IClassFactory> Factories = new();
    private static bool _classRegistered;
    private static nint _backgroundBrush;
    private static Helper? _helper;

    private nint _hwnd;
    private PeekHandlerCom.IPreviewHandler? _handler;
    private nint _helperWindow;
    private FileStream? _stream;
    private Color _background = Color.FromRgb(0x1E, 0x1E, 0x1E);
    private int _width;
    private int _height;

    /// <summary>The colour shown behind and around the handler's own window.</summary>
    public Color BackgroundColor
    {
        get => _background;
        set
        {
            _background = value;
            if (_hwnd != 0)
            {
                ApplyBackground();
            }
        }
    }

    /// <summary>True when the keyboard focus is in this window or in the handler's window inside it.</summary>
    public bool HasFocusWithin
    {
        get
        {
            if (_hwnd == 0) return false;
            var focus = GetFocus();
            return focus != 0 && (focus == _hwnd || IsChild(_hwnd, focus));
        }
    }

    /// <summary>Starts the handler registered for the file (<see cref="PeekPreview.PreviewHandlerFor"/>) inside this
    /// window and previews the file. False, with everything cleaned up, when no handler could be created or
    /// initialised (the host process died, the file is unreadable, the handler cannot take a file).</summary>
    public bool Load(string path)
    {
        Unload();
        if (_hwnd == 0 || PeekPreview.PreviewHandlerFor(path) is not { } clsid)
        {
            return false;
        }

        return PeekPreview.RunsAsLocalServer(clsid) ? LoadInProcess(path, clsid) : LoadThroughHelper(path, clsid);
    }

    /// <summary>Ends the current handler: takes the helper's window out of this one, tells the handler to unload,
    /// releases it and closes the file stream. The helper process, if any, stays for the next file.</summary>
    public void Unload()
    {
        if (_helperWindow != 0)
        {
            // Out of sight and out of this window first, so no orphan window is left on screen whatever the helper does.
            var window = _helperWindow;
            _helperWindow = 0;
            ShowWindow(window, SwHide);
            SetParent(window, 0);
            _helper?.Send("unload");
        }

        if (_handler is { } handler)
        {
            _handler = null;
            try
            {
                handler.Unload();
            }
            catch (COMException ex)
            {
                // The handler's host process is already gone: nothing is left to unload, only the proxy to release.
                System.Diagnostics.Debug.WriteLine("Peek: preview handler Unload failed: " + ex.Message);
            }
            finally
            {
                Marshal.FinalReleaseComObject(handler);
            }
        }

        _stream?.Dispose();
        _stream = null;
    }

    /// <summary>Releases the cached class factories and ends the helper (end of the process or of the window).</summary>
    public static void ReleaseFactories()
    {
        StopHelper();
        foreach (var factory in Factories.Values)
        {
            ReleaseFactory(factory);
        }

        Factories.Clear();
    }

    /// <summary>Ends the helper process, if one runs (Peek hid, or the process ends). Call after <see cref="Unload"/>.
    /// The next helper load starts a new one.</summary>
    public static void StopHelper()
    {
        _helper?.Dispose();
        _helper = null;
    }

    /// <summary>A handler that is a local server of its own: created from this process, drawn into this window.</summary>
    private bool LoadInProcess(string path, Guid clsid)
    {
        try
        {
            var handler = CreateHandler(clsid);
            if (handler is null)
            {
                return false;
            }

            _handler = handler;
            if (!PeekHandlerCom.Initialize(handler, path, out _stream))
            {
                Unload();
                return false;
            }

            var rect = new PeekHandlerCom.Rect32 { Right = _width, Bottom = _height };
            handler.SetWindow(_hwnd, ref rect);
            handler.DoPreview();
            return true;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or IOException or UnauthorizedAccessException)
        {
            Unload();
            return false;
        }
    }

    /// <summary>A handler that runs in prevhost.exe: the Low integrity helper creates it and previews the file in a
    /// window of its own, which is then made a child of this one. False when the helper cannot start, dies, does not
    /// answer in time (it is then ended, the next load starts a new one) or reports that the handler failed.</summary>
    private bool LoadThroughHelper(string path, Guid clsid)
    {
        var helper = _helper;
        var fresh = false;
        if (helper is null || !helper.IsAlive)
        {
            StopHelper();
            helper = _helper = Helper.Start();
            fresh = true;
        }

        if (helper is null)
        {
            return false;
        }

        var colour = _background.R | (_background.G << 8) | (_background.B << 16);
        if (!helper.Send("bg " + colour.ToString("X", System.Globalization.CultureInfo.InvariantCulture))
            || !helper.Send($"resize {Math.Max(1, _width)} {Math.Max(1, _height)}")
            || !helper.Send($"load {clsid:B} {path}"))
        {
            StopHelper();
            return false;
        }

        var reply = helper.ReadReply(fresh ? LoadTimeout + StartAllowance : LoadTimeout);
        if (reply is null)
        {
            StopHelper();
            return false;
        }

        var parts = reply.Split(' ', 2);
        if (parts[0] != "loaded" || parts.Length != 2 || !long.TryParse(parts[1], out var window) || window == 0)
        {
            // "failed <hr>": the handler could not show this file, the helper is fine and stays.
            return false;
        }

        if (!Attach((nint)window))
        {
            helper.Send("unload");
            return false;
        }

        _helperWindow = (nint)window;
        return true;
    }

    /// <summary>Makes the helper's window a child of this window, filling it. The helper is Low integrity and this
    /// process Medium, so this direction (a lower integrity window under a higher one, set from the higher one) is
    /// the one Windows allows.</summary>
    private bool Attach(nint window)
    {
        // WS_CHILD goes on before the new parent is set, as SetParent documents for a window that was top level.
        SetWindowLongPtrW(window, GwlStyle, WsChild | WsVisible | WsClipSiblings | WsClipChildren);
        SetWindowLongPtrW(window, GwlExStyle, 0);
        SetParent(window, _hwnd);
        if (GetParent(window) != _hwnd)
        {
            return false;
        }

        SetWindowPos(window, 0, 0, 0, Math.Max(1, _width), Math.Max(1, _height),
            SwpNoZOrder | SwpNoActivate | SwpFrameChanged | SwpShowWindow);
        return true;
    }

    /// <summary>The footer text of a hosted preview: the Windows type name of the file and its size.</summary>
    public static string FormatInfo(string path)
    {
        var parts = new List<string>(2);
        var info = new ShFileInfo();
        if (SHGetFileInfoW(path, FileAttributeNormal, ref info, (uint)Marshal.SizeOf<ShFileInfo>(), ShgfiTypeName | ShgfiUseFileAttributes) != 0
            && !string.IsNullOrEmpty(info.TypeName))
        {
            parts.Add(info.TypeName);
        }

        long? size;
        try
        {
            size = new FileInfo(path).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The file vanished or is unreadable: the footer omits the size.
            size = null;
        }

        if (size is { } bytes)
        {
            parts.Add(PeekWebContent.FormatBytes(bytes));
        }

        return string.Join("  ·  ", parts);
    }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        RegisterWindowClass(_background);
        _hwnd = CreateWindowExW(WsExLayered, ClassName, null, WsChild | WsVisible | WsClipSiblings | WsClipChildren,
            0, 0, 0, 0, hwndParent.Handle, 0, 0, 0);
        if (_hwnd == 0)
        {
            throw new InvalidOperationException("The Peek shell preview window could not be created.");
        }

        // A layered child is what lets the preview handlers paint properly (as in PowerToys Peek).
        SetLayeredWindowAttributes(_hwnd, 0, 255, LwaAlpha);
        ApplyBackground();
        return new HandleRef(this, _hwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        Unload();
        DestroyWindow(hwnd.Handle);
        _hwnd = 0;
    }

    /// <summary>HwndHost already moved the window; the handler gets its new size in device pixels.</summary>
    protected override void OnWindowPositionChanged(Rect rcBoundingBox)
    {
        base.OnWindowPositionChanged(rcBoundingBox);
        _width = (int)Math.Round(rcBoundingBox.Width);
        _height = (int)Math.Round(rcBoundingBox.Height);
        if (_helperWindow != 0 && _helper is { } helper)
        {
            // The helper's window is a child of this one: sized here, and the handler inside it told by the helper.
            MoveWindow(_helperWindow, 0, 0, Math.Max(1, _width), Math.Max(1, _height), true);
            if (!helper.Send($"resize {Math.Max(1, _width)} {Math.Max(1, _height)}"))
            {
                System.Diagnostics.Debug.WriteLine("Peek: the preview helper no longer takes commands.");
            }
        }

        if (_handler is { } handler)
        {
            var rect = new PeekHandlerCom.Rect32 { Right = _width, Bottom = _height };
            try
            {
                handler.SetRect(ref rect);
            }
            catch (COMException ex)
            {
                System.Diagnostics.Debug.WriteLine("Peek: preview handler SetRect failed: " + ex.Message);
            }
        }
    }

    private void ApplyBackground()
    {
        var brush = CreateSolidBrush(_background.R | (_background.G << 8) | (_background.B << 16));
        var old = (nint)SetClassLongPtrW(_hwnd, GclpHbrBackground, brush);
        _backgroundBrush = brush;
        if (old != 0)
        {
            DeleteObject(old);
        }
    }

    private static void RegisterWindowClass(Color background)
    {
        if (_classRegistered) return;

        _backgroundBrush = CreateSolidBrush(background.R | (background.G << 8) | (background.B << 16));
        var wndProc = NativeLibrary.GetExport(NativeLibrary.Load("user32.dll"), "DefWindowProcW");
        var wndClass = new WndClassW
        {
            WndProc = wndProc,
            ClassName = ClassName,
            Background = _backgroundBrush,
        };
        if (RegisterClassW(ref wndClass) == 0)
        {
            throw new InvalidOperationException("The Peek shell preview window class could not be registered.");
        }

        _classRegistered = true;
    }

    /// <summary>Creates the handler through the cached class factory (the handler runs in prevhost.exe). When the
    /// factory's process has died, the call fails once: the factory is dropped and fetched again, one retry.</summary>
    private static PeekHandlerCom.IPreviewHandler? CreateHandler(Guid clsid)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (!Factories.TryGetValue(clsid, out var factory))
            {
                var id = clsid;
                var factoryIid = typeof(IClassFactory).GUID;
                var hr = CoGetClassObject(ref id, ClsctxLocalServer, 0, ref factoryIid, out var instance);
                if (hr < 0)
                {
                    return null;
                }

                factory = (IClassFactory)instance;
                factory.LockServer(true);
                Factories[clsid] = factory;
            }

            try
            {
                var handlerIid = typeof(PeekHandlerCom.IPreviewHandler).GUID;
                factory.CreateInstance(null, ref handlerIid, out var handler);
                return handler as PeekHandlerCom.IPreviewHandler;
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException)
            {
                // The host process is probably dead: forget this factory so the next pass asks for a fresh one.
                Factories.Remove(clsid);
                ReleaseFactory(factory);
            }
        }

        return null;
    }

    private static void ReleaseFactory(IClassFactory factory)
    {
        try
        {
            factory.LockServer(false);
        }
        catch (COMException ex)
        {
            System.Diagnostics.Debug.WriteLine("Peek: class factory LockServer failed: " + ex.Message);
        }
        finally
        {
            Marshal.FinalReleaseComObject(factory);
        }
    }

    /// <summary>The Low integrity helper process (<see cref="PeekHandlerHost"/>) and the two pipes to it: commands out,
    /// replies in. A thread reads the replies into a queue, so a helper that hangs never blocks a write of ours and a
    /// reply that does not come is a timeout, not a wait.</summary>
    private sealed class Helper : IDisposable
    {
        private readonly nint _process;
        private readonly AnonymousPipeServerStream _toHelper;
        private readonly AnonymousPipeServerStream _fromHelper;
        private readonly StreamWriter _commands;
        private readonly BlockingCollection<string> _replies = new();
        private readonly object _sendLock = new();

        private Helper(nint process, AnonymousPipeServerStream toHelper, AnonymousPipeServerStream fromHelper)
        {
            _process = process;
            _toHelper = toHelper;
            _fromHelper = fromHelper;
            _commands = new StreamWriter(toHelper, new UTF8Encoding(false)) { AutoFlush = true };
            new Thread(ReadReplies) { IsBackground = true, Name = "PeekShellPreview.Helper" }.Start();
        }

        /// <summary>True while the process runs.</summary>
        public bool IsAlive => WaitForSingleObject(_process, 0) == WaitTimeout;

        /// <summary>Starts the helper at Low integrity. Null when the process cannot be created.</summary>
        public static Helper? Start()
        {
            var exe = Environment.ProcessPath;
            if (exe is null)
            {
                return null;
            }

            var toHelper = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
            var fromHelper = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
            var inherited = new[]
            {
                toHelper.ClientSafePipeHandle.DangerousGetHandle(),
                fromHelper.ClientSafePipeHandle.DangerousGetHandle(),
            };
            var commandLine = $"\"{exe}\" {PeekHandlerHost.Arg} {(long)inherited[0]} {(long)inherited[1]}";
            if (!TryCreateLowIntegrityProcess(exe, commandLine, inherited, out var process))
            {
                toHelper.Dispose();
                fromHelper.Dispose();
                return null;
            }

            // The helper has its ends now; ours must go, or its end of input and its death would never be seen.
            toHelper.DisposeLocalCopyOfClientHandle();
            fromHelper.DisposeLocalCopyOfClientHandle();
            return new Helper(process, toHelper, fromHelper);
        }

        /// <summary>Sends one line. False when the pipe is broken (the helper is gone).</summary>
        public bool Send(string line)
        {
            lock (_sendLock)
            {
                try
                {
                    _commands.WriteLine(line);
                    return true;
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                    System.Diagnostics.Debug.WriteLine("Peek: sending to the preview helper failed: " + ex.Message);
                    return false;
                }
            }
        }

        /// <summary>The next reply line, or null when none comes in time or the helper died.</summary>
        public string? ReadReply(TimeSpan timeout) => _replies.TryTake(out var reply, timeout) ? reply : null;

        /// <summary>Asks the helper to quit; one that does not within a moment is terminated.</summary>
        public void Dispose()
        {
            Send("quit");
            if (WaitForSingleObject(_process, 500) != 0)
            {
                TerminateProcess(_process, 1);
            }

            CloseHandle(_process);
            _commands.Dispose();
            _fromHelper.Dispose();
            _toHelper.Dispose();
        }

        private void ReadReplies()
        {
            try
            {
                using var reader = new StreamReader(_fromHelper, new UTF8Encoding(false));
                while (reader.ReadLine() is { } line)
                {
                    _replies.Add(line);
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // The pipe was closed (the helper died or this object was disposed): the queue ends below.
                System.Diagnostics.Debug.WriteLine("Peek: reading the preview helper failed: " + ex.Message);
            }

            _replies.CompleteAdding();
        }

        /// <summary>Creates the process with a copy of this process's token lowered to Low integrity (S-1-16-4096),
        /// the way Windows lets a process start a prevhost.exe preview handler. Only the two pipe ends in
        /// <paramref name="inherited"/> pass to the child.</summary>
        private static bool TryCreateLowIntegrityProcess(string exe, string commandLine, nint[] inherited, out nint process)
        {
            process = 0;
            nint token = 0, lowToken = 0, sid = 0, attributes = 0, handleList = 0;
            try
            {
                if (!OpenProcessToken(GetCurrentProcess(), TokenDuplicate | TokenQuery | TokenAssignPrimary | TokenAdjustDefault, out token)
                    || !DuplicateTokenEx(token, MaximumAllowed, 0, SecurityImpersonation, TokenPrimary, out lowToken)
                    || !ConvertStringSidToSidW("S-1-16-4096", out sid))
                {
                    return false;
                }

                var label = new TokenMandatoryLabel { Sid = sid, Attributes = SeGroupIntegrity };
                if (!SetTokenInformation(lowToken, TokenIntegrityLevel, ref label,
                        (uint)Marshal.SizeOf<TokenMandatoryLabel>() + GetLengthSid(sid)))
                {
                    return false;
                }

                // The attribute list names the handles the child inherits, so nothing else of this process leaks into it.
                nuint size = 0;
                InitializeProcThreadAttributeList(0, 1, 0, ref size);
                attributes = Marshal.AllocHGlobal((nint)size);
                handleList = Marshal.AllocHGlobal(inherited.Length * nint.Size);
                Marshal.Copy(inherited, 0, handleList, inherited.Length);
                if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size))
                {
                    return false;
                }

                try
                {
                    if (!UpdateProcThreadAttribute(attributes, 0, ProcThreadAttributeHandleList, handleList,
                            (nuint)(inherited.Length * nint.Size), 0, 0))
                    {
                        return false;
                    }

                    var info = new StartupInfoEx { Size = Marshal.SizeOf<StartupInfoEx>(), AttributeList = attributes };
                    if (!CreateProcessAsUserW(lowToken, exe, commandLine, 0, 0, true, ExtendedStartupInfoPresent, 0, null,
                            ref info, out var created))
                    {
                        return false;
                    }

                    CloseHandle(created.Thread);
                    process = created.Process;
                    return true;
                }
                finally
                {
                    DeleteProcThreadAttributeList(attributes);
                }
            }
            finally
            {
                if (handleList != 0) Marshal.FreeHGlobal(handleList);
                if (attributes != 0) Marshal.FreeHGlobal(attributes);
                if (sid != 0) LocalFree(sid);
                if (lowToken != 0) CloseHandle(lowToken);
                if (token != 0) CloseHandle(token);
            }
        }
    }

    [ComImport, Guid("00000001-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IClassFactory
    {
        void CreateInstance([MarshalAs(UnmanagedType.Interface)] object? pUnkOuter, ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out object ppvObject);

        void LockServer([MarshalAs(UnmanagedType.Bool)] bool fLock);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassW
    {
        public uint Style;
        public nint WndProc;
        public int ClsExtra;
        public int WndExtra;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint Background;
        public string? MenuName;
        public string ClassName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileInfo
    {
        public nint Icon;
        public int IconIndex;
        public uint Attributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string DisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string TypeName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenMandatoryLabel
    {
        public nint Sid;
        public uint Attributes;
    }

    /// <summary>STARTUPINFOEXW with the fields Peek does not use left empty.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public int Size;
        public nint Reserved;
        public nint Desktop;
        public nint Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public int Flags;
        public ushort ShowWindow;
        public ushort Reserved2Size;
        public nint Reserved2;
        public nint StdInput;
        public nint StdOutput;
        public nint StdError;
        public nint AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint Process;
        public nint Thread;
        public int ProcessId;
        public int ThreadId;
    }

    private const uint TokenAssignPrimary = 0x1;
    private const uint TokenDuplicate = 0x2;
    private const uint TokenQuery = 0x8;
    private const uint TokenAdjustDefault = 0x80;
    private const uint MaximumAllowed = 0x02000000;
    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;
    private const int TokenIntegrityLevel = 25;
    private const uint SeGroupIntegrity = 0x20;
    private const uint ExtendedStartupInfoPresent = 0x80000;
    private const nuint ProcThreadAttributeHandleList = 0x20002;
    private const uint WaitTimeout = 0x102;

    [DllImport("ole32.dll")]
    private static extern int CoGetClassObject(ref Guid rclsid, uint dwClsContext, nint pServerInfo, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object ppv);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SHGetFileInfoW(string pszPath, uint dwFileAttributes, ref ShFileInfo psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassW(ref WndClassW lpWndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowExW(int exStyle, string className, string? windowName, int style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetLayeredWindowAttributes(nint hwnd, uint colorKey, byte alpha, uint flags);

    [DllImport("user32.dll", EntryPoint = "SetClassLongPtrW")]
    private static extern nint SetClassLongPtrW(nint hwnd, int index, nint newValue);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtrW(nint hwnd, int index, nint newValue);

    [DllImport("user32.dll")]
    private static extern nint SetParent(nint child, nint newParent);

    [DllImport("user32.dll")]
    private static extern nint GetParent(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveWindow(nint hwnd, int x, int y, int width, int height, [MarshalAs(UnmanagedType.Bool)] bool repaint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint hwnd, int command);

    [DllImport("user32.dll")]
    private static extern nint GetFocus();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsChild(nint parent, nint window);

    [DllImport("gdi32.dll")]
    private static extern nint CreateSolidBrush(int colorRef);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint obj);

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(nint handle, uint milliseconds);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(nint process, uint exitCode);

    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(nint list, int count, int flags, ref nuint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(nint list, uint flags, nuint attribute, nint value, nuint size,
        nint previous, nint returnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(nint list);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(nint process, uint access, out nint token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateTokenEx(nint token, uint access, nint attributes, int impersonationLevel,
        int tokenType, out nint newToken);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSidToSidW(string stringSid, out nint sid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetTokenInformation(nint token, int informationClass, ref TokenMandatoryLabel information,
        uint length);

    [DllImport("advapi32.dll")]
    private static extern uint GetLengthSid(nint sid);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessAsUserW(nint token, string? applicationName, string commandLine,
        nint processAttributes, nint threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags,
        nint environment, string? currentDirectory, ref StartupInfoEx startupInfo, out ProcessInformation processInformation);
}
