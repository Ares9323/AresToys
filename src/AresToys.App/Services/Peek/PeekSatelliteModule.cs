using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using AresToys.App.Services.Pins;
using AresToys.App.Services.Satellite;
using AresToys.App.Services.Shell;

namespace AresToys.App.Services.Peek;

/// <summary>Peek inside the satellite (the non elevated process, the only one whose <c>Shell.Application</c> sees a
/// normal Explorer): answers <c>peek.show</c> {root} with {shown}. It reads the selection of the active tab of the
/// Explorer window <c>root</c> (or of the desktop, when <c>root</c> is the desktop window) on a thread of its own
/// (COM), takes the first selected file, asks for the files of the folder view in the order Explorer shows them and
/// opens the <see cref="PeekWindow"/> on it, or retargets the window when it is already open. Folders in the
/// selection are skipped. The window is created on the first request.
/// <para>The request comes from <see cref="PeekHotkey"/> in the main app. Handlers run on the thread pool, so the window
/// is touched on the dispatcher only. A Wormhole window, which lives in this same process, skips the request and calls
/// <see cref="ShowAsync"/> through <see cref="Current"/>.</para></summary>
public sealed class PeekSatelliteModule : ISatelliteModule
{
    public const string ShowRequest = "peek.show";
    public const string HideRequest = "peek.hide";

    /// <summary>Sent to the main app, which owns the editor: {path} of the image to open in it.</summary>
    public const string EditRequest = "peek.edit";

    private const uint AsfwAny = unchecked((uint)-1);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);

    private SatelliteChannel? _channel;

    private Dispatcher? _dispatcher;
    private ILogger? _logger;
    private PeekWindow? _window;
    private PeekOpenWithStore? _openWith;
    private PeekOpenWithConfig? _config;
    private PeekKeysStore? _keysStore;
    private volatile PeekKeys _keys = PeekKeys.Default;
    private PeekWindowStore? _windowStore;
    private volatile PeekWindowSettings _windowSettings = PeekWindowSettings.Default;
    private PeekShowAsStore? _showAsStore;
    private bool _stopped;

    /// <summary>The running module of this satellite process, or null while Peek is off. How a Wormhole window reaches Peek
    /// in-process: it calls <see cref="ShowAsync"/> on this instance, and when it is null the key is not handled.</summary>
    internal static PeekSatelliteModule? Current { get; private set; }

    /// <summary>The Peek keys as last loaded (the defaults until the first load). Read on the UI thread by the Wormhole
    /// windows to match the toggle key.</summary>
    internal PeekKeys Keys => _keys;

    public string Name => SatelliteModuleNames.Peek;

    public void Register(SatelliteChannel channel, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(services);
        _dispatcher = Application.Current.Dispatcher;
        _logger = services.GetService<ILoggerFactory>()?.CreateLogger<PeekSatelliteModule>();
        _openWith = services.GetService<PeekOpenWithStore>();
        _keysStore = services.GetService<PeekKeysStore>();
        _windowStore = services.GetService<PeekWindowStore>();
        _showAsStore = services.GetService<PeekShowAsStore>();
        _channel = channel;
        channel.Handle(ShowRequest, OnShowAsync);
        channel.Handle(HideRequest, OnHideAsync);
        Current = this;
    }

    /// <summary>The window is created by the first request, so a Peek that is never used costs nothing. The settings are
    /// read now, so a Wormhole can match the toggle key before the first Peek.</summary>
    public void Start() => _ = RefreshConfigAsync();

    public void Stop()
    {
        _stopped = true;
        if (ReferenceEquals(Current, this)) Current = null;
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

    private async Task<JsonElement?> OnHideAsync(JsonElement? data)
    {
        var dispatcher = _dispatcher;
        if (dispatcher is null || _stopped) return Answer(false);

        await dispatcher.InvokeAsync(() => _window?.HidePeek()).Task.ConfigureAwait(false);
        return Answer(false);
    }

    private async Task<JsonElement?> OnShowAsync(JsonElement? data)
    {
        var root = ReadRoot(data);
        if (root == IntPtr.Zero || _stopped) return Answer(false);

        var logger = _logger;
        var file = await RunStaAsync(() => ReadSelectedFile(root, logger)).ConfigureAwait(false);
        if (file is null) return Answer(false);

        // The selected file shows at once; the folder list (navigation) follows when it is read.
        var shown = await ShowAsync(new[] { file }, 0, root).ConfigureAwait(false);
        if (shown) _ = FillFolderListAsync(root, file, logger);
        return Answer(shown);
    }

    // Reads the folder view in the view's order and hands it to the open window, which keeps it only while it still
    // shows the file. Runs after the answer: a failure leaves the single file.
    private async Task FillFolderListAsync(IntPtr root, string file, ILogger? logger)
    {
        try
        {
            var view = await RunStaAsync(() => ExplorerWindows.GetViewItems(root, file)).ConfigureAwait(false);
            if (view is not { Index: >= 0 } found || _dispatcher is not { } dispatcher || _stopped) return;
            await dispatcher.InvokeAsync(() => _window?.SetFolderList(found.Items, file)).Task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "Peek module: reading the folder list failed");
        }
    }

    /// <summary>The one entry that puts files on screen: <c>peek.show</c> and the Wormhole windows (same process) both
    /// end here. Loads the settings again, then shows <paramref name="files"/> at <paramref name="index"/> on the UI
    /// thread, placed on the monitor of <paramref name="anchor"/>. Any thread. True when the window is on screen afterwards.</summary>
    internal async Task<bool> ShowAsync(IReadOnlyList<string> files, int index, IntPtr anchor)
    {
        var dispatcher = _dispatcher ?? throw new InvalidOperationException("The Peek module is not registered.");
        if (_stopped || files.Count == 0) return false;

        // Not awaited: a change saved in Settings reaches Peek on the next press, which does not wait for four stores.
        _ = RefreshConfigAsync();
        return await dispatcher.InvokeAsync(() => ShowWindow(files, index, anchor)).Task.ConfigureAwait(false);
    }

    /// <summary>Loads the Open in settings and the Peek keys again so a change made in Settings shows up in the next Peek.
    /// A Wormhole calls it when it gets the keyboard focus, so its copy of <see cref="Keys"/> follows a change too.</summary>
    internal async Task RefreshConfigAsync()
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

        if (_showAsStore is not null)
        {
            try
            {
                PeekShowAs.Apply(await _showAsStore.LoadAsync().ConfigureAwait(false));
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Peek module: reading the Show as picks failed, keeping the last ones");
            }
        }

        if (_windowStore is not null)
        {
            try
            {
                _windowSettings = await _windowStore.LoadAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Peek module: reading the Peek window settings failed, keeping the last ones");
            }
        }
    }

    /// <summary>STA thread. The first selected file of the active tab of <paramref name="root"/>, or null when no file
    /// is selected (nothing, or only folders).</summary>
    private static string? ReadSelectedFile(IntPtr root, ILogger? logger)
        => ExplorerWindows.GetActiveSelection(root, logger).FirstOrDefault(File.Exists);

    /// <summary>UI thread only. True when the window is on screen afterwards. A visible window that already shows the requested file hides instead (false).</summary>
    private bool ShowWindow(IReadOnlyList<string> files, int index, IntPtr anchor)
    {
        if (_stopped) return false;

        var window = EnsureWindow();
        // The key on the file Peek already shows closes it; another file re-targets the window.
        if (window.IsVisible && string.Equals(window.CurrentPath, files[Math.Clamp(index, 0, files.Count - 1)], StringComparison.OrdinalIgnoreCase))
        {
            window.HidePeek();
            return false;
        }

        window.Keys = _keys;
        window.Settings = _windowSettings;
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
        window.EditRequested += OnEditRequested;
        window.OpenInRequested += OnOpenInRequested;
        window.BoundsRemembered += OnBoundsRemembered;
        window.ShowAsRequested += OnShowAsRequested;
        window.ProgramsProvider = path => _config is { } cfg ? PeekOpenWithLauncher.GetProgramsFor(cfg, path) : Array.Empty<PeekProgram>();
        window.AssignedProvider = path => _config is { } cfg ? PeekOpenWithLauncher.FindAssigned(cfg, path) : null;
        _window = window;
        return window;
    }

    /// <summary>An entry of the "Show as" menu: saves the pick of the file's extension (null = Auto removes it), makes it
    /// the pick Peek uses at once and shows the file again. The window never writes settings itself.</summary>
    private async void OnShowAsRequested(string path, string? pick)
    {
        if (_showAsStore is null) return;

        try
        {
            var extension = Path.GetExtension(path);
            if (extension.Length == 0) return;
            PeekShowAs.Apply(await _showAsStore.SetAsync(extension, pick).ConfigureAwait(true));
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Peek module: saving the Show as pick failed");
        }

        _window?.ReloadCurrent();
    }

    /// <summary>The window was hidden at new bounds: stores them so the next Peek opens there. The window never writes
    /// settings itself. A failed save is logged and the bounds are lost until the next hide.</summary>
    private async void OnBoundsRemembered(PixelRect bounds)
    {
        if (_windowStore is null) return;

        try
        {
            await _windowStore.SaveBoundsAsync(bounds).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Peek module: saving the window bounds failed");
        }
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

    /// <summary>The editor key (R by default): asks the main app, which owns the editor, to open the image in it, and
    /// closes the preview. The satellite gives the foreground right away first so the editor window comes to the front.
    /// The request is not awaited: the main app answers as soon as it has queued the editor.</summary>
    private void OnEditRequested(string path)
    {
        try { AllowSetForegroundWindow(AsfwAny); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            _logger?.LogDebug(ex, "Peek module: AllowSetForegroundWindow unavailable");
        }

        _ = SendEditAsync(path);
        _window?.Close();
    }

    private async Task SendEditAsync(string path)
    {
        try
        {
            var answer = _channel is null
                ? null
                : await _channel.RequestAsync(EditRequest, SatelliteProtocol.ToElement(new { path })).ConfigureAwait(false);
            if (answer is not { Ok: true })
            {
                _logger?.LogWarning("Peek module: the main app did not open the editor ({Reason})", answer?.Error ?? "no answer");
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Peek module: asking the main app to open the editor failed");
        }
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
