using System.Globalization;
using AresToys.Storage.Settings;
using Microsoft.Extensions.Logging;

namespace AresToys.App.Services.Notifications;

/// <summary>How long a Windows toast lives, in two independent parts: how long the popup stays on
/// screen, and how long the entry stays in the Notification Center afterwards.
///
/// Measured behaviour of the underlying APIs (Win11 26200, WinRT toasts through
/// <c>ToastNotificationManagerCompat</c>), which is what these settings map onto:
/// <list type="bullet">
/// <item><description>A popup shows for ~6.9 s and cannot be made to stay indefinitely: the
/// longest non-ringing option tops out at ~25.7 s ("long" duration) and the Reminder scenario at
/// ~14 s. Only shortening is reliable, via <c>ToastNotifier.Hide</c>, hence the 0–7 s
/// range.</description></item>
/// <item><description><c>ExpirationTime</c> makes Windows drop the Center entry on its own, to
/// the tenth of a second, app running or not.</description></item>
/// <item><description><c>Hide</c> closes the popup early but ALSO removes the Center entry, so a
/// shortened popup that should still leave an entry has to re-issue the toast with
/// <c>SuppressPopup</c> straight after.</description></item>
/// <item><description>The <c>Dismissed</c> event fires when the popup goes away (reason
/// <c>TimedOut</c> normally) — the exact moment to drop an entry the user never wanted
/// kept.</description></item>
/// </list></summary>
public sealed class ToastLifetimeService
{
    // v2 keys: the first iteration used the opposite sentinels for the Center (0 = forever,
    // -1 = never). Reading those values under the current meaning would turn "stock behaviour"
    // into "no popup and no Center entry" — i.e. notifications silently disappearing. New keys
    // sidestep the reinterpretation entirely; the old ones are simply left behind.
    public const string PopupSecondsKey = "app.notifications.popup_seconds_v2";
    public const string CenterSecondsKey = "app.notifications.center_seconds_v2";

    /// <summary>Popup: no popup at all, the toast goes straight to the Notification Center.</summary>
    public const int PopupNone = 0;

    /// <summary>Popup ceiling, and the default. Windows' own short toast duration is ~6.9 s and
    /// nothing shorter-than-ringing goes beyond it usefully, so this doubles as "let Windows
    /// decide" — which also preserves a longer duration set in the accessibility settings.</summary>
    public const int PopupMaxSeconds = 7;

    /// <summary>Center: keep the entry until the user clears it. This is Windows' stock
    /// behaviour and the default here.</summary>
    public const int CenterUnlimited = -1;

    /// <summary>Center: never keep an entry — it goes as soon as the popup closes.</summary>
    public const int CenterNone = 0;

    private readonly ISettingsStore _store;
    private readonly ILogger<ToastLifetimeService> _logger;
    private int _popupSeconds = PopupMaxSeconds;
    private int _centerSeconds = CenterUnlimited;

    public ToastLifetimeService(ISettingsStore store, ILogger<ToastLifetimeService> logger)
    {
        _store = store;
        _logger = logger;
    }

    /// <summary>Seconds the popup stays on screen, 0–<see cref="PopupMaxSeconds"/>.
    /// <see cref="PopupNone"/> (0) shows no popup at all; <see cref="PopupMaxSeconds"/> (the
    /// default) leaves the duration to Windows; anything in between closes the popup at exactly
    /// that many seconds.</summary>
    public int PopupSeconds => _popupSeconds;

    /// <summary>Seconds the entry stays in the Notification Center.
    /// <see cref="CenterUnlimited"/> (-1, the default) keeps it until the user clears it;
    /// <see cref="CenterNone"/> (0) never keeps one, so notifications can't pile up; any positive
    /// value is a lifetime in seconds, with no upper bound.</summary>
    public int CenterSeconds => _centerSeconds;

    /// <summary>True when the popup should be closed by us rather than by Windows. At the
    /// ceiling we deliberately stand back: the OS duration also honours the user's "show
    /// notifications for" accessibility setting, and overriding it would quietly undo that.</summary>
    public bool ClosesPopupEarly => _popupSeconds > PopupNone && _popupSeconds < PopupMaxSeconds;

    /// <summary>True when the settings ask for a toast nobody could ever see: no popup AND no
    /// Center entry. Callers skip the notification instead of doing the work for nothing.</summary>
    public bool NotificationsEffectivelyInvisible => _popupSeconds == PopupNone && _centerSeconds == CenterNone;

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            var popupRaw = await _store.GetAsync(PopupSecondsKey, cancellationToken).ConfigureAwait(false);
            if (int.TryParse(popupRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var popup))
                _popupSeconds = ClampPopup(popup);

            var centerRaw = await _store.GetAsync(CenterSecondsKey, cancellationToken).ConfigureAwait(false);
            if (int.TryParse(centerRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var center))
                _centerSeconds = ClampCenter(center);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Toast lifetime settings failed to load; using Windows defaults");
        }
    }

    public async Task SetPopupSecondsAsync(int seconds, CancellationToken cancellationToken)
    {
        var clamped = ClampPopup(seconds);
        if (clamped == _popupSeconds) return;
        _popupSeconds = clamped;
        await _store.SetAsync(PopupSecondsKey, clamped.ToString(CultureInfo.InvariantCulture),
            sensitive: false, cancellationToken).ConfigureAwait(true);
    }

    public async Task SetCenterSecondsAsync(int seconds, CancellationToken cancellationToken)
    {
        var clamped = ClampCenter(seconds);
        if (clamped == _centerSeconds) return;
        _centerSeconds = clamped;
        await _store.SetAsync(CenterSecondsKey, clamped.ToString(CultureInfo.InvariantCulture),
            sensitive: false, cancellationToken).ConfigureAwait(true);
    }

    private static int ClampPopup(int seconds) => Math.Clamp(seconds, PopupNone, PopupMaxSeconds);

    /// <summary>No upper bound — a lifetime of a week in the Center is the user's business.
    /// Anything below the sentinel collapses onto it: there's no meaning for "-5 seconds", and
    /// persisting it would store a value the UI can't represent.</summary>
    private static int ClampCenter(int seconds) => seconds < CenterUnlimited ? CenterUnlimited : seconds;
}
