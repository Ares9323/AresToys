using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Velopack;
using Velopack.Sources;

namespace AresToys.Updater;

/// <summary>
/// Wraps Velopack's <see cref="UpdateManager"/> against the public AresToys GitHub Releases.
/// Two entry points: <see cref="CheckSilentlyAsync"/> for the at-startup background check
/// (toast-only on found, no UI on no-update / error), and <see cref="CheckInteractivelyAsync"/>
/// for the explicit "Check for updates" button (returns a status the caller can show).
///
/// Skips itself entirely when the app isn't running from a Velopack-managed install (e.g. dev
/// builds running from the bin/Debug folder, or a hand-extracted portable zip without the
/// Velopack metadata). <see cref="UpdateManager.IsInstalled"/> is the gate — there's no point
/// trying to update something we can't atomically swap out.
///
/// We don't apply updates automatically. The result of a successful check is propagated via
/// <see cref="UpdateAvailable"/>; the caller decides whether to prompt the user, restart, or
/// queue for next-launch.
/// </summary>
public sealed class UpdaterService
{
    /// <summary>GitHub repository slug used as the Velopack release source. Kept public so the
    /// caller can override (e.g. a fork) without subclassing.</summary>
    public const string GithubRepoUrl = "https://github.com/Ares9323/AresToys";

    private readonly ILogger<UpdaterService> _logger;
    private readonly UpdateManager? _manager;

    public UpdaterService(ILogger<UpdaterService>? logger = null)
    {
        _logger = logger ?? NullLogger<UpdaterService>.Instance;
        try
        {
            // GithubSource hits api.github.com/repos/<owner>/<repo>/releases. Pre-releases left
            // off so testers on a tagged stable release don't accidentally hop onto an alpha.
            var source = new GithubSource(GithubRepoUrl, accessToken: null, prerelease: false);
            _manager = new UpdateManager(source);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "UpdaterService: failed to construct UpdateManager — updater disabled");
            _manager = null;
        }
    }

    /// <summary>True only when we're running from a Velopack-installed location AND the update
    /// manager initialised. Dev builds and hand-extracted zips return false — the Settings UI
    /// uses this to grey out the "Check for updates" button instead of throwing.</summary>
    public bool IsAvailable => _manager?.IsInstalled == true;

    /// <summary>Raised on any successful check that turns up a new version. <see cref="EventArgs"/>
    /// carries the version string so the caller can format a toast / dialog.</summary>
    public event EventHandler<UpdateAvailableEventArgs>? UpdateAvailable;

    /// <summary>Background check fired once at startup. Logs and swallows everything — no UI on
    /// "no update" or "network down". Only the <see cref="UpdateAvailable"/> event surfaces the
    /// positive case so the host can toast.</summary>
    public async Task CheckSilentlyAsync(CancellationToken cancellationToken)
    {
        if (!IsAvailable) { _logger.LogDebug("UpdaterService: not installed via Velopack — silent check skipped"); return; }
        try
        {
            var result = await _manager!.CheckForUpdatesAsync().ConfigureAwait(false);
            if (result is null)
            {
                _logger.LogDebug("UpdaterService: silent check — already on latest");
                return;
            }
            _logger.LogInformation("UpdaterService: silent check found update {Version}", result.TargetFullRelease.Version);
            UpdateAvailable?.Invoke(this, new UpdateAvailableEventArgs(result.TargetFullRelease.Version.ToString(), result));
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "UpdaterService: silent check failed (offline / GitHub rate-limit) — ignoring");
        }
    }

    /// <summary>User-driven check from Settings → "Check for updates". Surfaces every outcome
    /// (already-latest / new-version / error) so the caller can show a corresponding message.</summary>
    public async Task<CheckOutcome> CheckInteractivelyAsync(CancellationToken cancellationToken)
    {
        if (!IsAvailable) return CheckOutcome.NotInstalled();
        try
        {
            var result = await _manager!.CheckForUpdatesAsync().ConfigureAwait(false);
            if (result is null) return CheckOutcome.AlreadyLatest();
            UpdateAvailable?.Invoke(this, new UpdateAvailableEventArgs(result.TargetFullRelease.Version.ToString(), result));
            return CheckOutcome.UpdateFound(result.TargetFullRelease.Version.ToString(), result);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "UpdaterService: interactive check failed");
            return CheckOutcome.Failed(ex.Message);
        }
    }

    /// <summary>Download + (close blocking processes) + apply + restart. Used when the user clicks
    /// "Install now" on the toast or in the Settings dialog. Between download and apply we query the
    /// Restart Manager for processes locking the install folder; if any are found,
    /// <paramref name="confirmCloseLockers"/> decides whether to close them and continue. On decline
    /// we return without applying (the update is simply deferred — no restart loop).</summary>
    public async Task DownloadAndRestartAsync(
        UpdateInfo info,
        Func<IReadOnlyList<LockingProcess>, Task<bool>> confirmCloseLockers,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(confirmCloseLockers);
        if (!IsAvailable) throw new InvalidOperationException("Updater not available — cannot apply update.");

        await _manager!.DownloadUpdatesAsync(info, progress: null, ignoreDeltas: false, cancelToken: cancellationToken).ConfigureAwait(false);

        var installDir = Path.GetDirectoryName(Environment.ProcessPath);
        var lockers = string.IsNullOrEmpty(installDir)
            ? (IReadOnlyList<LockingProcess>)Array.Empty<LockingProcess>()
            : InstallLockGuard.FindLockers(installDir, _logger);
        if (lockers.Count > 0)
        {
            var proceed = await confirmCloseLockers(lockers).ConfigureAwait(false);
            if (!proceed)
            {
                _logger.LogInformation("UpdaterService: user declined to close {Count} locking process(es); update deferred", lockers.Count);
                return;
            }
            InstallLockGuard.CloseLockers(lockers, TimeSpan.FromSeconds(5), _logger);
        }

        // ApplyUpdatesAndRestart exits the process; we don't return from this call.
        _manager.ApplyUpdatesAndRestart(info);
    }
}

public sealed class UpdateAvailableEventArgs : EventArgs
{
    public UpdateAvailableEventArgs(string version, UpdateInfo info)
    {
        Version = version;
        Info = info;
    }
    public string Version { get; }
    public UpdateInfo Info { get; }
}

/// <summary>Shape-of-result for the interactive check — flat enum + payload so the Settings UI
/// can switch on it without dealing with Velopack types in the view.</summary>
public sealed record CheckOutcome(CheckOutcomeKind Kind, string? Version = null, UpdateInfo? Info = null, string? ErrorMessage = null)
{
    public static CheckOutcome NotInstalled() => new(CheckOutcomeKind.NotInstalled);
    public static CheckOutcome AlreadyLatest() => new(CheckOutcomeKind.AlreadyLatest);
    public static CheckOutcome UpdateFound(string version, UpdateInfo info) => new(CheckOutcomeKind.UpdateFound, version, info);
    public static CheckOutcome Failed(string message) => new(CheckOutcomeKind.Failed, ErrorMessage: message);
}

public enum CheckOutcomeKind
{
    NotInstalled,
    AlreadyLatest,
    UpdateFound,
    Failed,
}
