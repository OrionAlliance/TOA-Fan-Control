namespace FanControlApp.Infrastructure;

/// <summary>
/// Display names for fans. The hardware's own names ("Chassis Fan #2") stay the
/// keys everywhere - settings, the watchdog handoff, lookups - because they must
/// match the chip exactly. Only what a person READS gets shortened: the chip
/// jargon ("Chassis", "System") says nothing a user needs, "Fan #2" does.
/// </summary>
public static class FanName
{
    public static string Display(string name) => name
        .Replace("Chassis Fan", "Fan", StringComparison.OrdinalIgnoreCase)
        .Replace("System Fan", "Fan", StringComparison.OrdinalIgnoreCase)
        .Trim();
}
