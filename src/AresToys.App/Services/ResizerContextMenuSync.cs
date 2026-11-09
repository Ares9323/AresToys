using Microsoft.Extensions.Logging;
using AresToys.Core.Pipeline;
using AresToys.Pipeline.Profiles;
using AresToys.Storage.Settings;

namespace AresToys.App.Services;

/// <summary>
/// Keeps the Explorer "Image Resizer" submenu (<see cref="ResizerContextMenuRegistration"/>) in step
/// with the user's Resizer workflows. A singleton: the Settings checkbox calls
/// <see cref="SetEnabledAsync"/> and reads <see cref="IsEnabled"/>, startup calls
/// <see cref="SyncAsync"/> once, and every later workflow change reaches it through
/// <see cref="NotifyingPipelineProfileStore.ProfilesChanged"/> (no polling). Changes are debounced
/// so a burst of saves rewrites the registry once.
/// </summary>
public sealed class ResizerContextMenuSync : IDisposable
{
    /// <summary>Settings key. A missing key means ENABLED, so a fresh install gets the menu.</summary>
    public const string EnabledKey = "resizer.contextmenu.enabled";

    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(500);

    private readonly NotifyingPipelineProfileStore _profiles;
    private readonly ISettingsStore _settings;
    private readonly LocalizationService _localization;
    private readonly ILogger<ResizerContextMenuSync> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _timerLock = new();
    private System.Threading.Timer? _debounceTimer;
    private bool _enabled = true;
    private bool _enabledLoaded;
    // What the registry was last written from; an identical list is not rewritten.
    private string? _lastSignature;
    private bool _disposed;

    public ResizerContextMenuSync(
        NotifyingPipelineProfileStore profiles,
        ISettingsStore settings,
        LocalizationService localization,
        ILogger<ResizerContextMenuSync> logger)
    {
        _profiles = profiles;
        _settings = settings;
        _localization = localization;
        _logger = logger;
        profiles.ProfilesChanged += OnProfilesChanged;
        // The menu text is localized when written, so a language switch needs a rewrite too.
        localization.CultureChanged += OnProfilesChanged;
    }

    /// <summary>Whether the Explorer submenu is wanted. Read from the settings store on first use
    /// (the lookup does not capture a synchronization context, so waiting here is safe).</summary>
    public bool IsEnabled
    {
        get
        {
            EnsureEnabledLoaded();
            return _enabled;
        }
    }

    /// <summary>Store the choice, then register (rebuilding the list) or remove every key.</summary>
    public async Task SetEnabledAsync(bool enabled)
    {
        await _settings.SetAsync(EnabledKey, enabled ? "true" : "false", sensitive: false, CancellationToken.None)
            .ConfigureAwait(false);
        _enabled = enabled;
        _enabledLoaded = true;

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _lastSignature = null;
            if (enabled)
                await RegisterCurrentWorkflowsAsync().ConfigureAwait(false);
            else
                await Task.Run(ResizerContextMenuRegistration.Unregister).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    /// <summary>When enabled, rewrite the submenu from the current Resizer workflows (with only
    /// "Custom..." if there are none). Never throws: failures are logged. Safe to call often.</summary>
    public async Task SyncAsync()
    {
        try
        {
            if (!IsEnabled) return;
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                // The setting may have flipped while this call waited for the gate.
                if (!_enabled) return;
                await RegisterCurrentWorkflowsAsync().ConfigureAwait(false);
            }
            finally { _gate.Release(); }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not sync the Explorer resize submenu");
        }
    }

    public void Dispose()
    {
        _disposed = true;
        lock (_timerLock) { _debounceTimer?.Dispose(); _debounceTimer = null; }
        _profiles.ProfilesChanged -= OnProfilesChanged;
        _localization.CultureChanged -= OnProfilesChanged;
        _gate.Dispose();
    }

    // Caller holds the gate.
    private async Task RegisterCurrentWorkflowsAsync()
    {
        var workflows = await ListResizerWorkflowsAsync().ConfigureAwait(false);
        var signature = string.Join('\n', workflows.Select(w => w.Id + '\t' + w.Name))
            + '\n' + (Markup.LocalizedStrings.Instance.Culture?.Name ?? string.Empty);
        if (signature == _lastSignature && ResizerContextMenuRegistration.IsRegistered()) return;

        // Registry and shell notification off the caller's thread (it may be the UI thread).
        await Task.Run(() => ResizerContextMenuRegistration.Register(workflows)).ConfigureAwait(false);
        _lastSignature = signature;
    }

    /// <summary>Shipped presets first in their defined order, then the user's own by name.</summary>
    private async Task<IReadOnlyList<(string Id, string Name)>> ListResizerWorkflowsAsync()
    {
        var profiles = await _profiles.ListAsync(CancellationToken.None).ConfigureAwait(false);
        var builtInOrder = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < DefaultPipelineProfiles.All.Count; i++)
            builtInOrder[DefaultPipelineProfiles.All[i].Id] = i;

        return profiles
            .Where(p => DefaultPipelineProfiles.CategoryOf(p) == DefaultPipelineProfiles.ResizerCategory)
            .Select(p => (Profile: p, Name: WorkflowDisplayNameLocalizer.Localize(p.Id, p.DisplayName)))
            .OrderBy(x => builtInOrder.TryGetValue(x.Profile.Id, out var order) ? order : int.MaxValue)
            .ThenBy(x => x.Name, NaturalStringComparer.Instance)
            .Select(x => (x.Profile.Id, x.Name))
            .ToList();
    }

    private void EnsureEnabledLoaded()
    {
        if (_enabledLoaded) return;
        try
        {
            var value = _settings.GetAsync(EnabledKey, CancellationToken.None).GetAwaiter().GetResult();
            // Only an explicit "false" turns it off; a missing key means enabled.
            _enabled = !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the Explorer resize submenu setting, assuming enabled");
            _enabled = true;
        }
        _enabledLoaded = true;
    }

    private void OnProfilesChanged(object? sender, EventArgs e) => ScheduleSync();

    private void ScheduleSync()
    {
        lock (_timerLock)
        {
            if (_disposed) return;
            _debounceTimer ??= new System.Threading.Timer(_ => _ = SyncAsync(), null,
                System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
            _debounceTimer.Change(Debounce, System.Threading.Timeout.InfiniteTimeSpan);
        }
    }
}

/// <summary>
/// Wraps the real profile store and raises <see cref="ProfilesChanged"/> after every write. The
/// workflows are written from several places (the workflows list, the editor, the hotkey config,
/// the seeder), and this wrapper is the one point they all pass through. It is registered as the
/// <see cref="IPipelineProfileStore"/> in <c>App.xaml.cs</c>.
/// </summary>
public sealed class NotifyingPipelineProfileStore : IPipelineProfileStore
{
    private readonly IPipelineProfileStore _inner;

    public NotifyingPipelineProfileStore(IPipelineProfileStore inner) => _inner = inner;

    /// <summary>Raised after a profile was saved or deleted, on the writer's thread.</summary>
    public event EventHandler? ProfilesChanged;

    public Task<PipelineProfile?> GetAsync(string id, CancellationToken cancellationToken)
        => _inner.GetAsync(id, cancellationToken);

    public Task<IReadOnlyList<PipelineProfile>> ListAsync(CancellationToken cancellationToken)
        => _inner.ListAsync(cancellationToken);

    public async Task UpsertAsync(PipelineProfile profile, CancellationToken cancellationToken)
    {
        await _inner.UpsertAsync(profile, cancellationToken).ConfigureAwait(false);
        ProfilesChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken cancellationToken)
    {
        var deleted = await _inner.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
        if (deleted) ProfilesChanged?.Invoke(this, EventArgs.Empty);
        return deleted;
    }
}
