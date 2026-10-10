using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace FanControlApp.Infrastructure;

/// <summary>Makes a folder admin-only: admins and SYSTEM can change it, everyone else can only read and run.</summary>
public static class FolderLock
{
    // Well-known group IDs (Administrators, SYSTEM, Users), so this works on any Windows language.
    private const string Grants =
        "*S-1-5-32-544:(OI)(CI)F *S-1-5-18:(OI)(CI)F *S-1-5-32-545:(OI)(CI)RX";

    // Write bits only; "Modify" also carries read bits, so it can't be used here.
    private const FileSystemRights Writes =
        FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles
        | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership
        | (FileSystemRights)0x40000000   // generic write
        | (FileSystemRights)0x10000000;  // generic all

    public static bool LockToAdmins(string dir)
    {
        dir = dir.TrimEnd('\\');

        // Replace inherited rules with ours, then reset anything already inside to follow them.
        bool ok = Icacls($"\"{dir}\" /inheritance:r /grant:r {Grants} /C /Q")
                  && (!Directory.EnumerateFileSystemEntries(dir).Any()
                      || Icacls($"\"{dir}\\*\" /reset /T /C /Q"))
                  // An owner can always rewrite the rules, so hand ownership to Administrators too.
                  && Icacls($"\"{dir}\" /setowner *S-1-5-32-544 /T /C /Q");
        return ok && IsLocked(dir);
    }

    /// <summary>Download folder for admin installers: admin-only when the app's own folder is locked.</summary>
    public static string DownloadFolder()
    {
        // Unlocked app folder (portable or Downloads): no safer than Temp, so use Temp.
        if (!IsLockedSafe(AppPaths.ExeDir)) return Path.GetTempPath();

        string exeDir = AppPaths.ExeDir.TrimEnd('\\');
        string dir = Path.GetFileName(exeDir).Equals("Updates", StringComparison.OrdinalIgnoreCase)
            ? exeDir                           // updater-launched setup already runs from Updates
            : Path.Combine(exeDir, "Updates");
        Directory.CreateDirectory(dir);
        if (!LockToAdmins(dir)) throw new InvalidOperationException("couldn't secure the download folder");
        return dir;
    }

    /// <summary>IsLocked that answers false instead of throwing.</summary>
    public static bool IsLockedSafe(string dir)
    {
        try { return IsLocked(dir); }
        catch (Exception ex)
        {
            DebugLog.Write("Folder lock check failed.", ex);
            return false;
        }
    }

    /// <summary>True when ordinary users can't add, change or delete anything in the folder.</summary>
    public static bool IsLocked(string dir)
    {
        DirectorySecurity acl = new DirectoryInfo(dir).GetAccessControl();
        if (!acl.AreAccessRulesProtected) return false;
        if (acl.GetOwner(typeof(SecurityIdentifier))?.Value is not ("S-1-5-32-544" or "S-1-5-18")) return false;

        foreach (FileSystemAccessRule r in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            bool admin = r.IdentityReference.Value is "S-1-5-32-544" or "S-1-5-18";
            if (!admin && r.AccessControlType == AccessControlType.Allow && (r.FileSystemRights & Writes) != 0)
                return false;
        }
        return true;
    }

    private static bool Icacls(string args)
    {
        try
        {
            using Process? p = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "icacls.exe"),
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p == null || !p.WaitForExit(30_000)) return false;
            return p.ExitCode == 0;
        }
        catch (Exception ex)
        {
            DebugLog.Write("Folder lock (icacls) failed to run.", ex);
            return false;
        }
    }
}
