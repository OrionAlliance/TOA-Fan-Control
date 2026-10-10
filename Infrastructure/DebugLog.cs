using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace FanControlApp.Infrastructure;

/// <summary>Append-only log next to the exe, on in every build because the app drives fan hardware.</summary>
public static class DebugLog
{
    private static readonly object Gate = new();
    private static readonly string LogPath =
        Path.Combine(AppPaths.ExeDir, "fan_debug.log");

    private const long MaxBytes = 2 * 1024 * 1024;

    // Any C:\Users\<name> folder (full or ~1 short form), so the name never reaches the log.
    private static readonly Regex UserFolder = new(
        @"([A-Za-z]:\\Users\\)[^\\/:*?""<>|\r\n]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Catches profiles stored outside C:\Users by exact path.
    private static readonly string Profile =
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    // Folders that could carry a person's name outside C:\Users, starting with this exe's own.
    private static readonly List<(string Path, string Label)> Hidden =
        new() { (AppPaths.ExeDir.TrimEnd('\\'), "<exe folder>") };

    /// <summary>Log lines show this folder as the label instead of its real path.</summary>
    public static void HidePath(string path, string label)
    {
        lock (Gate) Hidden.Add((path.Trim().TrimEnd('\\'), label));
    }

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
            // Logging must never crash the app, especially on the fan release path.
        }
    }

    public static void Write(string message, Exception ex) =>
        Write($"{message} :: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");

    public static string Scrub(string s)
    {
        s = UserFolder.Replace(s, "$1<user>");
        if (Profile.Length > 0) s = s.Replace(Profile, "<user profile>", StringComparison.OrdinalIgnoreCase);
        foreach ((string path, string label) in Hidden)
        {
            // Never hide a bare drive root like "D:", it would swallow every path on that drive.
            if (path.Length > 3) s = s.Replace(path, label, StringComparison.OrdinalIgnoreCase);
        }
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
