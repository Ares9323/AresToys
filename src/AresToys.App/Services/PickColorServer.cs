using System.Globalization;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Windows;
using Microsoft.Extensions.Logging;

namespace AresToys.App.Services;

/// <summary>
/// The primary instance's half of <c>AresToys.exe --pick-color</c> (the calling half is
/// <see cref="PickColorClient"/>). Receives the <see cref="SingleInstanceGuard.PickColorPrefix"/> message, opens
/// <see cref="ColorWheelLauncher.PickAsync"/> on the UI thread without touching the main window, and writes the
/// answer (<c>ok\t#RRGGBB</c> or <c>cancel</c>) to the one-shot reply pipe the caller named.
/// </summary>
public sealed class PickColorServer
{
    private const int ReplyConnectTimeoutMs = 5000;

    private readonly ColorWheelLauncher _launcher;
    private readonly ILogger<PickColorServer> _logger;

    public PickColorServer(ColorWheelLauncher launcher, ILogger<PickColorServer> logger)
    {
        _launcher = launcher;
        _logger = logger;
    }

    /// <summary>Handles the text after the prefix: <c>&lt;pipe&gt;\t&lt;initial or empty&gt;\t&lt;owner hwnd or 0&gt;</c>.
    /// Never throws: any failure is logged and answered <c>cancel</c> when the reply pipe is known.</summary>
    public async Task HandleAsync(string payload)
    {
        var pipeName = string.Empty;
        try
        {
            var parts = payload.Split('\t');
            pipeName = parts[0].Trim();
            if (!IsValidReplyPipeName(pipeName))
            {
                _logger.LogWarning("Pick color: refused a request with a bad reply pipe name");
                return;
            }

            AresToys.Editor.Model.ShapeColor? initial = null;
            if (parts.Length > 1 && parts[1].Length > 0
                && ColorWheelLauncher.TryParseHex(parts[1], out var parsed))
                initial = parsed;

            var owner = IntPtr.Zero;
            if (parts.Length > 2 && long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var hwnd) && hwnd > 0)
                owner = new IntPtr(hwnd);

            // Connecting before the picker opens is the acknowledgement: a caller that sees no connection within its
            // short wait knows this instance cannot answer (an older build) and gives up instead of hanging.
            using var reply = Connect(pipeName);
            if (reply is null) return;

            var dispatcher = Application.Current.Dispatcher;
            var pick = await dispatcher.InvokeAsync(
                () => _launcher.PickAsync(initialColour: initial, ownerHwnd: owner, externalCaller: true));
            var color = await pick;

            var answer = color is null ? "cancel" : $"ok\t#{color.R:X2}{color.G:X2}{color.B:X2}";
            Write(reply, answer);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Pick color failed");
            // The caller still waits on its open pipe; closing it without an answer reads as "gone" there.
        }
    }

    /// <summary>Only pipes the client names itself (<see cref="PickColorClient.ReplyPipePrefix"/> plus a guid in "N"
    /// form), so a stray message cannot make this process connect to an arbitrary pipe.</summary>
    internal static bool IsValidReplyPipeName(string name)
    {
        if (!name.StartsWith(PickColorClient.ReplyPipePrefix, StringComparison.Ordinal)) return false;
        return Guid.TryParseExact(name[PickColorClient.ReplyPipePrefix.Length..], "N", out _);
    }

    private NamedPipeClientStream? Connect(string pipeName)
    {
        try
        {
            // Anonymous: the caller's pipe must not be able to act as this process.
            var client = new NamedPipeClientStream(
                ".", pipeName, PipeDirection.Out, PipeOptions.None, TokenImpersonationLevel.Anonymous);
            client.Connect(ReplyConnectTimeoutMs);
            return client;
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Pick color: the caller is gone before the picker opened");
            return null;
        }
    }

    private void Write(NamedPipeClientStream reply, string answer)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(answer);
            reply.Write(bytes, 0, bytes.Length);
            reply.Flush();
        }
        catch (Exception ex)
        {
            // The caller is gone (closed its side): nothing left to tell.
            _logger.LogInformation(ex, "Pick color: could not deliver the answer");
        }
    }
}
