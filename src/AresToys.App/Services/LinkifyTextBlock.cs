using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace AresToys.App.Services;

/// <summary>Attached property: <c>LinkifyTextBlock.Text</c> behaves like the regular
/// <see cref="TextBlock.Text"/> binding but recognises <c>http(s)://…</c> substrings and
/// renders them as <see cref="Hyperlink"/> inlines that open in the user's default browser
/// when clicked. Used in the uploader-config dialog so the documentation strings ("Create
/// an app at https://portal.azure.com → …") are actually navigable instead of being plain
/// text the user has to retype.</summary>
public static class LinkifyTextBlock
{
    // Conservative URL pattern: requires explicit scheme. Trailing common punctuation
    // (period, comma, paren, bracket, semi/colon) is stripped so a sentence like "open
    // https://example.com." doesn't carry the trailing period into the link.
    private static readonly Regex UrlPattern = new(
        @"https?://[^\s<>""']+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(LinkifyTextBlock),
        new PropertyMetadata(string.Empty, OnTextChanged));

    public static string GetText(DependencyObject obj) => (string)obj.GetValue(TextProperty);
    public static void SetText(DependencyObject obj, string value) => obj.SetValue(TextProperty, value);

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock tb) return;
        tb.Inlines.Clear();
        var text = e.NewValue as string;
        if (string.IsNullOrEmpty(text)) return;

        var lastIndex = 0;
        foreach (Match m in UrlPattern.Matches(text))
        {
            if (m.Index > lastIndex)
                tb.Inlines.Add(new Run(text[lastIndex..m.Index]));

            var raw = m.Value;
            // Trim trailing sentence-ending punctuation that never belongs to a URL — these
            // get added as a plain Run so the visual text stays intact, just outside the link.
            var trimmed = raw.TrimEnd('.', ',', ')', ']', ';', ':', '!', '?');
            var trailing = raw[trimmed.Length..];

            if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            {
                var hyperlink = new Hyperlink(new Run(trimmed)) { NavigateUri = uri };
                hyperlink.RequestNavigate += (_, args) =>
                {
                    try { Process.Start(new ProcessStartInfo(args.Uri.ToString()) { UseShellExecute = true }); }
                    catch { /* user cancelled / no browser */ }
                    args.Handled = true;
                };
                tb.Inlines.Add(hyperlink);
            }
            else
            {
                tb.Inlines.Add(new Run(trimmed));
            }

            if (trailing.Length > 0) tb.Inlines.Add(new Run(trailing));
            lastIndex = m.Index + m.Length;
        }
        if (lastIndex < text.Length)
            tb.Inlines.Add(new Run(text[lastIndex..]));
    }
}
