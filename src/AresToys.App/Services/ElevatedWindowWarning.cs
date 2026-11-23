using System.Globalization;
using System.Windows;
using AresToys.App.Resources;
using AresToys.Storage.Settings;

namespace AresToys.App.Services;

/// <summary>The notice shown when an unelevated AresToys is asked to act on a window that runs as administrator,
/// which Windows forbids (UIPI). Ported from PowerToys NotificationUtil::WarnIfElevationIsRequired: shown at most
/// once per session, and "Don't show again" silences it for 30 days (ElevatedDisableIntervalInDays). PowerToys'
/// "Learn more" link becomes "Restart as administrator", which removes the limit for good.</summary>
public sealed class ElevatedWindowWarning
{
    /// <summary>Settings key holding the UTC date until which the notice stays silent (round-trip format).</summary>
    public const string KeySilencedUntil = "warnings.elevated.silenced-until";
    private static readonly TimeSpan SilencePeriod = TimeSpan.FromDays(30);

    private readonly IToastNotifier _toasts;
    private readonly ISettingsStore _settings;
    private int _shown;

    public ElevatedWindowWarning(IToastNotifier toasts, ISettingsStore settings)
    {
        _toasts = toasts;
        _settings = settings;
    }

    /// <summary>True when this process cannot act on <paramref name="hwnd"/> because the window is elevated and we are not.</summary>
    public static bool IsOutOfReach(nint hwnd)
        => !ElevationService.IsCurrentProcessElevated && ElevationService.IsWindowElevated(hwnd);

    /// <summary>Shows the notice for <paramref name="feature"/> (its display name) unless it was already shown in this
    /// session or the user silenced it.</summary>
    public async Task WarnAsync(string feature, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _shown) != 0) return;
        var silencedUntil = await _settings.GetAsync(KeySilencedUntil, cancellationToken).ConfigureAwait(false);
        if (DateTime.TryParse(silencedUntil, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var until)
            && DateTime.UtcNow < until)
            return;
        if (Interlocked.Exchange(ref _shown, 1) != 0) return;

        var buttons = new List<ToastButtonChoice>
        {
            new(Strings.Tray_RestartAsAdmin, RestartAsAdministrator),
            new(Strings.ElevatedWarning_DontShowAgain, () => _ = SilenceAsync()),
        };
        _toasts.Show(feature, Strings.ElevatedWarning_Message, buttons: buttons);
    }

    private async Task SilenceAsync()
    {
        var until = DateTime.UtcNow.Add(SilencePeriod).ToString("O", CultureInfo.InvariantCulture);
        await _settings.SetAsync(KeySilencedUntil, until, false, CancellationToken.None).ConfigureAwait(false);
    }

    private static void RestartAsAdministrator()
    {
        // UAC declined: stay in the current session, as the tray entry does.
        if (ElevationService.RestartElevated()) Application.Current.Shutdown();
    }
}
