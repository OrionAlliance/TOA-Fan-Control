using System.Text.Json.Serialization;

namespace FanControlApp.Helpers;

public sealed class CurvePoint
{
    public float Temp { get; set; }
    public float Percent { get; set; }

    public CurvePoint() { }

    public CurvePoint(float temp, float percent)
    {
        Temp = temp;
        Percent = percent;
    }
}

/// <summary>
/// The "line" - temperature across the bottom, fan percent up the side.
/// Between the points it interpolates straight; outside them it clamps flat.
/// </summary>
public sealed class FanCurve
{
    public List<CurvePoint> Points { get; set; } = new();

    [JsonIgnore]
    public IEnumerable<CurvePoint> Sorted => Points.OrderBy(p => p.Temp);

    public static FanCurve Default() => new()
    {
        Points =
        {
            new CurvePoint(30f, 30f),
            new CurvePoint(50f, 40f),
            new CurvePoint(65f, 60f),
            new CurvePoint(75f, 85f),
            new CurvePoint(85f, 100f),
        },
    };

    public float Evaluate(float temp)
    {
        List<CurvePoint> pts = Sorted.ToList();
        if (pts.Count == 0) return 50f;
        if (pts.Count == 1) return pts[0].Percent;

        if (temp <= pts[0].Temp) return pts[0].Percent;
        if (temp >= pts[^1].Temp) return pts[^1].Percent;

        for (int i = 0; i < pts.Count - 1; i++)
        {
            CurvePoint a = pts[i];
            CurvePoint b = pts[i + 1];
            if (temp < a.Temp || temp > b.Temp) continue;

            float span = b.Temp - a.Temp;
            if (span <= 0.001f) return b.Percent;

            float t = (temp - a.Temp) / span;
            return a.Percent + t * (b.Percent - a.Percent);
        }

        return pts[^1].Percent;
    }

    public FanCurve Clone() => new()
    {
        Points = Points.Select(p => new CurvePoint(p.Temp, p.Percent)).ToList(),
    };
}
