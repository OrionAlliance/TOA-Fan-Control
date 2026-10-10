using FanControlApp.Infrastructure;

namespace FanControlApp.Cooling;

/// <summary>Spots a fan spinning on a header the app isn't driving and hasn't asked about.</summary>
public static class NewFans
{
    // Empty headers read 0; real fans idle well above this.
    private const float MinSpinRpm = 100;

    // Same case-insensitive rule as the controller and picker.
    private static readonly StringComparer Names = StringComparer.OrdinalIgnoreCase;

    /// <summary>Spinning headers the user hasn't picked and hasn't been asked about yet.</summary>
    public static List<string> Find(IReadOnlyList<(string Name, float? Rpm)> candidates, FanSettings s)
    {
        // No list = every candidate is driven, so nothing is new.
        if (s.AskedFans == null || s.SelectedFans == null) return new List<string>();

        return candidates
            .Where(c => Spinning(c.Rpm)
                        && !s.SelectedFans.Contains(c.Name, Names)
                        && !s.AskedFans.Contains(c.Name, Names))
            .Select(c => c.Name)
            .ToList();
    }

    /// <summary>Marks every spinning header the user left unchecked as decided, so it's never asked about.</summary>
    public static void RecordDecisions(FanSettings s, IReadOnlyList<(string Name, float? Rpm)> candidates)
    {
        s.AskedFans ??= new List<string>();
        s.AskedFans.AddRange(Find(candidates, s));
    }

    private static bool Spinning(float? rpm) => rpm is > MinSpinRpm;
}
