using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AresToys.App.Services.Peek;

/// <summary>The helper mode of AresToys.exe (<c>--peek-host &lt;in&gt; &lt;out&gt;</c>) that hosts one Windows preview
/// handler at a time. Peek starts it with a Low integrity token, the only kind of process Windows lets start a
/// handler that runs in prevhost.exe; Peek then reparents the helper's window into its own (<see cref="PeekShellPreview"/>).
/// It does not touch settings, the database, logs or any Medium folder: all it has is two inherited anonymous pipes
/// (commands in, replies out), one UTF-8 line per message:
/// <code>
/// bg &lt;colorref hex&gt;          the colour behind the handler's window
/// resize &lt;w&gt; &lt;h&gt;            the size in pixels (before a load: the size the window is created at)
/// load &lt;{clsid}&gt; &lt;path&gt;    replaces the preview; replies "loaded &lt;hwnd&gt;" or "failed &lt;hresult hex&gt;"
/// unload                     ends the preview and destroys its window
/// quit                       ends the helper (so does the end of the input pipe)
/// </code>
/// The process runs one STA thread that pumps messages and does all the COM work; a second thread only reads
/// lines and posts them to it.</summary>
internal sealed class PeekHandlerHost
{
    internal const string Arg = "--peek-host";

    private const string WindowClass = "AresToysPeekHandlerHost";
    private const uint WmApp = 0x8000;
    private const uint ClsctxLocalServer = 4;
    private const int HwndMessage = -3;
    private const int WsPopup = unchecked((int)0x80000000);
    private const int WsClipChildren = 0x02000000;
    private const int WsClipSiblings = 0x04000000;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const int SwShowNoActivate = 4;
    private const int GclpHbrBackground = -10;
    private const int OffScreen = -32000;
    private const int ENoInterface = unchecked((int)0x80004002);
    private const int EInvalidArg = unchecked((int)0x80070057);

    private static readonly UTF8Encoding Utf8 = new(false);

    private readonly StreamReader _input;
    private readonly StreamWriter _output;
    private readonly ConcurrentQueue<string> _commands = new();
    private readonly WndProcDelegate _wndProc;
    private nint _pump;
    private nint _window;
    private nint _brush;
    private PeekHandlerCom.IPreviewHandler? _handler;
    private FileStream? _stream;
    private int _width = 400;
    private int _height = 300;
    private bool _draining;

    private PeekHandlerHost(StreamReader input, StreamWriter output)
    {
        _input = input;
        _output = output;
        _wndProc = WndProc;
    }

    /// <summary>True for the arguments the Peek process starts the helper with.</summary>
    internal static bool IsHostRequest(string[] args) => args.Length == 3 && args[0] == Arg;

    /// <summary>Runs the helper until <c>quit</c> or the end of the input pipe. The exit code is 0, or 2 for bad arguments.</summary>
    internal static int Run(string[] args)
    {
        if (!long.TryParse(args[1], out var inHandle) || !long.TryParse(args[2], out var outHandle))
        {
            return 2;
        }

        using var input = new StreamReader(
            new AnonymousPipeClientStream(PipeDirection.In, new SafePipeHandle((nint)inHandle, true)), Utf8);
        using var output = new StreamWriter(
            new AnonymousPipeClientStream(PipeDirection.Out, new SafePipeHandle((nint)outHandle, true)), Utf8)
        {
            AutoFlush = true,
        };
        return new PeekHandlerHost(input, output).Loop();
    }

    private int Loop()
    {
        var wndClass = new WndClassW
        {
            WndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            ClassName = WindowClass,
        };
        if (RegisterClassW(ref wndClass) == 0)
        {
            return 3;
        }

        // A message only window is the target the reader thread posts to; the preview windows are created per load.
        _pump = CreateWindowExW(0, WindowClass, null, 0, 0, 0, 0, 0, HwndMessage, 0, 0, 0);
        if (_pump == 0)
        {
            return 3;
        }

        new Thread(ReadCommands) { IsBackground = true, Name = "PeekHandlerHost.Reader" }.Start();
        while (GetMessageW(out var msg, 0, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }

        UnloadHandler();
        DestroyWindow(_pump);
        return 0;
    }

    private void ReadCommands()
    {
        try
        {
            while (_input.ReadLine() is { } line)
            {
                _commands.Enqueue(line);
                PostMessageW(_pump, WmApp, 0, 0);
            }
        }
        catch (IOException)
        {
            // The parent closed the pipe abruptly: the same as the end of the input, the quit below ends the helper.
        }

        _commands.Enqueue("quit");
        PostMessageW(_pump, WmApp, 0, 0);
    }

    private nint WndProc(nint hwnd, uint message, nint wParam, nint lParam)
    {
        if (message == WmApp && hwnd == _pump)
        {
            // A handler call can pump messages (COM waits): the commands queued meanwhile run after it, in order,
            // by the loop of the outer call.
            if (!_draining)
            {
                _draining = true;
                try
                {
                    while (_commands.TryDequeue(out var line))
                    {
                        Execute(line);
                    }
                }
                finally
                {
                    _draining = false;
                }
            }

            return 0;
        }

        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    private void Execute(string line)
    {
        var parts = line.Split(' ', 3);
        switch (parts[0])
        {
            case "bg" when parts.Length == 2 && int.TryParse(parts[1], System.Globalization.NumberStyles.HexNumber, null, out var colour):
                SetBackground(colour);
                break;
            case "resize" when parts.Length == 3 && int.TryParse(parts[1], out var width) && int.TryParse(parts[2], out var height):
                Resize(width, height);
                break;
            case "load" when parts.Length == 3:
                Load(parts[1], parts[2]);
                break;
            case "unload":
                UnloadHandler();
                break;
            case "quit":
                UnloadHandler();
                PostQuitMessage(0);
                break;
        }
    }

    private void Reply(string text) => _output.WriteLine(text);

    private void SetBackground(int colorRef)
    {
        var old = _brush;
        _brush = CreateSolidBrush(colorRef);
        ApplyBackground();
        if (old != 0)
        {
            DeleteObject(old);
        }
    }

    /// <summary>The window class background is what shows between the handler's own windows and while they repaint.</summary>
    private void ApplyBackground()
    {
        if (_window != 0 && _brush != 0)
        {
            SetClassLongPtrW(_window, GclpHbrBackground, _brush);
        }
    }

    private void Resize(int width, int height)
    {
        _width = Math.Max(1, width);
        _height = Math.Max(1, height);
        if (_handler is { } handler)
        {
            var rect = new PeekHandlerCom.Rect32 { Right = _width, Bottom = _height };
            try
            {
                handler.SetRect(ref rect);
            }
            catch (COMException ex)
            {
                System.Diagnostics.Debug.WriteLine("Peek host: SetRect failed: " + ex.Message);
            }
        }
    }

    private void Load(string clsidText, string path)
    {
        UnloadHandler();
        if (!Guid.TryParse(clsidText, out var clsid))
        {
            Reply("failed " + EInvalidArg.ToString("X8", System.Globalization.CultureInfo.InvariantCulture));
            return;
        }

        var hr = TryLoad(clsid, path);
        if (hr < 0)
        {
            UnloadHandler();
            Reply("failed " + hr.ToString("X8", System.Globalization.CultureInfo.InvariantCulture));
            return;
        }

        Reply("loaded " + _window);
    }

    /// <summary>Creates the window (a top level popup out of sight: Peek reparents it as soon as it hears "loaded"),
    /// the handler, gives it the file and starts the preview. 0 when it shows, else the failing HRESULT.</summary>
    private int TryLoad(Guid clsid, string path)
    {
        _window = CreateWindowExW(WsExToolWindow | WsExNoActivate, WindowClass, null,
            WsPopup | WsClipChildren | WsClipSiblings, OffScreen, OffScreen, _width, _height, 0, 0, 0, 0);
        if (_window == 0)
        {
            return Marshal.GetHRForLastWin32Error();
        }

        ApplyBackground();
        ShowWindow(_window, SwShowNoActivate);
        try
        {
            var iid = typeof(PeekHandlerCom.IPreviewHandler).GUID;
            var hr = CoCreateInstance(ref clsid, 0, ClsctxLocalServer, ref iid, out var instance);
            if (hr < 0)
            {
                return hr;
            }

            var handler = (PeekHandlerCom.IPreviewHandler)instance;
            _handler = handler;
            if (!PeekHandlerCom.Initialize(handler, path, out _stream))
            {
                return ENoInterface;
            }

            var rect = new PeekHandlerCom.Rect32 { Right = _width, Bottom = _height };
            handler.SetWindow(_window, ref rect);
            handler.DoPreview();
            return 0;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or IOException or UnauthorizedAccessException)
        {
            return ex.HResult < 0 ? ex.HResult : ENoInterface;
        }
    }

    /// <summary>Tells the handler to unload, releases it, closes the file and destroys the window.</summary>
    private void UnloadHandler()
    {
        if (_handler is { } handler)
        {
            _handler = null;
            try
            {
                handler.Unload();
            }
            catch (COMException ex)
            {
                // The handler's own process is gone: nothing is left to unload, only the proxy to release.
                System.Diagnostics.Debug.WriteLine("Peek host: Unload failed: " + ex.Message);
            }
            finally
            {
                Marshal.FinalReleaseComObject(handler);
            }
        }

        _stream?.Dispose();
        _stream = null;
        if (_window != 0)
        {
            DestroyWindow(_window);
            _window = 0;
        }
    }

    private delegate nint WndProcDelegate(nint hwnd, uint message, nint wParam, nint lParam);

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

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public nint Hwnd;
        public uint Message;
        public nint WParam;
        public nint LParam;
        public uint Time;
        public int X;
        public int Y;
    }

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(ref Guid rclsid, nint pUnkOuter, uint dwClsContext, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object ppv);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassW(ref WndClassW lpWndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(int exStyle, string className, string? windowName, int style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint hwnd, int command);

    [DllImport("user32.dll")]
    private static extern nint DefWindowProcW(nint hwnd, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(nint hwnd, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessageW(out Msg msg, nint hwnd, uint filterMin, uint filterMax);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref Msg msg);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessageW(ref Msg msg);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int exitCode);

    [DllImport("user32.dll", EntryPoint = "SetClassLongPtrW")]
    private static extern nint SetClassLongPtrW(nint hwnd, int index, nint newValue);

    [DllImport("gdi32.dll")]
    private static extern nint CreateSolidBrush(int colorRef);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint obj);
}
