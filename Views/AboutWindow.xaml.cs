using System.Windows;
using System.Windows.Input;
using FanControlApp.Infrastructure;

namespace FanControlApp;

/// <summary>Settings → About: what the app does, copyright, and legal disclaimer.</summary>
public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();

        // Same version source as the title bar, so they never disagree.
        TitleLine.Text = $"TOA - Fan Control  {AppVersion.Display}";
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        try { DragMove(); }
        catch (InvalidOperationException) { /* button already released */ }
    }

    private void OnLinkClick(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        Browser.Open(e.Uri.AbsoluteUri);
        e.Handled = true;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
