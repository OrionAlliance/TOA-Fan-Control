// System tray icon, its menu, and tray balloons.
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FanControlApp.Controls;
using FanControlApp.Cooling;
using FanControlApp.Infrastructure;

namespace FanControlApp;

public partial class MainWindow
{
    // ---- system tray --------------------------------------------------------

    private void SetupTray()
    {
        _tray = new System.Windows.Forms.NotifyIcon
        {
            // The exe's embedded icon, so the tray matches the app.
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!),
            Text = "TOA - Fan Control",
            Visible = true,
        };

        _tray.DoubleClick += (_, _) => ShowFromTray();

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => ShowFromTray());
        menu.Items.Add("Exit", null, (_, _) => Close());
        _tray.ContextMenuStrip = menu;
    }

    /// <summary>Small non-modal tray notice.</summary>
    public void ShowTrayBalloon(string title, string text) =>
        _tray?.ShowBalloonTip(8000, title, text, System.Windows.Forms.ToolTipIcon.Info);

    private void ShowFromTray()
    {
        // In Game Mode, leave it properly instead of stacking behind the overlay.
        if (_overlay is { IsVisible: true })
        {
            LeaveGameMode();
            return;
        }

        Show();
        WindowState = WindowState.Normal;
        Activate();

        // Surface any update missed while hidden.
        _ = ((App)Application.Current).CheckOnUserReturnAsync();
    }
}
