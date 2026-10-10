// Dependency properties and the value, load and peak setters.
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
    // ---- properties ---------------------------------------------------------

    public static readonly DependencyProperty LabelProperty = Reg(nameof(Label), "");
    public static readonly DependencyProperty UnitProperty = Reg(nameof(Unit), "");
    public static readonly DependencyProperty MinimumProperty = Reg(nameof(Minimum), 0d);
    public static readonly DependencyProperty MaximumProperty = Reg(nameof(Maximum), 100d);
    public static readonly DependencyProperty MajorTickProperty = Reg(nameof(MajorTick), 20d);

    /// <summary>Green runs from Minimum to here. NaN = no green band.</summary>
    public static readonly DependencyProperty GreenToProperty = Reg(nameof(GreenTo), double.NaN);

    /// <summary>Red runs from here to Maximum. NaN = no red band.</summary>
    public static readonly DependencyProperty RedFromProperty = Reg(nameof(RedFrom), double.NaN);

    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(nameof(Value), typeof(double), typeof(Gauge),
            new PropertyMetadata(double.NaN, OnValueChanged));

    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public string Unit { get => (string)GetValue(UnitProperty); set => SetValue(UnitProperty, value); }
    public double Minimum { get => (double)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double MajorTick { get => (double)GetValue(MajorTickProperty); set => SetValue(MajorTickProperty, value); }
    public double GreenTo { get => (double)GetValue(GreenToProperty); set => SetValue(GreenToProperty, value); }
    public double RedFrom { get => (double)GetValue(RedFromProperty); set => SetValue(RedFromProperty, value); }
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    private double _peak = double.NaN;
    private double _peakLoad = double.NaN;
    private double _loadValue = double.NaN;
    private bool _trueLoad;

    /// <summary>True labels the load lane as real load (watts vs max), false as busy time; wording only.</summary>
    public bool TrueLoad
    {
        set
        {
            if (value == _trueLoad) return;
            _trueLoad = value;
            UpdatePeakMarks();
            UpdateLiveLoad();
        }
    }

    private string LoadWord => _trueLoad ? "load" : "busy time";

    /// <summary>Live load % (0-100) for the cyan triangle; NaN hides it.</summary>
    public double LoadValue
    {
        set
        {
            if (value.Equals(_loadValue)) return; // double.Equals: NaN equals NaN
            _loadValue = value;
            UpdateLiveLoad();
        }
    }

    /// <summary>Session peak load % (0-100) from the controller for the cyan tick; NaN hides it.</summary>
    public double PeakLoad
    {
        set
        {
            if (value.Equals(_peakLoad)) return; // double.Equals: NaN equals NaN
            _peakLoad = value;
            UpdatePeakMarks();
        }
    }

    /// <summary>Session peak from the controller, shared by every view so they agree; NaN hides it.</summary>
    public double Peak
    {
        get => _peak;
        set
        {
            if (value.Equals(_peak)) return; // double.Equals: NaN equals NaN
            _peak = value;
            UpdatePeakMarks();
        }
    }

    private static DependencyProperty Reg(string name, object def) =>
        DependencyProperty.Register(name, def.GetType(), typeof(Gauge),
            new PropertyMetadata(def, (d, _) => ((Gauge)d).Rebuild()));

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((Gauge)d).UpdateMoving();
}
