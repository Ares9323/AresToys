using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;

namespace AresToys.App.Services;

/// <summary>One-shot probe for the WebView2 Runtime — used to gate the Capture webpage feature
/// so the affordances stay clean on machines without the runtime installed (older Win10
/// builds, stripped LTSC images). Calls
/// <see cref="CoreWebView2Environment.GetAvailableBrowserVersionString(string?)"/>: returns
/// the installed version string when present, throws when absent. We probe once at startup
/// and cache; reinstall during the session means a restart, but the trade-off is a single
/// fast call instead of every entry-point hitting the registry.
///
/// Microsoft's evergreen download URL ships with the runtime version pinned by
/// <c>https://go.microsoft.com/fwlink/p/?LinkId=2124703</c> — we expose that as
/// <see cref="OpenInstallerPage"/> so the UI can offer a one-click install path.</summary>
public sealed class WebView2AvailabilityService
{
    /// <summary>Microsoft's stable redirect to the WebView2 evergreen runtime installer.
    /// Public surface so any UI affordance (tray menu, settings, error toast) can offer the
    /// same install path without duplicating the link.</summary>
    public const string InstallerUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";

    private readonly ILogger<WebView2AvailabilityService> _logger;
    private readonly bool _isAvailable;
    private readonly string? _installedVersion;

    public WebView2AvailabilityService(ILogger<WebView2AvailabilityService> logger)
    {
        _logger = logger;
        try
        {
            // GetAvailableBrowserVersionString(null) returns the version of the runtime that
            // CoreWebView2Environment.CreateAsync would use; throws WebView2RuntimeNotFoundException
            // (or similar) when nothing is installed. The native call is cheap (registry lookup)
            // so blocking the ctor for a moment at startup is fine.
            _installedVersion = CoreWebView2Environment.GetAvailableBrowserVersionString();
            _isAvailable = !string.IsNullOrEmpty(_installedVersion);
            if (_isAvailable)
                _logger.LogInformation("WebView2 runtime detected (version {Version})", _installedVersion);
            else
                _logger.LogInformation("WebView2 runtime probe returned an empty version — treating as unavailable");
        }
        catch (Exception ex)
        {
            _isAvailable = false;
            _installedVersion = null;
            // Info, not Warning: this is an expected outcome on machines where the user hasn't
            // installed the runtime. The Capture webpage UI gates itself on the result.
            _logger.LogInformation(ex, "WebView2 runtime not available — Capture webpage feature will be hidden");
        }
    }

    /// <summary>True when WebView2 Runtime is installed and ready to host
    /// <see cref="CoreWebView2Environment.CreateAsync(string?, string?)"/>. Cached at ctor.</summary>
    public bool IsAvailable => _isAvailable;

    /// <summary>The installed runtime version string when <see cref="IsAvailable"/> is true,
    /// otherwise null. Currently informational — not surfaced in UI but useful in logs.</summary>
    public string? InstalledVersion => _installedVersion;

    /// <summary>Open the Microsoft download page for the WebView2 evergreen runtime in the
    /// user's default browser. Best-effort: failures are logged and swallowed (a tray menu
    /// click that does nothing is better than an unhandled exception popup).</summary>
    public void OpenInstallerPage()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = InstallerUrl,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to open WebView2 installer URL");
        }
    }
}
