using System.Runtime.InteropServices;

namespace FanControlApp.Infrastructure;

/// <summary>Safe to show a popup? Uses Windows' toast check, so fullscreen games and videos aren't interrupted.</summary>
public static class ScreenState
{
    // Safe only on 5 ACCEPTS_NOTIFICATIONS or 6 QUIET_TIME; others are away, fullscreen, or presenting.
    public static bool PopupsSafe()
    {
        try
        {
            if (SHQueryUserNotificationState(out int state) != 0)
                return true; // a failed query must not block updates forever

            return state is 5 or 6;
        }
        catch
        {
            return true;
        }
    }

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);
}
