using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace FanControlApp.Infrastructure;

/// <summary>Limits DLL loading to the app's folder and System32, never PATH or the current folder.</summary>
internal static class DllSearch
{
    // App folder + System32 (+ folders added on purpose); a DLL dropped in a user-writable PATH folder is never picked up.
    private const uint LOAD_LIBRARY_SEARCH_DEFAULT_DIRS = 0x00001000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetDefaultDllDirectories(uint directoryFlags);

    // Runs before any app code, so it's in place before the hardware library loads GPU DLLs by name.
    [ModuleInitializer]
    internal static void Lock() => SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
}
