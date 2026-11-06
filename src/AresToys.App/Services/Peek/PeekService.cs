using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using AresToys.App.Native;
using AresToys.App.Services.Satellite;

namespace AresToys.App.Services.Peek;

/// <summary>Asks the Peek module in the non elevated satellite to preview the selection of an Explorer window. The one
/// place that sends <c>peek.show</c>: the Ctrl+Space key (<see cref="PeekHotkey"/>) and the workflow task
/// (<c>PeekTask</c>) both go through it, and of <c>peek.hide</c>, sent when the Peek key is pressed while Peek itself is
/// in front. The satellite shows the preview of the active tab's selection, retargets an open window to it, or closes
/// the window when it already shows that file.</summary>
public sealed class PeekService
{
    private const int RequestTimeoutMs = 1500;
    private const uint AsfwAny = unchecked((uint)-1);

    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(uint processId);

    private readonly SatelliteService _satellite;
    private readonly ILogger<PeekService> _logger;
    private readonly IServiceProvider _services;
    // One request (show or hide) in flight at a time: a press while one runs is dropped, so a second press never
    // reads the selection the first is still showing and closes it.
    private int _inFlight;

    /// <param name="services">Resolves <see cref="EditorLauncher"/> when the satellite asks for the editor, instead of
    /// taking it as a constructor dependency, like <c>LauncherHostBridge</c> does for the workflow runner.</param>
    public PeekService(SatelliteService satellite, IServiceProvider services, ILogger<PeekService> logger)
    {
        _satellite = satellite;
        _services = services;
        _logger = logger;
        // The handler is registered when the satellite connects (before its channel reads, so no request can arrive
        // first); a satellite that connected before this service existed has none yet.
        _satellite.Server.Connected += OnConnected;
        if (_satellite.Server.Current is { } existing) OnConnected(existing);
    }

    private void OnConnected(SatelliteChannel channel) => channel.Handle(PeekSatelliteModule.EditRequest, OnEditAsync);

    // The editor key in the Peek window: opens the image in the AresToys editor on the UI thread. Answers at once, the
    // editor stays open until the user is done and must not hold the channel's request.
    private Task<JsonElement?> OnEditAsync(JsonElement? data)
    {
        var path = data is { ValueKind: JsonValueKind.Object } d && d.TryGetProperty("path", out var value)
                   && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        if (string.IsNullOrWhiteSpace(path)) return Task.FromResult<JsonElement?>(null);

        Application.Current.Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                await _services.GetRequiredService<EditorLauncher>().EditPathAsync(path, CancellationToken.None).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Peek: opening {Path} in the editor failed", path);
            }
        });
        return Task.FromResult<JsonElement?>(null);
    }

    // Gives the foreground right of this process to the satellite, which then puts the preview window in front. Runs
    // on the caller's thread, before the request moves to the pool, while this process still owns the foreground right
    // (the key press or the hotkey run that started the request).
    private void HandOffForeground()
    {
        try { AllowSetForegroundWindow(AsfwAny); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            _logger.LogDebug(ex, "Peek: AllowSetForegroundWindow unavailable");
        }
    }

    /// <summary>Opens Peek on the selection of the active tab of the Explorer window <paramref name="root"/>, or
    /// retargets an open Peek to it. Hands the foreground right to the satellite on the calling thread, then sends the request from the pool. Returns whether a preview was
    /// shown: false when no satellite answers, it refuses, or nothing is selected.</summary>
    public Task<bool> ToggleAsync(IntPtr root)
    {
        if (Interlocked.CompareExchange(ref _inFlight, 1, 0) != 0) return Task.FromResult(false);
        HandOffForeground();
        return Task.Run(async () =>
        {
            try { return await RequestShowAsync(root).ConfigureAwait(false); }
            finally { Volatile.Write(ref _inFlight, 0); }
        });
    }

    /// <summary>True when <paramref name="root"/> is the Peek window: a top-level window of the satellite process with
    /// the Peek title. Cheap and cached state only (hook thread). Native children of the window (web view, shell
    /// preview) never change the foreground top-level window, so this holds wherever the focus is inside Peek.</summary>
    public bool IsPeekWindow(IntPtr root)
    {
        var pid = _satellite.SatelliteProcessId;
        if (pid == 0 || root == IntPtr.Zero) return false;
        if (AppNativeMethods.GetWindowThreadProcessId(root, out var owner) == 0 || owner != (uint)pid) return false;

        return AppNativeMethods.GetProp(root, PeekWindow.WindowMarker) != IntPtr.Zero;
    }

    /// <summary>Closes the open Peek window (the Peek key pressed while Peek is in front). Sends <c>peek.hide</c> from
    /// the pool. A failure is logged; the window stays open then.</summary>
    public Task HideAsync()
    {
        if (Interlocked.CompareExchange(ref _inFlight, 1, 0) != 0) return Task.CompletedTask;
        return Task.Run(async () =>
        {
            try { await RequestHideAsync().ConfigureAwait(false); }
            finally { Volatile.Write(ref _inFlight, 0); }
        });
    }

    private async Task RequestHideAsync()
    {
        try
        {
            var channel = _satellite.Server.Current;
            if (channel is null)
            {
                _logger.LogWarning("Peek: no satellite connected, nothing to hide");
                return;
            }

            var answer = await channel.RequestAsync(PeekSatelliteModule.HideRequest, null, RequestTimeoutMs).ConfigureAwait(false);
            if (answer is null || !answer.Ok)
            {
                _logger.LogWarning("Peek: the satellite did not hide the preview ({Reason})", answer?.Error ?? "no answer");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Peek: asking the satellite to hide the preview failed");
        }
    }

    private async Task<bool> RequestShowAsync(IntPtr root)
    {
        try
        {
            var channel = _satellite.Server.Current;
            if (channel is null)
            {
                _logger.LogWarning("Peek: no satellite connected, nothing to show");
                return false;
            }

            var answer = await channel.RequestAsync(PeekSatelliteModule.ShowRequest,
                SatelliteProtocol.ToElement(new { root = root.ToInt64() }), RequestTimeoutMs).ConfigureAwait(false);
            if (answer is null)
            {
                _logger.LogWarning("Peek: the satellite did not answer in {Timeout} ms", RequestTimeoutMs);
                return false;
            }

            if (!answer.Ok)
            {
                _logger.LogWarning("Peek: the satellite refused the request ({Reason})", answer.Error ?? "no reason given");
                return false;
            }

            if (answer.Data is not { ValueKind: JsonValueKind.Object } data
                || !data.TryGetProperty("shown", out var shown) || shown.ValueKind != JsonValueKind.True)
            {
                _logger.LogDebug("Peek: nothing to show (no file selected in the active tab)");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Peek: asking the satellite to show the preview failed");
            return false;
        }
    }
}
