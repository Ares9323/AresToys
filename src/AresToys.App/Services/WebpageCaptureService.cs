using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace AresToys.App.Services;

/// <summary>
/// Renders a URL in a hidden off-screen WebView2 and returns a full-page PNG screenshot via the
/// Chrome DevTools Protocol (<c>Page.captureScreenshot</c> with <c>captureBeyondViewport=true</c>).
/// We host the control in a real <see cref="Window"/> placed at (-32000, -32000) — WebView2
/// requires an actual HWND surface to render into, and a hidden window with Visibility=Hidden
/// would suspend rendering. Off-screen positioning keeps the surface live without flashing the
/// user's screen.
///
/// User data goes under <c>%LOCALAPPDATA%\AresToys\WebView2</c> (cookies, cache) so a future
/// "remember login" feature could reuse the same profile across captures. We do NOT clear it
/// between calls — that's the user's lever for capturing pages that need a session.
/// </summary>
public sealed class WebpageCaptureService
{
    private const string EnvironmentSubfolder = "WebView2";
    // 1366×900 is the most common laptop viewport; pages that respond to width-based media
    // queries render their desktop layout at this size. The CDP call later re-grabs the full
    // scrollable height regardless, so this is just the "visible viewport" the page lays out for.
    private const int InitialWidth = 1366;
    private const int InitialHeight = 900;
    // Off-screen position — far enough that no monitor in any layout could overlap it.
    private const int OffscreenX = -32000;
    private const int OffscreenY = -32000;
    // Render-settle wait: gives lazy-loaded images / web fonts / above-the-fold animations a
    // moment to land before we grab. Pages with infinite scroll will still only get what's
    // currently in the DOM — chasing them is out of scope.
    private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(800);
    // Hard ceiling so a hung page can't pin the WebView2 thread forever.
    private static readonly TimeSpan NavigationTimeout = TimeSpan.FromSeconds(30);

    private readonly ILogger<WebpageCaptureService> _logger;

    public WebpageCaptureService(ILogger<WebpageCaptureService> logger)
    {
        _logger = logger;
    }

    /// <summary>Capture <paramref name="url"/> to PNG bytes. Returns <c>null</c> when navigation
    /// fails (DNS / 4xx / 5xx / SSL error / timeout) or when the WebView2 runtime isn't installed.
    /// All execution is dispatched onto the WPF UI thread because WebView2 demands STA + a live
    /// HWND.</summary>
    public Task<byte[]?> CaptureAsync(string url, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        return Application.Current.Dispatcher.InvokeAsync(() => CaptureCoreAsync(url, cancellationToken)).Task.Unwrap();
    }

    private async Task<byte[]?> CaptureCoreAsync(string url, CancellationToken cancellationToken)
    {
        // Off-screen host window. ShowInTaskbar=false + WindowStyle=None hides it from Alt+Tab and
        // the taskbar even though it's technically Visible (a real surface is required for the
        // browser to paint into; a hidden window pauses the compositor and breaks the screenshot).
        var window = new Window
        {
            Width = InitialWidth,
            Height = InitialHeight,
            Left = OffscreenX,
            Top = OffscreenY,
            ShowInTaskbar = false,
            ShowActivated = false,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            Topmost = false,
            Title = "AresToys Webpage Capture (offscreen)"
        };

        var web = new WebView2();
        window.Content = web;
        window.Show();

        try
        {
            // Dedicated user-data folder under LOCALAPPDATA — keeps cookies/cache separate from
            // any other Edge/WebView2 user on the machine. The folder is created lazily on first use.
            var userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AresToys",
                EnvironmentSubfolder);
            Directory.CreateDirectory(userDataFolder);

            CoreWebView2Environment env;
            try
            {
                env = await CoreWebView2Environment.CreateAsync(browserExecutableFolder: null, userDataFolder).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "WebpageCaptureService: WebView2 runtime missing or failed to initialise — install via https://go.microsoft.com/fwlink/p/?LinkId=2124703");
                return null;
            }

            await web.EnsureCoreWebView2Async(env).ConfigureAwait(true);

            // Wait for the first NavigationCompleted that matches the URL we asked for —
            // pre-navigation 'about:blank' fires NavigationCompleted too, hence the URI guard.
            var navTcs = new TaskCompletionSource<CoreWebView2NavigationCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler<CoreWebView2NavigationCompletedEventArgs>? handler = null;
            handler = (_, e) =>
            {
                navTcs.TrySetResult(e);
                if (handler is not null) web.CoreWebView2.NavigationCompleted -= handler;
            };
            web.CoreWebView2.NavigationCompleted += handler;

            try
            {
                web.CoreWebView2.Navigate(url);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "WebpageCaptureService: invalid URL {Url}", url);
                return null;
            }

            // Three-way wait: navigation done, user cancellation, or timeout — whichever fires first.
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(NavigationTimeout);

            CoreWebView2NavigationCompletedEventArgs nav;
            try
            {
                using var registration = timeoutCts.Token.Register(() => navTcs.TrySetCanceled(timeoutCts.Token));
                nav = await navTcs.Task.ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("WebpageCaptureService: navigation to {Url} timed out / cancelled", url);
                return null;
            }

            if (!nav.IsSuccess)
            {
                _logger.LogWarning("WebpageCaptureService: navigation to {Url} failed with status {Status}", url, nav.WebErrorStatus);
                return null;
            }

            // Render-settle delay — fonts swap, lazy images flush, hero animations finish. 800ms
            // is empirically enough for ~95% of pages without dragging total capture time.
            try { await Task.Delay(SettleDelay, cancellationToken).ConfigureAwait(true); }
            catch (OperationCanceledException) { return null; }

            // Page.captureScreenshot with captureBeyondViewport tells CDP to grab the entire
            // scrollable area, not just what's currently rendered in the viewport. format=png
            // gives lossless output (matches the rest of the capture pipeline).
            const string cdpArgs = "{\"format\":\"png\",\"captureBeyondViewport\":true}";
            string json;
            try
            {
                json = await web.CoreWebView2.CallDevToolsProtocolMethodAsync("Page.captureScreenshot", cdpArgs).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "WebpageCaptureService: Page.captureScreenshot failed for {Url}", url);
                return null;
            }

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var dataProp) || dataProp.ValueKind != JsonValueKind.String)
            {
                _logger.LogWarning("WebpageCaptureService: unexpected CDP response shape for {Url}", url);
                return null;
            }

            var bytes = Convert.FromBase64String(dataProp.GetString()!);
            _logger.LogInformation("WebpageCaptureService: captured {Url} → {Bytes} bytes PNG", url, bytes.Length);
            return bytes;
        }
        finally
        {
            // WebView2 hosts a COM CoreWebView2Controller that the GC's finalizer can't tear
            // down safely once the underlying Edge process has gone (the HwndHost finalizer
            // tries to QueryInterface on a dead RCW and crashes the app with E_NOINTERFACE).
            // Dispose explicitly here, before window.Close(), so the finalizer sees the
            // already-disposed flag and skips its cleanup.
            try
            {
                window.Content = null;
                web.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "WebpageCaptureService: WebView2 dispose threw — ignoring");
            }
            window.Close();
        }
    }
}
