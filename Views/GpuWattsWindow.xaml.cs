using System.Windows;

namespace FanControlApp;

/// <summary>
/// The KISS answer to an unlisted GPU: the user is sitting next to the card and
/// its max watts is one search away - so just ask them. No accounts, no waiting
/// for a library update, and the gauge works thirty seconds from now.
/// </summary>
public partial class GpuWattsWindow : Window
{
    /// <summary>The accepted wattage, or null if they skipped.</summary>
    public int? Watts { get; private set; }

    private readonly string _cardName;

    public GpuWattsWindow(Window? owner, string cardName)
    {
        InitializeComponent();
        _cardName = cardName;

        if (owner is { IsVisible: true })
        {
            Owner = owner;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            // Started minimized to tray: nothing anchors this dialog, and modal +
            // borderless + hidden owner = "the app looks hung" if it gets buried.
            // A taskbar button and Topmost keep it findable until it's answered.
            ShowInTaskbar = true;
            Topmost = true;
        }

        BodyText.Text =
            $"The cyan markers can show your GPU's TRUE load - watts pulled versus " +
            $"the card's maximum - but the app doesn't know the maximum for\n\n" +
            $"    {cardName}\n\n" +
            $"You can fix that in 30 seconds: the search button below finds it - " +
            $"the wattage (TDP) is the first number you'll see. Type it here and " +
            $"the markers switch to true load immediately.\n\n" +
            $"Or skip it: the markers then show how BUSY the GPU is instead - " +
            $"honest, just a different measure. Temperatures and fan control are " +
            $"not affected either way.";

        SearchButtonText.Text = $"Search Google for \"{cardName} TDP\"";

        Loaded += (_, _) => WattsBox.Focus();
    }

    private void OnSearchClick(object sender, RoutedEventArgs e)
    {
        string url = "https://www.google.com/search?q="
                     + Uri.EscapeDataString($"{_cardName} TDP");
        System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (int.TryParse(WattsBox.Text.Trim(), out int w) && w is >= 30 and <= 1000)
        {
            Watts = w;
            Close();
            return;
        }

        ErrorText.Text = "That doesn't look like a card wattage - expected a number between 30 and 1000.";
        ErrorText.Visibility = Visibility.Visible;
    }

    private void OnSkipClick(object sender, RoutedEventArgs e) => Close();

    private void OnDrag(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        try { DragMove(); }
        catch (InvalidOperationException) { /* button already released */ }
    }
}
