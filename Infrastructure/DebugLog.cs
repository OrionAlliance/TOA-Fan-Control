using System.IO;
using System.Text;

namespace FanControlApp.Infrastructure;

/// <summary>
/// Append-only log written next to the exe. Ships with every build - when the
/// app touches fan hardware, "what did it do just before it went wrong" is the
/// only question that matters.
/// </summary>
public static class DebugLog
{
    private static readonly object Gate = new();
    private static readonly string LogPath =
        Path.Combine(AppPaths.ExeDir, "fan_debug.log");

    private const long MaxBytes = 2 * 1024 * 1024;

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                RollIfTooBig();
                string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}";
                File.AppendAllText(LogPath, line, Encoding.UTF8);
            }
        }
        catch
        {
            // Logging must never take the app down - especially not on the path
            // that releases the fans.
        }
    }

    public static void Write(string message, Exception ex) =>
        Write($"{message} :: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");

    private static void RollIfTooBig()
    {
        var fi = new FileInfo(LogPath);
        if (!fi.Exists || fi.Length < MaxBytes) return;

        string old = LogPath + ".old";
        if (File.Exists(old)) File.Delete(old);
        File.Move(LogPath, old);
    }
}
