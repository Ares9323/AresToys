using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;
using Microsoft.Extensions.Logging;
using AresToys.App.Resources;
using AresToys.App.Services;

namespace AresToys.App.Markup;

/// <summary>XAML markup extension that resolves a localized string by key, e.g.
/// <c>{Markup:Loc Common_Cancel}</c>. Internally it returns a Binding to a singleton helper
/// that exposes Strings.ResourceManager — when <see cref="LocalizationService.CultureChanged"/>
/// fires, the helper raises PropertyChanged on a single shared property name and every Loc
/// binding refreshes itself, so language switches are live across the whole UI without
/// rebuilding visual trees.</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class LocExtension : MarkupExtension
{
    public string Key { get; set; } = string.Empty;

    public LocExtension() { }
    public LocExtension(string key) { Key = key; }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        // Return the value through a Binding that follows the same shared LocalizedStrings
        // singleton; the indexer takes the key and looks up Strings.ResourceManager. When the
        // service raises CultureChanged, LocalizedStrings broadcasts PropertyChanged("Item[]")
        // and every Binding re-fetches.
        var binding = new Binding($"[{Key}]")
        {
            Source = LocalizedStrings.Instance,
            Mode = BindingMode.OneWay,
            FallbackValue = Key,
        };
        return binding.ProvideValue(serviceProvider);
    }
}

/// <summary>Indexer-style accessor used by <see cref="LocExtension"/>. Singleton because every
/// Loc binding shares it as Source. Hooks LocalizationService.CultureChanged once when the
/// service is set, and from then on every culture flip raises a single Item[] PropertyChanged
/// that re-fetches all live Loc bindings.</summary>
public sealed class LocalizedStrings : INotifyPropertyChanged
{
    public static LocalizedStrings Instance { get; } = new();

    private LocalizationService? _service;
    // Instance-level culture cache. We intentionally don't rely on Strings.Culture (static) or
    // Thread.CurrentThread.CurrentUICulture: both have been observed flipping back to the OS
    // default between ApplyToThread and the first XAML binding eval. The instance field is set
    // explicitly by LocalizationService and survives whatever process-wide culture mutation
    // the framework decides to do later in startup.
    private CultureInfo? _culture;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The culture the singleton currently resolves keys in. Null until Attach + the
    /// first culture sync; callsites should fall back to <see cref="CultureInfo.CurrentUICulture"/>
    /// in that window.</summary>
    public CultureInfo? Culture => _culture;

    /// <summary>Wire the singleton to the live LocalizationService. Called once during app
    /// startup (App.xaml.cs) — subsequent calls re-subscribe so LoadAsync ordering is robust.</summary>
    public void Attach(LocalizationService service)
    {
        if (_service is not null) _service.CultureChanged -= OnCultureChanged;
        _service = service;
        _service.CultureChanged += OnCultureChanged;
        // Pull current culture immediately even if LoadAsync hasn't run yet — that way the very
        // first binding eval, which is timed unpredictably (WPF lazy materialisation), reads a
        // sensible value rather than CurrentUICulture (which the framework keeps resetting).
        SyncCultureFromService();
    }

    /// <summary>Refresh the cached culture from the attached service. Public so
    /// LocalizationService can call it after each ApplyToThread (i.e. whenever the user picks a
    /// new language in the Settings combo).</summary>
    public void SyncCultureFromService()
    {
        if (_service is null) return;
        var tag = _service.CurrentTag;
        try
        {
            _culture = string.IsNullOrEmpty(tag)
                ? CultureInfo.InstalledUICulture
                : CultureInfo.GetCultureInfo(tag);
        }
        catch (CultureNotFoundException) { _culture = CultureInfo.InvariantCulture; }
    }

    private void OnCultureChanged(object? sender, EventArgs e)
    {
        SyncCultureFromService();
        // "Item[]" notifies every Binding using a path like "[KeyName]" on this object.
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
    }

    private static int _diagFired;

    public string this[string key]
    {
        get
        {
            if (string.IsNullOrEmpty(key)) return string.Empty;
            try
            {
                // Source-of-truth precedence: instance field on this singleton (set explicitly
                // by LocalizationService.ApplyToThread → SyncCultureFromService) → static
                // Strings.Culture (kept in sync as a courtesy to typed accessors) →
                // CurrentUICulture (only used in the tiny window before Attach). WPF/Hosting was
                // observed clobbering both static fields under us, but the instance field on a
                // managed singleton survives the framework's mood swings.
                var culture = _culture ?? Strings.Culture ?? CultureInfo.CurrentUICulture;
                var value = Strings.ResourceManager.GetString(key, culture) ?? key;
                // Log the first XAML-binding lookup so we can see, end-to-end, what culture the
                // WPF dispatcher thread is reading at binding-evaluation time and what value
                // ResourceManager returns for it. Fires exactly once.
                if (System.Threading.Interlocked.CompareExchange(ref _diagFired, 1, 0) == 0)
                {
                    // Route through Application.Current → debug log service via the localization
                    // service we hold a reference to. Trace.TraceInformation alone wouldn't show
                    // up in the in-app Debug Console (different sink); ILogger does.
                    System.Diagnostics.Trace.TraceInformation(
                        "[LocalizedStrings] first XAML lookup: key='{0}' culture='{1}' threadId={2} returned='{3}'",
                        key, culture.Name, Environment.CurrentManagedThreadId, value);
                    try
                    {
                        var app = System.Windows.Application.Current;
                        if (app is App arestoysApp)
                        {
                            var sp = arestoysApp.Services;
                            var lf = sp.GetService(typeof(Microsoft.Extensions.Logging.ILoggerFactory)) as Microsoft.Extensions.Logging.ILoggerFactory;
                            lf?.CreateLogger("LocalizedStrings").LogInformation(
                                "First XAML lookup: key='{Key}' culture='{Culture}' threadId={ThreadId} returned='{Value}'",
                                key, culture.Name, Environment.CurrentManagedThreadId, value);
                        }
                    }
                    catch { /* diag only, never throw */ }
                }
                return value;
            }
            catch
            {
                return key;
            }
        }
    }
}
