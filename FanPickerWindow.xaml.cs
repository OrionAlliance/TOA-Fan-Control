using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace FanControlApp;

/// <summary>
/// The safety picker: which of the discovered case fans may the app drive?
/// Exists for boards that name every header "Fan #N" - there, a liquid-cooler
/// pump is indistinguishable from a case fan by name, and the person who built
/// the PC is the only one who knows. This can only NARROW what the app drives:
/// pump/CPU/GPU-named fans were excluded before this list was built.
/// </summary>
public partial class FanPickerWindow : Window
{
    private readonly List<CheckBox> _boxes = new();

    /// <summary>The names the user checked, or null if they cancelled.</summary>
    public List<string>? Selection { get; private set; }

    /// <param name="fans">Candidate fans (already past the name-safety rule).</param>
    /// <param name="checkedNames">Names to pre-check; null = check everything.</param>
    /// <param name="firstRun">First run hides Cancel (a choice must be made) and
    /// the "takes effect next launch" footnote (it applies immediately).</param>
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
                Content = $"{name}   ·   {rpmText}",
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
