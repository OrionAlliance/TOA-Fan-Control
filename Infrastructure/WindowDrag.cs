using System.Windows;

namespace FanControlApp.Infrastructure;

/// <summary>Drags a borderless window by its body.</summary>
public static class WindowDrag
{
    public static void TryDrag(Window w)
    {
        try { w.DragMove(); }
        catch (InvalidOperationException) { /* button already released */ }
    }
}
