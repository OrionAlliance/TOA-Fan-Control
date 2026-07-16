using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace FanControlApp.Helpers;

/// <summary>
/// Paints the real Windows title bar to match the app instead of faking one with
/// a borderless window - so snapping, Aero shake, and the system menu all still
/// behave exactly as Windows intends.
///
/// Needs Windows 11 (build 22000+). On anything older the calls just fail and the
/// default title bar stays; that's a fine outcome, so nothing here throws.
/// </summary>
public static class TitleBarColor
{
    private const int DwmwaBorderColor = 34;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Call from the window's SourceInitialized - there's no HWND before that.</summary>
    public static void Apply(Window window, Color caption, Color text, Color border)
    {
        try
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;

            Set(hwnd, DwmwaCaptionColor, caption);
            Set(hwnd, DwmwaTextColor, text);
            Set(hwnd, DwmwaBorderColor, border);
        }
        catch (Exception ex)
        {
            // Cosmetic only - never let it take the window down.
            DebugLog.Write("Title bar colouring failed (pre-Win11?).", ex);
        }
    }

    private static void Set(IntPtr hwnd, int attribute, Color c)
    {
        // DWM wants a COLORREF: 0x00BBGGRR, not the RGB order you'd expect.
        int colorRef = (c.B << 16) | (c.G << 8) | c.R;
        DwmSetWindowAttribute(hwnd, attribute, ref colorRef, sizeof(int));
    }
}
