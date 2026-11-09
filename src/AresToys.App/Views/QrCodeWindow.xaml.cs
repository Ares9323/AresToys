using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace AresToys.App.Views;

public partial class QrCodeWindow : Window
{
    public QrCodeWindow(BitmapSource qr, string text)
    {
        InitializeComponent();
        QrImage.Source = qr;
        UrlText.Text = text;
        CloseButton.Click += (_, _) => Close();
        CopyButton.Click += (_, _) =>
        {
            try { System.Windows.Clipboard.SetText(text); }
            catch { /* clipboard may be locked by another app — silent fail is fine here */ }
        };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }
}
