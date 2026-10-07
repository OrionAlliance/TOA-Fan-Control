using FanControlApp.Infrastructure;

namespace FanControlApp.Cooling;

/// <summary>
/// Spots a fan spinning on a header the app isn't driving and hasn't asked about -
/// a new hub/board header, or a fan plugged into a header that was empty when picked.
/// </summary>
public static class NewFans
{
    // Empty headers read 0; real fans idle well above this.
    private const float MinSpinRpm = 100;

    /// <summary>Spinning headers the user hasn't picked and hasn't been asked about yet.</summary>
    public static List<string> Find(IReadOnlyList<(string Name, float? Rpm)> candidates, FanSettings s)
    {
        if (s.AskedFans == null) return new List<string>();
        return candidates
            .Where(c => Spinning(c.Rpm)
                        && !(s.SelectedFans?.Contains(c.Name) ?? false)
                        && !s.AskedFans.Contains(c.Name))
            .Select(c => c.Name)
            .ToList();
    }

    /// <summary>Marks every spinning header the user left unchecked as decided, so it's never asked about.</summary>
    public static void RecordDecisions(FanSettings s, IReadOnlyList<(string Name, float? Rpm)> candidates)
    {
        s.AskedFans ??= new List<string>();
        foreach ((string name, float? rpm) in candidates)
        {
            if (Spinning(rpm)
                && !(s.SelectedFans?.Contains(name) ?? false)
                && !s.AskedFans.Contains(name))
                s.AskedFans.Add(name);
        }
    }

    private static bool Spinning(float? rpm) => rpm is > MinSpinRpm;
}
