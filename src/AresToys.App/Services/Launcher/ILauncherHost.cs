using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using AresToys.App.Services.Satellite;

namespace AresToys.App.Services.Launcher;

/// <summary>What the Launcher window needs from the process that owns the workflows and the elevated windows. The
/// window always talks to this interface, whichever process it lives in: in the satellite it is
/// <see cref="ChannelLauncherHost"/> (asks the main app over the satellite channel).
/// <para>Every member returns at once or is asynchronous: the Launcher calls them on the UI thread.</para></summary>
public interface ILauncherHost
{
    /// <summary>Runs the workflow <paramref name="id"/>. Fire and forget: the answer is only logged.</summary>
    void RunWorkflow(string id);

    /// <summary>Asks the main app to bring forward a window the local activator could not (an elevated one, which a
    /// non elevated process cannot touch). True when a window was activated.</summary>
    Task<bool> ActivateAsync(string? title, string? process);

    /// <summary>Puts down the window of the elevated program that was just launched with <c>runas</c>, which the
    /// local minimizer cannot reach. Fire and forget.</summary>
    void MinimizeElevated(string path);
}

/// <summary>The satellite's <see cref="ILauncherHost"/>: forwards each call to the main app as a request on the
/// satellite channel (<c>workflow.run</c>, <c>launcher.activate</c>, <c>launcher.minimize</c>). Never blocks the
/// caller: a request that gets no answer within <see cref="RequestTimeoutMs"/> ends as a log line.</summary>
public sealed class ChannelLauncherHost : ILauncherHost
{
    public const string WorkflowRunRequest = "workflow.run";
    public const string ActivateRequest = "launcher.activate";
    public const string MinimizeRequest = "launcher.minimize";

    /// <summary>How long one request to the main app may take before it is given up.</summary>
    public const int RequestTimeoutMs = 1500;

    private readonly SatelliteChannel _channel;
    private readonly int _mainPid;
    private readonly ILogger? _logger;

    /// <param name="channel">The connection to the main app.</param>
    /// <param name="mainPid">The main app's process id; the satellite lets it take the foreground before asking it
    /// to activate a window. 0 skips that step.</param>
    public ChannelLauncherHost(SatelliteChannel channel, int mainPid, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(channel);
        _channel = channel;
        _mainPid = mainPid;
        _logger = logger;
    }

    public void RunWorkflow(string id)
    {
        if (string.IsNullOrEmpty(id)) return;
        _ = SendAsync(WorkflowRunRequest, SatelliteProtocol.ToElement(new { id }));
    }

    public async Task<bool> ActivateAsync(string? title, string? process)
    {
        // The main app is not the foreground process (the satellite is, or the user just pressed a key): without
        // this its SetForegroundWindow on the target would be refused.
        AllowForeground();
        var answer = await SendAsync(ActivateRequest, SatelliteProtocol.ToElement(new { title, process })).ConfigureAwait(false);
        return answer is { Ok: true, Data: { } data }
               && data.ValueKind == JsonValueKind.Object
               && data.TryGetProperty("activated", out var activated)
               && activated.ValueKind == JsonValueKind.True;
    }

    public void MinimizeElevated(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        _ = SendAsync(MinimizeRequest, SatelliteProtocol.ToElement(new { path }));
    }

    /// <summary>Sends one request and logs what comes back. Null when nobody answered (timeout, closed channel) or the
    /// send failed; never throws.</summary>
    private async Task<SatelliteMessage?> SendAsync(string name, JsonElement data)
    {
        try
        {
            var answer = await _channel.RequestAsync(name, data, RequestTimeoutMs).ConfigureAwait(false);
            if (answer is null)
                _logger?.LogWarning("Launcher: the main app did not answer '{Request}' within {Timeout} ms", name, RequestTimeoutMs);
            else if (!answer.Ok)
                _logger?.LogWarning("Launcher: the main app refused '{Request}': {Error}", name, answer.Error);
            return answer;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Launcher: request '{Request}' to the main app failed", name);
            return null;
        }
    }

    private void AllowForeground()
    {
        if (_mainPid <= 0) return;
        try { AllowSetForegroundWindow((uint)_mainPid); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            _logger?.LogDebug(ex, "Launcher: AllowSetForegroundWindow unavailable");
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint dwProcessId);
}
