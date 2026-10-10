namespace FanControlApp.Infrastructure;

/// <summary>Short display names for fans; the hardware names stay the keys because they must match the chip.</summary>
public static class FanName
{
    public static string Display(string name) => name
        .Replace("Chassis Fan", "Fan", StringComparison.OrdinalIgnoreCase)
        .Replace("System Fan", "Fan", StringComparison.OrdinalIgnoreCase)
        .Trim();
}
