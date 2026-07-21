using System.Runtime.InteropServices;

namespace FanControlApp.Infrastructure;

/// <summary>
/// Answers one question: is now a decent moment to show a popup? Asks Windows the
/// same thing Windows asks itself before showing a toast - so a fullscreen game
/// (exclusive OR borderless), a video, or presentation mode all report "busy",
/// and the update popups wait instead of stealing focus mid-game.
/// </summary>
public static class ScreenState
{
    // SHQueryUserNotificationState values:
    //   1 NOT_PRESENT   (locked/away - nobody there to click)
    //   2 BUSY          (fullscreen window in the foreground)
    //   3 D3D_FULL_SCREEN (exclusive-fullscreen game)
    //   4 PRESENTATION_MODE
    //   5 ACCEPTS_NOTIFICATIONS (normal desktop)
    //   6 QUIET_TIME    (fresh sign-in lull - fine for us)
    //   7 APP           (store app fullscreen)
    public static bool PopupsSafe()
    {
        try
        {
            if (SHQueryUserNotificationState(out int state) != 0)
                return true; // query failed - don't let a broken call block updates forever

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
