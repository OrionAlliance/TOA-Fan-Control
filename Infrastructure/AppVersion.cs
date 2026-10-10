namespace FanControlApp.Infrastructure;

/// <summary>This build's version from the assembly, read once.</summary>
public static class AppVersion
{
    /// <summary>Major.Minor.Build, with a missing build part read as 0.</summary>
    public static Version Current { get; } = Read();

    /// <summary>Shown form, such as "v1.2.3".</summary>
    public static string Display => $"v{Current.Major}.{Current.Minor}.{Current.Build}";

    private static Version Read()
    {
        Version v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version
                    ?? new Version(0, 0, 0);
        return new Version(v.Major, v.Minor, v.Build < 0 ? 0 : v.Build);
    }
}
