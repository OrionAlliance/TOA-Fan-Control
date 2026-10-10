using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace FanControlApp.Infrastructure;

/// <summary>Colours the real title bar so snapping still works; Windows 11 only, older keeps the default.</summary>
public static class TitleBarColor
{
    private const int DwmwaBorderColor = 34;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Call from SourceInitialized, there's no HWND before that.</summary>
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
            // Cosmetic only, never let it take the window down.
            DebugLog.Write("Title bar colouring failed (pre-Win11?).", ex);
        }
    }

    private static void Set(IntPtr hwnd, int attribute, Color c)
    {
        // DWM wants a COLORREF: 0x00BBGGRR.
        int colorRef = (c.B << 16) | (c.G << 8) | c.R;
        DwmSetWindowAttribute(hwnd, attribute, ref colorRef, sizeof(int));
    }
}
