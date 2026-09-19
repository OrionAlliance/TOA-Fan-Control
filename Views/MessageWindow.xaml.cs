using System.Windows;
using System.Windows.Input;

namespace FanControlApp;

/// <summary>
/// The app's own message box - same card styling as every other window, because
/// a native MessageBox in the middle of a themed app looks like a stranger
/// walked in. Show() for notices, Confirm() for yes/no questions.
/// </summary>
public partial class MessageWindow : Window
{
    private bool _result;

    private MessageWindow(Window? owner, string header, string body,
                          string primary, string? secondary, bool copyButton = false)
    {
        InitializeComponent();

        if (owner is { IsVisible: true })
        {
            Owner = owner;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }

        HeaderText.Text = header;
        BodyText.Text = body;
        PrimaryButton.Content = primary;

        if (secondary != null)
        {
            SecondaryButton.Content = secondary;
            SecondaryButton.Visibility = Visibility.Visible;
        }
        if (copyButton) CopyButton.Visibility = Visibility.Visible;
    }

    /// <summary>A notice with a single button - copyButton adds a "Copy info"
    /// that puts the whole notice on the clipboard.</summary>
    public static void Show(Window? owner, string header, string body, string button = "OK",
                            bool copyButton = false)
        => new MessageWindow(owner, header, body, button, null, copyButton).ShowDialog();

    /// <summary>A two-button question. True = the primary (right) button.</summary>
    public static bool Confirm(Window? owner, string header, string body,
                               string primary, string secondary = "Cancel")
    {
        var w = new MessageWindow(owner, header, body, primary, secondary);
        w.ShowDialog();
        return w._result;
    }

    private void OnPrimaryClick(object sender, RoutedEventArgs e)
    {
        _result = true;
        Close();
    }

    private void OnSecondaryClick(object sender, RoutedEventArgs e)
    {
        _result = false;
        Close();
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        // CRLF so the paste line-breaks everywhere, oldest Notepad included.
        string text = HeaderText.Text + "\r\n\r\n" + BodyText.Text.Replace("\n", "\r\n");
        try
        {
            Clipboard.SetText(text);
            CopyButton.Content = "Copied!";
        }
        catch { CopyButton.Content = "Try again"; } // another app held the clipboard
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        try { DragMove(); }
        catch (InvalidOperationException) { /* button already released */ }
    }
}
