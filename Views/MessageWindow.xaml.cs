using System.Windows;
using System.Windows.Input;

namespace FanControlApp;

/// <summary>Themed message box: Show() for notices, Confirm() for yes/no questions.</summary>
public partial class MessageWindow : Window
{
    private bool _result;
    private bool _secondaryClicked;

    private MessageWindow(Window? owner, string header, string body,
                          string primary, string? secondary, bool copyButton = false,
                          bool autoWidth = false)
    {
        InitializeComponent();

        // autoWidth = the window grows so pre-formatted lines never wrap.
        if (autoWidth)
        {
            Width = double.NaN;
            MinWidth = 430;
            SizeToContent = SizeToContent.WidthAndHeight;
            BodyText.TextWrapping = TextWrapping.NoWrap;
        }

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

    /// <summary>A one-button notice; copyButton adds "Copy info" for the clipboard.</summary>
    public static void Show(Window? owner, string header, string body, string button = "OK",
                            bool copyButton = false, bool autoWidth = false)
        => new MessageWindow(owner, header, body, button, null, copyButton, autoWidth).ShowDialog();

    /// <summary>A two-button question. True = the primary (right) button.</summary>
    public static bool Confirm(Window? owner, string header, string body,
                               string primary, string secondary = "Cancel")
    {
        var w = new MessageWindow(owner, header, body, primary, secondary);
        w.ShowDialog();
        return w._result;
    }

    /// <summary>Risky question: the safe button takes Enter and Escape; true only on a click of the risky one.</summary>
    public static bool ConfirmRisky(Window? owner, string header, string body,
                                    string risky, string safe)
    {
        var w = new MessageWindow(owner, header, body, safe, risky);
        w.PrimaryButton.IsCancel = true;
        w.SecondaryButton.IsCancel = false;

        // Can appear at boot with no main window, so never let it hide.
        w.Topmost = true;
        w.ShowInTaskbar = true;
        w.ShowDialog();
        return w._secondaryClicked;
    }

    private void OnPrimaryClick(object sender, RoutedEventArgs e)
    {
        _result = true;
        Close();
    }

    private void OnSecondaryClick(object sender, RoutedEventArgs e)
    {
        _result = false;
        _secondaryClicked = true;
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
