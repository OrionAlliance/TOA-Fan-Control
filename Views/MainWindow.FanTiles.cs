// Per fan dials and bars, laid out in rows.
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FanControlApp.Controls;
using FanControlApp.Cooling;
using FanControlApp.Infrastructure;

namespace FanControlApp;

public partial class MainWindow
{
    /// <summary>One dial per driven fan, latched once seen spinning so empty headers never show.</summary>
    private void UpdateFanGauges(FanReadings r)
    {
        bool added = false;

        foreach (string name in r.DrivenFans)
        {
            FanChannel? f = r.Fans.FirstOrDefault(x =>
                string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            double rpm = f?.Rpm ?? double.NaN;

            if (!_fanGauges.ContainsKey(name))
            {
                if (rpm is not (> 0)) continue; // not spinning yet
                FanBlade g = NewFanGauge(name);
                _fanGauges[name] = g;
                _fanBars[name] = new StatBar { Label = FanName.Display(name), Unit = "%" };
                _shownFans.Add(name);
                added = true;
            }

            _fanGauges[name].Value = rpm;             // spin speed
            _fanGauges[name].Percent = r.OutputPercent; // hub number

            _fanBars[name].Value = r.OutputPercent;
            _fanBars[name].TrailText = rpm is > 0 ? $"RPM: {rpm:F0}" : "RPM: --";
        }

        if (added) RebuildFanRows();
    }

    /// <summary>Re-lay the shown fans into centred dial rows and paired bar rows.</summary>
    private void RebuildFanRows()
    {
        // Detach tiles first; WPF throws when adding a control that still has a parent.
        foreach (FanBlade g in _fanGauges.Values)
            (g.Parent as Panel)?.Children.Remove(g);

        FanRows.Children.Clear();

        for (int i = 0; i < _shownFans.Count; i += FansPerRow)
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
            };

            for (int j = i; j < System.Math.Min(i + FansPerRow, _shownFans.Count); j++)
                row.Children.Add(_fanGauges[_shownFans[j]]);

            FanRows.Children.Add(row);
        }

        // Fan bars pair up side by side, like the CPU/GPU line.
        foreach (StatBar b in _fanBars.Values)
            (b.Parent as Panel)?.Children.Remove(b);

        FanBarRows.Children.Clear();
        for (int i = 0; i < _shownFans.Count; i += 2)
        {
            var row = new Grid();

            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            StatBar left = _fanBars[_shownFans[i]];
            left.Margin = new Thickness(0);
            Grid.SetColumn(left, 0);
            row.Children.Add(left);

            if (i + 1 < _shownFans.Count)
            {
                StatBar right = _fanBars[_shownFans[i + 1]];
                right.Margin = new Thickness(8, 0, 0, 0);
                Grid.SetColumn(right, 1);
                row.Children.Add(right);
            }
            // An odd last fan keeps the left slot, in reading order.

            FanBarRows.Children.Add(row);
        }
    }

    private static FanBlade NewFanGauge(string name) => new()
    {
        Label = FanName.Display(name),
        Width = FanTileW,
        Height = FanTileH,
    };
}
