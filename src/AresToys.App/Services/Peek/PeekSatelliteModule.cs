using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using AresToys.App.Services.Satellite;
using AresToys.App.Services.Shell;

namespace AresToys.App.Services.Peek;

/// <summary>Peek inside the satellite (the non elevated process, the only one whose <c>Shell.Application</c> sees a
/// normal Explorer): answers <c>peek.show</c> {root} with {shown}. It reads the selection of the active tab of the
/// Explorer window <c>root</c> on a thread of its own (COM), takes the first selected file, asks for the files of the
/// folder view in the order Explorer shows them and opens the <see cref="PeekWindow"/> on it, or retargets the window
/// when it is already open. Folders in the selection are skipped. The window is created on the first request.
/// <para>The request comes from <see cref="PeekHotkey"/> in the main app. Handlers run on the thread pool, so the window
/// is touched on the dispatcher only.</para></summary>
public sealed class PeekSatelliteModule : ISatelliteModule
{
    public const string ShowRequest = "peek.show";

    private Dispatcher? _dispatcher;
    private ILogger? _logger;
    private PeekWindow? _window;
    private PeekOpenWithStore? _openWith;
    private PeekOpenWithConfig? _config;
    private PeekKeysStore? _keysStore;
    private PeekKeys _keys = PeekKeys.Default;
    private bool _stopped;

    public string Name => SatelliteModuleNames.Peek;

    public void Register(SatelliteChannel channel, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(services);
        _dispatcher = Application.Current.Dispatcher;
        _logger = services.GetService<ILoggerFactory>()?.CreateLogger<PeekSatelliteModule>();
        _openWith = services.GetService<PeekOpenWithStore>();
        _keysStore = services.GetService<PeekKeysStore>();
        channel.Handle(ShowRequest, OnShowAsync);
    }

    /// <summary>Nothing to start: the window is created by the first request, so a Peek that is never used costs nothing.</summary>
    public void Start()
    {
    }

    public void Stop()
    {
        _stopped = true;
        try
        {
            // Shutdown allows the real close; a plain Close would only hide the window.
            _window?.Shutdown();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Peek module: closing the window failed");
        }
        _window = null;
    }

    private async Task<JsonElement?> OnShowAsync(JsonElement? data)
    {
        var dispatcher = _dispatcher ?? throw new InvalidOperationException("The Peek module is not registered.");
        var root = ReadRoot(data);
        if (root == IntPtr.Zero || _stopped) return Answer(false);

        await RefreshConfigAsync().ConfigureAwait(false);

        var logger = _logger;
        var target = await RunStaAsync(() => ReadTarget(root, logger)).ConfigureAwait(false);
        if (target is not { } found) return Answer(false);

        var shown = await dispatcher.InvokeAsync(() => ShowWindow(found.Files, found.Index, root)).Task.ConfigureAwait(false);
        return Answer(shown);
    }

    /// <summary>Loads the Open in settings and the Peek keys again so a change made in Settings shows up in the next Peek.</summary>
    private async Task RefreshConfigAsync()
    {
        if (_openWith is not null)
        {
            try
            {
                _config = await _openWith.LoadAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Peek module: reading the Open in settings failed");
            }
        }

        if (_keysStore is not null)
        {
            try
            {
                _keys = await _keysStore.LoadAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Peek module: reading the Peek keys failed, keeping the last ones");
            }
        }
    }

    /// <summary>STA thread. The first selected file of the active tab of <paramref name="root"/> and the files around it
    /// in the view's order. When the view cannot be read, or does not list that file, the file is shown alone.
    /// Null when no file is selected (nothing, or only folders).</summary>
    private static (IReadOnlyList<string> Files, int Index)? ReadTarget(IntPtr root, ILogger? logger)
    {
        var file = ExplorerWindows.GetActiveSelection(root, logger).FirstOrDefault(File.Exists);
        if (file is null) return null;

        if (ExplorerWindows.GetViewItems(root, file) is { Index: >= 0 } view)
            return (view.Items, view.Index);
        return (new[] { file }, 0);
    }

    /// <summary>UI thread only. True when the window is on screen afterwards.</summary>
    private bool ShowWindow(IReadOnlyList<string> files, int index, IntPtr anchor)
    {
        if (_stopped) return false;

        var window = EnsureWindow();
        window.Keys = _keys;
        window.Show(files, index, anchor);
        // The key went to the main app, so this process is not the foreground one and Activate alone may leave the
        // window without keyboard focus (arrows, Esc). The main app granted the right just before the request.
        TargetWindowTracker.ForceForeground(new WindowInteropHelper(window).Handle);
        return true;
    }

    /// <summary>UI thread only.</summary>
    private PeekWindow EnsureWindow()
    {
        if (_window is not null) return _window;

        var window = new PeekWindow();
        window.OpenRequested += OnOpenRequested;
        window.OpenAssignedRequested += OnOpenAssignedRequested;
        window.OpenInRequested += OnOpenInRequested;
        window.ProgramsProvider = path => _config is { } cfg ? PeekOpenWithLauncher.GetProgramsFor(cfg, path) : Array.Empty<PeekProgram>();
        window.AssignedProvider = path => _config is { } cfg ? PeekOpenWithLauncher.FindAssigned(cfg, path) : null;
        _window = window;
        return window;
    }

    /// <summary>Enter in the window: opens the file with the Windows default app, like a double click would (this
    /// process is not elevated), and closes the preview. Close is the window's way to hide itself, it only really
    /// closes through Shutdown.</summary>
    private void OnOpenRequested(string path)
    {
        PeekOpenWithLauncher.Launch(null, path, _logger);
        _window?.Close();
    }

    /// <summary>The open key (E by default): opens the file with the program assigned to its extension, the Windows
    /// default app when there is none, and closes the preview. A failed launch is logged by the launcher.</summary>
    private async void OnOpenAssignedRequested(string path)
    {
        PeekProgram? program = null;
        try
        {
            if (_openWith is not null)
            {
                var config = await _openWith.LoadAsync().ConfigureAwait(true);
                program = PeekOpenWithLauncher.FindAssigned(config, path);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Peek module: reading the Open in settings failed, using the default program");
        }

        PeekOpenWithLauncher.Launch(program, path, _logger);
        _window?.Close();
    }

    /// <summary>A program chosen in the "Open in" menu (null = system default): launches it and hides the window.</summary>
    private void OnOpenInRequested(PeekProgram? program, string path)
    {
        PeekOpenWithLauncher.Launch(program, path, _logger);
        _window?.Close();
    }

    private static IntPtr ReadRoot(JsonElement? data)
        => data is { ValueKind: JsonValueKind.Object } d
           && d.TryGetProperty("root", out var value)
           && value.ValueKind == JsonValueKind.Number
           && value.TryGetInt64(out var handle)
            ? new IntPtr(handle)
            : IntPtr.Zero;

    private static JsonElement? Answer(bool shown) => SatelliteProtocol.ToElement(new { shown });

    /// <summary>Runs <paramref name="work"/> on a new STA thread: Shell.Application needs one, and the satellite's UI
    /// thread must not wait for Explorer. An exception of the work comes back through the task.</summary>
    private static Task<T> RunStaAsync<T>(Func<T> work)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { completion.SetResult(work()); }
            catch (Exception ex) { completion.SetException(ex); }
        })
        {
            IsBackground = true,
            Name = "Peek-Selection",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
