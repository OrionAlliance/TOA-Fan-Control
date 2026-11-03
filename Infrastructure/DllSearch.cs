using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace FanControlApp.Infrastructure;

/// <summary>Limits DLL loading to the app's folder and System32.</summary>
internal static class DllSearch
{
    // App folder + System32, plus any folder added on purpose.
    private const uint LOAD_LIBRARY_SEARCH_DEFAULT_DIRS = 0x00001000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetDefaultDllDirectories(uint directoryFlags);

    // Runs before any app code, so it applies to every DLL the app loads.
    [ModuleInitializer]
    internal static void Lock()
    {
        if (!SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_DEFAULT_DIRS))
            DebugLog.Write($"DLL search setting failed (Windows error {Marshal.GetLastPInvokeError()}).");
    }
}
