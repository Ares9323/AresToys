using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AresToys.PluginContracts;
using AresToys.Uploaders.OAuth;

namespace AresToys.App.ViewModels;

/// <summary>Backs the per-uploader settings dialog. Walks <see cref="IConfigurableUploader.GetSettings"/>
/// once at construction, materializes one strongly-typed field VM per descriptor, loads current
/// values from the per-uploader <see cref="IPluginConfigStore"/>, and writes them all back on
/// <see cref="SaveAsync"/>. Cancelling the dialog discards the in-memory edits. When the uploader
/// also implements <see cref="IOAuthUploader"/>, an extra <see cref="OAuthSection"/> drives the
/// sign-in / sign-out flow above the field list.</summary>
public sealed partial class UploaderConfigDialogViewModel : ObservableObject
{
    private readonly IPluginConfigStore _store;

    public UploaderConfigDialogViewModel(
        IUploader uploader,
        IPluginConfigStore store,
        OAuthFlowService? oauthFlowService = null)
    {
        ArgumentNullException.ThrowIfNull(uploader);
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        DisplayName = uploader.DisplayName;
        Fields = [];

        if (uploader is IOAuthUploader oauthUploader && oauthFlowService is not null)
        {
            OAuthSection = new OAuthSignInViewModel(oauthUploader, store, oauthFlowService);
        }

        if (uploader is IConfigurableUploader configurable)
        {
            foreach (var setting in configurable.GetSettings())
            {
                UploaderConfigFieldViewModel? field = setting switch
                {
                    StringSetting s   => new StringFieldViewModel(s),
                    BoolSetting b     => new BoolFieldViewModel(b),
                    DropdownSetting d => new DropdownFieldViewModel(d),
                    _ => null,
                };
                if (field is not null) Fields.Add(field);
            }
        }
    }

    public string DisplayName { get; }
    public ObservableCollection<UploaderConfigFieldViewModel> Fields { get; }

    /// <summary>Non-null when the uploader implements <see cref="IOAuthUploader"/>. The dialog
    /// renders the sign-in panel only when this is set.</summary>
    public OAuthSignInViewModel? OAuthSection { get; }

    public bool HasOAuthSection => OAuthSection is not null;

    /// <summary>Pull stored values into each field VM. Sensitive strings round-trip through DPAPI
    /// transparently — the store decrypts on read.</summary>
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        foreach (var field in Fields)
        {
            var raw = await _store.GetAsync(field.Key, cancellationToken).ConfigureAwait(true);
            field.LoadFromStoredValue(raw);
        }
        if (OAuthSection is not null)
            await OAuthSection.RefreshStatusAsync(cancellationToken).ConfigureAwait(true);
    }

    /// <summary>Persist every field's current value. Empty strings are deleted instead of
    /// stored so the next load falls back to defaults / "missing" rather than "" (some
    /// uploaders fail validation on empty-string credentials). The OAuth token (if any) is
    /// written by the sign-in flow itself, not here.</summary>
    public async Task SaveAsync(CancellationToken cancellationToken)
    {
        foreach (var field in Fields)
        {
            var value = field.GetValueForStorage();
            if (string.IsNullOrEmpty(value))
                await _store.DeleteAsync(field.Key, cancellationToken).ConfigureAwait(true);
            else
                await _store.SetAsync(field.Key, value, field.Sensitive, cancellationToken).ConfigureAwait(true);
        }
    }
}

/// <summary>Drives the sign-in panel for OAuth uploaders. Three-state UI: not signed in
/// (button: "Sign in"), busy (spinner + cancel), signed in (label + "Sign out"). The
/// <see cref="SignInAsync"/> path opens the user's browser through <see cref="OAuthFlowService"/>;
/// the dialog cancels it via <see cref="CancelSignInAsync"/> when the window closes mid-flow,
/// and <see cref="Dispose"/> tears down the CancellationTokenSource so the dialog can be
/// closed without leaking the unmanaged WaitHandle inside it.</summary>
public sealed partial class OAuthSignInViewModel : ObservableObject, IDisposable
{
    private readonly IOAuthUploader _uploader;
    private readonly IPluginConfigStore _store;
    private readonly OAuthFlowService _oauth;
    private CancellationTokenSource? _signInCts;

    public OAuthSignInViewModel(IOAuthUploader uploader, IPluginConfigStore store, OAuthFlowService oauth)
    {
        _uploader = uploader;
        _store = store;
        _oauth = oauth;
    }

    [ObservableProperty]
    private bool _isSignedIn;

    [ObservableProperty]
    private string? _signedInDisplayName;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _statusMessage;

    /// <summary>Re-reads the stored token + (if signed in and provider supports it) the account
    /// display name. Called on dialog open and after sign-in / sign-out.</summary>
    public async Task RefreshStatusAsync(CancellationToken cancellationToken)
    {
        var token = await OAuthTokenStore.LoadAsync(_store, cancellationToken).ConfigureAwait(true);
        if (token is null || string.IsNullOrEmpty(token.AccessToken))
        {
            IsSignedIn = false;
            SignedInDisplayName = null;
            return;
        }
        IsSignedIn = true;
        // Best-effort label fetch. A failure here (expired token, network blip) doesn't undo
        // the signed-in state — the actual upload path will surface real auth errors.
        try
        {
            SignedInDisplayName = await _uploader.GetSignedInDisplayNameAsync(token.AccessToken, cancellationToken).ConfigureAwait(true);
        }
        catch { SignedInDisplayName = null; }
    }

    [RelayCommand]
    private async Task SignInAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        StatusMessage = "Waiting for browser sign-in…";
        _signInCts?.Dispose();
        _signInCts = new CancellationTokenSource();
        try
        {
            var request = _uploader.BuildOAuthRequest();
            var token = await _oauth.AuthorizeAsync(request, _signInCts.Token).ConfigureAwait(true);
            await OAuthTokenStore.SaveAsync(_store, token, _signInCts.Token).ConfigureAwait(true);
            await RefreshStatusAsync(_signInCts.Token).ConfigureAwait(true);
            StatusMessage = null;
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Sign-in cancelled.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Sign-in failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SignOutAsync()
    {
        await OAuthTokenStore.ClearAsync(_store, CancellationToken.None).ConfigureAwait(true);
        IsSignedIn = false;
        SignedInDisplayName = null;
        StatusMessage = null;
    }

    /// <summary>Called by the dialog code-behind when the window is closing while a sign-in is
    /// in flight — cancels the loopback listener so the awaited task unwinds cleanly.</summary>
    public Task CancelSignInAsync()
    {
        try { _signInCts?.Cancel(); } catch (ObjectDisposedException) { /* already torn down */ }
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _signInCts?.Dispose();
        _signInCts = null;
    }
}

/// <summary>Common base for the 3 field shapes. Holds the descriptor's display metadata and
/// abstract <see cref="LoadFromStoredValue"/> / <see cref="GetValueForStorage"/> hooks each
/// subclass implements over its own typed value. <see cref="Sensitive"/> tells the store to
/// DPAPI-encrypt on write — currently true only for <see cref="StringFieldViewModel"/>.
/// Inherits <see cref="ObservableObject"/> so the <c>[ObservableProperty]</c> source generators
/// in the concrete subclasses can hook into <c>INotifyPropertyChanged</c>.</summary>
public abstract class UploaderConfigFieldViewModel : ObservableObject
{
    protected UploaderConfigFieldViewModel(string key, string label, string? description)
    {
        Key = key;
        Label = label;
        Description = description;
    }

    public string Key { get; }
    public string Label { get; }
    public string? Description { get; }
    public virtual bool Sensitive => false;

    public abstract void LoadFromStoredValue(string? raw);
    public abstract string? GetValueForStorage();
}

public sealed partial class StringFieldViewModel : UploaderConfigFieldViewModel
{
    private readonly string _default;

    public StringFieldViewModel(StringSetting setting)
        : base(setting.Key, setting.Label, setting.Description)
    {
        Placeholder = setting.Placeholder;
        Sensitive = setting.Sensitive;
        _default = setting.Default ?? string.Empty;
        // Pre-populate so a freshly-opened dialog (no stored value yet) shows the default
        // instead of an empty box. Overridden by LoadFromStoredValue when the user has saved.
        _value = _default;
    }

    public string? Placeholder { get; }
    public override bool Sensitive { get; }

    [ObservableProperty]
    private string _value = string.Empty;

    public override void LoadFromStoredValue(string? raw)
        => Value = !string.IsNullOrEmpty(raw) ? raw : _default;
    public override string? GetValueForStorage() => Value;
}

public sealed partial class BoolFieldViewModel : UploaderConfigFieldViewModel
{
    private readonly bool _default;

    public BoolFieldViewModel(BoolSetting setting)
        : base(setting.Key, setting.Label, setting.Description)
    {
        _default = setting.Default;
        _value = setting.Default;
    }

    [ObservableProperty]
    private bool _value;

    public override void LoadFromStoredValue(string? raw)
        => Value = bool.TryParse(raw, out var parsed) ? parsed : _default;

    public override string? GetValueForStorage() => Value.ToString();
}

public sealed partial class DropdownFieldViewModel : UploaderConfigFieldViewModel
{
    public DropdownFieldViewModel(DropdownSetting setting)
        : base(setting.Key, setting.Label, setting.Description)
    {
        Options = setting.Options;
        // Default = first option (descriptor convention). Set initial selection to it; LoadFromStoredValue
        // overrides with the persisted selection when one exists.
        _selectedOption = setting.Options.Count > 0 ? setting.Options[0] : null;
    }

    public IReadOnlyList<DropdownOption> Options { get; }

    [ObservableProperty]
    private DropdownOption? _selectedOption;

    public override void LoadFromStoredValue(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return; // keep the default
        var match = Options.FirstOrDefault(o => o.Value == raw);
        if (match is not null) SelectedOption = match;
    }

    public override string? GetValueForStorage() => SelectedOption?.Value;
}
