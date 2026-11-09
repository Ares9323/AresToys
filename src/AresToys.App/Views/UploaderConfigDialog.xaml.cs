using System.Windows;
using System.Windows.Controls;
using AresToys.App.Services;
using AresToys.App.ViewModels;
using AresToys.PluginContracts;
using AresToys.Uploaders.OAuth;

namespace AresToys.App.Views;

/// <summary>Modal form for editing one uploader's settings (Imgur Client ID, Pastebin API key,
/// Gist PAT, etc.). Pure data-driven — the field list comes from
/// <see cref="IConfigurableUploader.GetSettings"/>, the template selector picks the UI per field
/// type. PasswordBox is hooked up manually because WPF's PasswordBox doesn't support binding for
/// security reasons. OAuth uploaders additionally get the <c>OAuthSection</c> sign-in panel.</summary>
public partial class UploaderConfigDialog : Window
{
    private readonly UploaderConfigDialogViewModel _viewModel;

    public UploaderConfigDialog(IUploader uploader, IPluginConfigStore store, OAuthFlowService? oauthFlowService = null)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        _viewModel = new UploaderConfigDialogViewModel(uploader, store, oauthFlowService);
        DataContext = _viewModel;
        Loaded += async (_, _) => await _viewModel.LoadAsync(CancellationToken.None);
        // Cancel any in-flight OAuth listener if the user closes the dialog mid-flow, then dispose
        // the underlying CancellationTokenSource on Closed (after the cancellation has propagated).
        Closing += async (_, _) =>
        {
            if (_viewModel.OAuthSection is not null)
                await _viewModel.OAuthSection.CancelSignInAsync();
        };
        Closed += (_, _) => _viewModel.OAuthSection?.Dispose();
    }

    private void OnPasswordBoxLoaded(object sender, RoutedEventArgs e)
    {
        // Initial sync from VM → control. Re-runs whenever the template realises (e.g. virtualization
        // or LoadAsync completing after the box was already constructed) so the masked field
        // shows the persisted value.
        if (sender is PasswordBox box && box.Tag is StringFieldViewModel field)
        {
            if (box.Password != field.Value) box.Password = field.Value;
        }
    }

    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        // Inverse direction: control → VM. Bypassing PasswordChar exposure (the standard guidance
        // for WPF) — we never read the live string outside this handler.
        if (sender is PasswordBox box && box.Tag is StringFieldViewModel field)
            field.Value = box.Password;
    }

    private async void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            await _viewModel.SaveAsync(CancellationToken.None);
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't save settings: {ex.Message}",
                "AresToys", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
