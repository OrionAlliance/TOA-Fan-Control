using System.IO;
using System.Text;
using System.Text.RegularExpressions;

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

    // Any C:\Users\<name> folder (full or Windows' shortened ~1 form) - the name never reaches the log.
    private static readonly Regex UserFolder = new(
        @"([A-Za-z]:\\Users\\)[^\\/:*?""<>|\r\n]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Profiles stored outside C:\Users (rare corporate setups) get caught by their exact path.
    private static readonly string Profile =
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                RollIfTooBig();
                string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {Scrub(message)}{Environment.NewLine}";
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

    private static string Scrub(string s)
    {
        s = UserFolder.Replace(s, "$1<user>");
        if (Profile.Length > 0) s = s.Replace(Profile, "<user profile>", StringComparison.OrdinalIgnoreCase);
        return s;
    }

    private static void RollIfTooBig()
    {
        var fi = new FileInfo(LogPath);
        if (!fi.Exists || fi.Length < MaxBytes) return;

        string old = LogPath + ".old";
        if (File.Exists(old)) File.Delete(old);
        File.Move(LogPath, old);
    }
}
