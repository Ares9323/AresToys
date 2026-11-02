using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AresToys.App.Services;

/// <summary>
/// The calling side of <c>AresToys.exe --pick-color [--initial RRGGBB] [--owner HWND] [--format hex|rgb|hsl]</c>,
/// run from <c>Program.Main</c> before the WPF app exists so the calling process never boots WPF. Other apps
/// (AresExplorerTools first) call it instead of the Windows ChooseColor dialog.
///
/// <para>Contract. stdout: one line, <c>#RRGGBB</c> (default), <c>rgb(R, G, B)</c> or <c>hsl(H, S%, L%)</c>, then exit 0.
/// Exit 1: the user cancelled. Exit 2: no running AresToys, or it went away before answering. Exit 3: bad arguments.</para>
///
/// <para>Transport. This process creates a one-shot reply pipe <c>AresToys.PickColor.&lt;guid&gt;</c>, sends
/// <c>pick-color:&lt;pipe&gt;\t&lt;initial or empty&gt;\t&lt;owner hwnd or 0&gt;</c> on the primary's single-instance pipe
/// and waits for <c>ok\t#RRGGBB</c> or <c>cancel</c> from the primary. While it waits it probes the single-instance
/// mutex about every 500 ms and gives up with exit 2 when the primary is gone, so it never hangs on a dead instance.</para>
/// </summary>
internal static class PickColorClient
{
    internal const string PickColorSwitch = "--pick-color";
    internal const string ReplyPipePrefix = "AresToys.PickColor.";

    internal const int ExitOk = 0;
    internal const int ExitCancelled = 1;
    internal const int ExitNoInstance = 2;
    internal const int ExitBadArgs = 3;

    private const int ProbeIntervalMs = 500;
    private static readonly TimeSpan ConnectBudget = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan AckBudget = TimeSpan.FromSeconds(10);

    internal enum ColorFormat { Hex, Rgb, Hsl }

    internal sealed record PickRequest(string? InitialHex, long OwnerHwnd, ColorFormat Format);

    /// <summary>True when the command line asks for the picker.</summary>
    internal static bool IsPickColorRequest(string[] args) =>
        args.Any(a => string.Equals(a, PickColorSwitch, StringComparison.OrdinalIgnoreCase));

    /// <summary>Runs the whole job and returns the process exit code.</summary>
    internal static int Run(string[] args)
    {
        if (!TryParse(args, out var request)) return ExitBadArgs;

        // Nobody to ask: do not even build the reply pipe.
        if (!IsPrimaryAlive()) return ExitNoInstance;

        using var replyPipe = CreateReplyPipe(out var pipeName);
        if (replyPipe is null) return ExitNoInstance;

        // The primary shows the picker; let it take the foreground from this process.
        try { AllowSetForegroundWindow(AsfwAny); } catch { /* best effort */ }

        var message = SingleInstanceGuard.PickColorPrefix + pipeName + "\t" + (request.InitialHex ?? string.Empty)
                      + "\t" + request.OwnerHwnd.ToString(CultureInfo.InvariantCulture);
        if (!SendToPrimary(message)) return ExitNoInstance;

        var answer = WaitForAnswer(replyPipe);
        if (answer is null) return ExitNoInstance;

        var parts = answer.Split('\t', 2);
        if (string.Equals(parts[0].Trim(), "ok", StringComparison.Ordinal)
            && parts.Length == 2
            && TryParseRgb(parts[1], out var r, out var g, out var b))
        {
            WriteLine(Format(r, g, b, request.Format));
            return ExitOk;
        }
        // "cancel", or anything the primary could not turn into a colour.
        return ExitCancelled;
    }

    // ---------------------------------------------------------------- arguments

    internal static bool TryParse(string[] args, out PickRequest request)
    {
        request = new PickRequest(null, 0, ColorFormat.Hex);
        string? initial = null;
        long owner = 0;
        var format = ColorFormat.Hex;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (string.Equals(arg, PickColorSwitch, StringComparison.OrdinalIgnoreCase)) continue;

            // "--name value" or "--name=value".
            string name = arg, value;
            var eq = arg.IndexOf('=');
            if (eq > 0)
            {
                name = arg[..eq];
                value = arg[(eq + 1)..];
            }
            else
            {
                if (i + 1 >= args.Length) return false;
                value = args[++i];
            }

            switch (name.ToLowerInvariant())
            {
                case "--initial":
                    if (!TryNormalizeHex(value, out initial)) return false;
                    break;
                case "--owner":
                    if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out owner)) return false;
                    break;
                case "--format":
                    switch (value.Trim().ToLowerInvariant())
                    {
                        case "hex": format = ColorFormat.Hex; break;
                        case "rgb": format = ColorFormat.Rgb; break;
                        case "hsl": format = ColorFormat.Hsl; break;
                        default: return false;
                    }
                    break;
                default:
                    return false;
            }
        }

        request = new PickRequest(initial, owner, format);
        return true;
    }

    /// <summary>Accepts <c>RRGGBB</c> with or without '#', returns it upper cased without '#'.</summary>
    private static bool TryNormalizeHex(string? text, out string? hex)
    {
        hex = null;
        var s = (text ?? string.Empty).Trim().TrimStart('#');
        if (s.Length != 6) return false;
        foreach (var c in s)
            if (!Uri.IsHexDigit(c)) return false;
        hex = s.ToUpperInvariant();
        return true;
    }

    // ---------------------------------------------------------------- transport

    /// <summary>One-shot server pipe for the reply. Readable and writable by the current user explicitly: when this
    /// process is elevated and the primary is not, the default pipe ACL (administrators only) would keep the primary
    /// out.</summary>
    private static NamedPipeServerStream? CreateReplyPipe(out string pipeName)
    {
        pipeName = ReplyPipePrefix + Guid.NewGuid().ToString("N");
        try
        {
            var security = new PipeSecurity();
            var user = WindowsIdentity.GetCurrent().User;
            if (user is not null)
                security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
            return NamedPipeServerStreamAcl.Create(
                pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
                inBufferSize: 0, outBufferSize: 0, security);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Connects to the primary's single-instance pipe and writes the message. The primary opens that pipe
    /// only after its host is built, so it retries in short steps while the primary is alive, up to a budget.</summary>
    private static bool SendToPrimary(string message)
    {
        var deadline = DateTime.UtcNow + ConnectBudget;
        while (true)
        {
            try
            {
                using var client = new NamedPipeClientStream(
                    ".", SingleInstanceGuard.PipeName, PipeDirection.Out, PipeOptions.None,
                    TokenImpersonationLevel.Anonymous);
                client.Connect(ProbeIntervalMs);
                var bytes = Encoding.UTF8.GetBytes(message);
                client.Write(bytes, 0, bytes.Length);
                client.Flush();
                return true;
            }
            catch (TimeoutException)
            {
                // Not listening yet: fall through to the liveness check.
            }
            catch (IOException)
            {
                // Pipe busy or broken mid-handshake: same.
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
            catch
            {
                return false;
            }

            if (!IsPrimaryAlive() || DateTime.UtcNow >= deadline) return false;
        }
    }

    /// <summary>Waits for the primary to connect to the reply pipe and write its answer. Returns the answer text, or
    /// null when the primary exits first or closes without writing.</summary>
    private static string? WaitForAnswer(NamedPipeServerStream replyPipe)
    {
        try
        {
            // The primary connects as soon as it takes the request, before the picker opens. No connection within
            // the acknowledgement wait means it cannot answer (an older build without the handler, or busy dying).
            var connect = replyPipe.WaitForConnectionAsync();
            var ackDeadline = DateTime.UtcNow + AckBudget;
            while (!connect.Wait(ProbeIntervalMs))
            {
                if (!IsPrimaryAlive() || DateTime.UtcNow >= ackDeadline) return null;
            }

            using var ms = new MemoryStream();
            var copy = replyPipe.CopyToAsync(ms);
            while (!copy.Wait(ProbeIntervalMs))
            {
                if (!IsPrimaryAlive()) return null;
            }
            var text = Encoding.UTF8.GetString(ms.ToArray()).Trim();
            return text.Length == 0 ? null : text;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>True while a primary AresToys holds the single-instance mutex. Opening it from a process of lower
    /// integrity than an elevated primary is refused with an access error, which still means it exists.</summary>
    private static bool IsPrimaryAlive()
    {
        try
        {
            if (!Mutex.TryOpenExisting(SingleInstanceGuard.MutexName, out var mutex)) return false;
            mutex.Dispose();
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
        catch
        {
            return true;
        }
    }

    // ---------------------------------------------------------------- output

    private static bool TryParseRgb(string text, out byte r, out byte g, out byte b)
    {
        r = g = b = 0;
        var s = text.Trim().TrimStart('#');
        if (s.Length != 6) return false;
        return byte.TryParse(s.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out r)
            && byte.TryParse(s.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out g)
            && byte.TryParse(s.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b);
    }

    internal static string Format(byte r, byte g, byte b, ColorFormat format)
    {
        switch (format)
        {
            case ColorFormat.Rgb:
                return string.Create(CultureInfo.InvariantCulture, $"rgb({r}, {g}, {b})");
            case ColorFormat.Hsl:
            {
                var (h, s, l) = ToHsl(r, g, b);
                return string.Create(CultureInfo.InvariantCulture, $"hsl({h}, {s}%, {l}%)");
            }
            default:
                return string.Create(CultureInfo.InvariantCulture, $"#{r:X2}{g:X2}{b:X2}");
        }
    }

    /// <summary>RGB to HSL as integers: hue 0 to 359 degrees, saturation and lightness 0 to 100 percent.</summary>
    internal static (int H, int S, int L) ToHsl(byte r8, byte g8, byte b8)
    {
        double r = r8 / 255.0, g = g8 / 255.0, b = b8 / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2.0;
        double h = 0, s = 0;
        var d = max - min;
        if (d > 0)
        {
            s = l > 0.5 ? d / (2.0 - max - min) : d / (max + min);
            if (max == r) h = (g - b) / d + (g < b ? 6 : 0);
            else if (max == g) h = (b - r) / d + 2;
            else h = (r - g) / d + 4;
            h *= 60;
        }
        var hi = (int)Math.Round(h, MidpointRounding.AwayFromZero) % 360;
        return (hi, (int)Math.Round(s * 100, MidpointRounding.AwayFromZero), (int)Math.Round(l * 100, MidpointRounding.AwayFromZero));
    }

    /// <summary>Writes the result line to the inherited stdout. This is a WinExe: when stdout is redirected (a calling
    /// app reading a pipe) the handle is already ours; when it is not (a console user), the parent's console is attached
    /// and stdout pointed at it so the line shows.</summary>
    private static void WriteLine(string line)
    {
        try
        {
            if (!HasUsableStdout())
            {
                if (AttachConsole(AttachParentProcess))
                {
                    var conout = CreateFile("CONOUT$", GenericWrite, FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
                    if (!conout.IsInvalid) SetStdHandle(StdOutputHandle, conout.DangerousGetHandle());
                    // The handle stays open for the life of this short process on purpose.
                    GC.KeepAlive(conout);
                }
            }
            using var stdout = Console.OpenStandardOutput();
            var bytes = new UTF8Encoding(false).GetBytes(line + Environment.NewLine);
            stdout.Write(bytes, 0, bytes.Length);
            stdout.Flush();
        }
        catch
        {
            // Nowhere to print: the exit code still tells the caller what happened.
        }
    }

    private static bool HasUsableStdout()
    {
        var handle = GetStdHandle(StdOutputHandle);
        return handle != IntPtr.Zero && handle != new IntPtr(-1) && GetFileType(handle) != FileTypeUnknown;
    }

    // ---------------------------------------------------------------- native

    private const uint AsfwAny = 0xFFFFFFFF;
    private const uint AttachParentProcess = 0xFFFFFFFF;
    private const int StdOutputHandle = -11;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint FileTypeUnknown = 0;

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint dwProcessId);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll")]
    private static extern bool SetStdHandle(int nStdHandle, IntPtr hHandle);

    [DllImport("kernel32.dll")]
    private static extern uint GetFileType(IntPtr hFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
        uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);
}
