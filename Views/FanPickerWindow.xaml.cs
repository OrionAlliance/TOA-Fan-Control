using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace FanControlApp;

/// <summary>Safety picker for which case fans the app may drive; only narrows, since a "Fan #N" pump looks like a fan.</summary>
public partial class FanPickerWindow : Window
{
    private readonly List<CheckBox> _boxes = new();

    /// <summary>The names the user checked, or null if they cancelled.</summary>
    public List<string>? Selection { get; private set; }

    /// <param name="fans">Candidate fans (already past the name-safety rule).</param>
    /// <param name="checkedNames">Names to pre-check; null = check everything.</param>
    /// <param name="firstRun">Hides Cancel and the "next launch" footnote, since a choice is required and applies now.</param>
    public FanPickerWindow(IReadOnlyList<(string Name, float? Rpm)> fans,
                           IReadOnlyCollection<string>? checkedNames,
                           bool firstRun)
    {
        InitializeComponent();

        foreach ((string name, float? rpm) in fans)
        {
            string rpmText = rpm is { } r and >= 1 ? $"{r:F0} RPM" : "not spinning";
            var box = new CheckBox
            {
                Content = $"{Infrastructure.FanName.Display(name)}   ·   {rpmText}",
                FontSize = 13,
                Margin = new Thickness(0, 4, 0, 4),
                IsChecked = checkedNames == null ||
                            checkedNames.Contains(name, StringComparer.OrdinalIgnoreCase),
                Tag = name,
            };
            box.SetResourceReference(ForegroundProperty, "Text");
            _boxes.Add(box);
            FanList.Children.Add(box);
        }

        if (!firstRun)
        {
            CancelButton.Visibility = Visibility.Visible;
            FootnoteText.Visibility = Visibility.Visible;
        }
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        Selection = _boxes.Where(b => b.IsChecked == true)
                          .Select(b => (string)b.Tag)
                          .ToList();
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        Selection = null;
        Close();
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        try { DragMove(); }
        catch (InvalidOperationException) { /* button already released */ }
    }
}
