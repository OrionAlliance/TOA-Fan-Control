// Moves the needle and the peak and load marks when values change.
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace FanControlApp.Controls;

public partial class Gauge
{
    private void UpdateMoving()
    {
        if (_needleRotate == null || _valueText == null) return;

        double v = Value;
        bool has = !double.IsNaN(v);

        _valueText.Text = has
            ? v.ToString("0", CultureInfo.InvariantCulture) +
              (string.IsNullOrEmpty(Unit) ? "" : " " + Unit)
            : "--";

        _valueText.Foreground = !has ? B("#FFFFFF")
            : !double.IsNaN(RedFrom) && v >= RedFrom ? B("#F85149")
            : !double.IsNaN(GreenTo) && v <= GreenTo ? B("#3FB950")
            : B("#FFFFFF");

        // Sweep rather than snap; a jumping needle reads as broken.
        double target = AngleFor(has ? v : Minimum);
        _needleRotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation
        {
            To = target,
            Duration = TimeSpan.FromMilliseconds(350),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        });
    }

    // Repaints only when a peak changes, never on value ticks.
    private void UpdatePeakMarks()
    {
        if (_peakMark == null || _peakRotate == null || _peakHit == null) return;

        bool hasPeak = !double.IsNaN(Peak);
        Visibility peakVis = hasPeak ? Visibility.Visible : Visibility.Collapsed;
        _peakMark.Visibility = peakVis;
        _peakHit.Visibility = peakVis;
        if (hasPeak)
        {
            string unit = string.IsNullOrEmpty(Unit) ? "" : " " + Unit;
            _peakHit.ToolTip = $"{Label} peak temp this run: {Peak:0}{unit}";
            _peakRotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation
            {
                To = AngleFor(Peak),
                Duration = TimeSpan.FromMilliseconds(350),
            });
        }

        if (_loadPeakMark == null || _loadPeakHit == null || _loadPeakRotate == null) return;

        bool hasLoad = !double.IsNaN(_peakLoad);
        Visibility loadVis = hasLoad ? Visibility.Visible : Visibility.Collapsed;
        _loadPeakMark.Visibility = loadVis;
        _loadPeakHit.Visibility = loadVis;
        if (hasLoad)
        {
            _loadPeakHit.ToolTip = $"{Label} peak {LoadWord} this run: {_peakLoad:0}%";
            double loadAngle = LoadAngle(_peakLoad);

            // If the peaks overlap, park the load mark one dial unit off the temp mark.
            double oneUnit = SweepAngle / 100.0;
            if (hasPeak)
            {
                double tempAngle = AngleFor(Peak);
                if (Math.Abs(loadAngle - tempAngle) < oneUnit)
                    loadAngle = tempAngle + oneUnit;
                if (loadAngle > StartAngle + SweepAngle) loadAngle = tempAngle - oneUnit;
            }

            _loadPeakRotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation
            {
                To = loadAngle,
                Duration = TimeSpan.FromMilliseconds(350),
            });
        }
    }

    // Sweeps the live load triangle like the temp needle.
    private void UpdateLiveLoad()
    {
        if (_loadMark == null || _loadHit == null || _loadRotate == null) return;

        bool has = !double.IsNaN(_loadValue);
        Visibility vis = has ? Visibility.Visible : Visibility.Collapsed;
        _loadMark.Visibility = vis;
        _loadHit.Visibility = vis;
        if (!has) return;

        _loadHit.ToolTip = $"{Label} {LoadWord} right now: {_loadValue:0}%";
        _loadRotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation
        {
            To = LoadAngle(_loadValue),
            Duration = TimeSpan.FromMilliseconds(350),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        });
    }

    // Load is a fraction of the whole sweep, not a point on the temperature axis.
    private double LoadAngle(double pct) =>
        StartAngle + Math.Clamp(pct / 100.0, 0, 1) * SweepAngle;
}
