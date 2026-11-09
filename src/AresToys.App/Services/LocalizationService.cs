using System.ComponentModel;
using System.Globalization;
using Microsoft.Extensions.Logging;
using AresToys.Storage.Settings;

namespace AresToys.App.Services;

/// <summary>App-wide UI culture switcher. Reads / writes the user's preferred language to
/// settings (<c>ui.language</c>) and applies it to the current thread + the app default. Other
/// services and the <c>Loc</c> markup extension subscribe to <see cref="CultureChanged"/> to
/// re-evaluate their bindings live — no restart required for resx-driven strings.
///
/// Empty / "system" → fall back to <see cref="CultureInfo.InstalledUICulture"/>. Unknown
/// culture string → fall back to invariant English (the resx base). Bad input never crashes
/// the app, the language just stays English.</summary>
public sealed class LocalizationService : INotifyPropertyChanged
{
    private const string LanguageKey = "ui.language";
    public const string SystemDefaultMarker = "";

    /// <summary>Languages bundled in this build. Order = order shown in the Settings dropdown.
    /// Adding a new culture: drop a <c>Strings.&lt;tag&gt;.resx</c> next to <c>Strings.resx</c>
    /// and append the tag here.</summary>
    public static readonly IReadOnlyList<(string Tag, string DisplayName)> AvailableLanguages = new[]
    {
        (SystemDefaultMarker, "System default"),
        ("en", "English"),
        ("it", "Italiano"),
    };

    private readonly ISettingsStore _settings;
    private readonly ILogger<LocalizationService> _logger;
    private string _currentTag = SystemDefaultMarker;

    public LocalizationService(ISettingsStore settings, ILogger<LocalizationService> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? CultureChanged;

    /// <summary>The persisted culture tag the user picked, or <see cref="SystemDefaultMarker"/>
    /// when they want the OS UI culture. Round-tripped to settings on every change.</summary>
    public string CurrentTag
    {
        get => _currentTag;
        set
        {
            if (_currentTag == value) return;
            _currentTag = value;
            ApplyToThread();
            _ = _settings.SetAsync(LanguageKey, value, sensitive: false, CancellationToken.None);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentTag)));
            CultureChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Pull the persisted tag and apply. Called once at startup, before any UI is
    /// rendered, so the first frame already paints in the chosen language.
    ///
    /// IMPORTANT: ApplyToThread mutates Thread.CurrentThread.CurrentUICulture, which only
    /// affects whichever thread runs the call. The settings read uses ConfigureAwait(false)
    /// (SQLite IO has no thread affinity), so the continuation can resume on a thread-pool
    /// thread — applying the culture there leaves the actual UI thread on the OS default,
    /// and every binding still reads English. Marshal the apply onto the Dispatcher so the
    /// main UI thread itself flips, regardless of where the await resumed.</summary>
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        var raw = await _settings.GetAsync(LanguageKey, cancellationToken).ConfigureAwait(false) ?? SystemDefaultMarker;
        _currentTag = raw;
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
            await dispatcher.InvokeAsync(ApplyToThread);
        else
            ApplyToThread();
        ProbeSatellite();
        // Fire CultureChanged even on first load so any surface that materialised before the
        // persisted language was applied (in particular: WPF Binding objects whose first fetch
        // happens between InitializeComponent and the actual ApplyToThread call) gets a chance
        // to refresh through LocalizedStrings → "Item[]" PropertyChanged. App.xaml.cs now
        // attaches LocalizedStrings BEFORE calling LoadAsync, so this event has a live listener.
        CultureChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyToThread()
    {
        try
        {
            var culture = string.IsNullOrEmpty(_currentTag)
                ? CultureInfo.InstalledUICulture
                : CultureInfo.GetCultureInfo(_currentTag);
            // Set both the UI culture (resource lookups) AND the formatting culture, on both the
            // current thread and the process default. The `Default*` properties cover threads not
            // yet started (background workers, hosted services); the `Thread.CurrentThread.*`
            // settings make the change immediate on the UI thread that's about to paint the first
            // frame. Without the formatting half, dates / numbers in user-facing strings would
            // still render with the OS-default culture even when the UI is translated.
            CultureInfo.DefaultThreadCurrentUICulture = culture;
            CultureInfo.DefaultThreadCurrentCulture   = culture;
            Thread.CurrentThread.CurrentUICulture     = culture;
            Thread.CurrentThread.CurrentCulture       = culture;
            // Pin a static override on Strings + LocalizedStrings: WPF / Hosting / something
            // resets Thread.CurrentThread.CurrentUICulture between ApplyToThread and the first
            // binding eval (observed: en-GB came back even after we set 'it' on the same UI
            // thread). The static override is read by both the typed accessors (Strings.Foo) and
            // the {Markup:Loc Foo} indexer, so resource lookups stay deterministic regardless of
            // any rogue mutation to CurrentUICulture.
            Resources.Strings.Culture = culture;
            // Sanity check: read it back. If it's null, something is hosing the setter (e.g.
            // a regenerated Designer.cs without our override property, or a duplicate Strings
            // type loaded from a different ALC).
            var readBack = Resources.Strings.Culture;
            _logger.LogInformation("Localization: Strings.Culture readback after assign = '{ReadBack}'",
                readBack?.Name ?? "<null>");
            // Authoritative source for the LocExtension binding (instance field on the singleton —
            // survives anything that resets static fields). LocalizationService.LoadAsync fires
            // CultureChanged after this method returns, which also triggers SyncCultureFromService;
            // calling it here directly covers the very-first-load path where CultureChanged hasn't
            // gone out yet but XAML is already materialising.
            Markup.LocalizedStrings.Instance.SyncCultureFromService();
            _logger.LogInformation("Localization: applied culture '{Culture}' (tag='{Tag}', threadId={Tid}, isUI={IsUI})",
                culture.Name, _currentTag,
                Environment.CurrentManagedThreadId,
                System.Windows.Application.Current?.Dispatcher.CheckAccess() ?? false);
        }
        catch (CultureNotFoundException ex)
        {
            _logger.LogWarning(ex, "Localization: unknown culture tag '{Tag}', falling back to invariant", _currentTag);
            CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
            Thread.CurrentThread.CurrentUICulture = CultureInfo.InvariantCulture;
        }
    }

    /// <summary>Sanity-check that the satellite assembly for the current culture is actually
    /// shipping with the build. The first time we noticed translations didn't apply on launch,
    /// the satellite was being skipped by the publish step — surfacing this in the log makes
    /// future regressions obvious without a debugger session.
    ///
    /// We do TWO checks: (1) GetResourceSet without parent lookup tells us whether a per-culture
    /// .resources stream actually exists for this culture. (2) GetString on a known key with the
    /// current culture lets us see what value end-users will see — if it returns the English
    /// string, every binding will too, no matter how correctly CurrentUICulture is wired.</summary>
    private void ProbeSatellite()
    {
        try
        {
            var culture = Thread.CurrentThread.CurrentUICulture;
            if (culture.TwoLetterISOLanguageName == "en") return; // base resx covers it

            var rm = Resources.Strings.ResourceManager;
            var rs = rm.GetResourceSet(culture, createIfNotExists: true, tryParents: false);
            if (rs is null)
            {
                _logger.LogWarning("Localization: no satellite resources found for '{Culture}'. Translations will fall back to English. Verify <output>/{Culture}/AresToys.resources.dll exists; add the tag to <SatelliteResourceLanguages> in AresToys.App.csproj if missing.",
                    culture.Name, culture.Name);
                return;
            }

            // Read a known key both ways — neutral (English) + the requested culture. If they
            // match we're getting the fallback even though a culture-specific resource set
            // exists; that'd point at a key-name typo or a stale resx.
            var translated = rm.GetString("Tray_Quit", culture);
            var neutral    = rm.GetString("Tray_Quit", System.Globalization.CultureInfo.InvariantCulture);
            if (string.Equals(translated, neutral, StringComparison.Ordinal))
                _logger.LogWarning("Localization: satellite for '{Culture}' present but Tray_Quit returns '{Value}' (same as neutral) — the resx may be missing keys.",
                    culture.Name, translated);
            else
                _logger.LogInformation("Localization: '{Culture}' satellite OK — Tray_Quit='{Translated}' (neutral was '{Neutral}').",
                    culture.Name, translated, neutral);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Localization: satellite probe failed");
        }
    }
}
