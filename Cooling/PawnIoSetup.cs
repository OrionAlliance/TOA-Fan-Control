using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using FanControlApp.Infrastructure;
using Microsoft.Win32;

namespace FanControlApp.Cooling;

/// <summary>Installs the PawnIO driver from the author's own signed release (not bundled), verifying the signature first.</summary>
public static class PawnIoSetup
{
    // /latest/download/ always redirects to the newest release, so no version goes stale.
    private const string InstallerUrl =
        "https://github.com/namazso/PawnIO.Setup/releases/latest/download/PawnIO_setup.exe";

    // Releases API for the latest version number.
    private const string LatestReleaseApi =
        "https://api.github.com/repos/namazso/PawnIO.Setup/releases/latest";

    // The installer must be signed by this publisher, or we refuse to run it.
    private const string ExpectedSigner = "namazso";

    private const int RebootRequiredExitCode = 3010; // ERROR_SUCCESS_REBOOT_REQUIRED

    public sealed record InstallResult(bool Success, bool RebootRequired, string Message);

    /// <summary>True if the "PawnIO" kernel service is registered.</summary>
    public static bool IsInstalled()
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services\PawnIO");
            return key != null;
        }
        catch (Exception ex)
        {
            DebugLog.Write("PawnIO detection failed; assuming present so we don't nag.", ex);
            return true; // don't nag if the check itself broke
        }
    }

    /// <summary>Installed vs newest-available PawnIO, when an update exists.</summary>
    public sealed record UpdateInfo(Version Installed, Version Latest);

    /// <summary>Returns a newer PawnIO version if one exists; null when missing, current, or offline.</summary>
    public static async Task<UpdateInfo?> CheckForUpdateAsync()
    {
        Version? installed = GetInstalledVersion();
        if (installed == null) return null; // missing or unreadable

        Version? latest = await GetLatestVersionAsync();
        if (latest == null) return null; // offline, stay quiet

        return latest > installed ? new UpdateInfo(installed, latest) : null;
    }

    /// <summary>Installed PawnIO version from its uninstall entry, or null if absent or unreadable.</summary>
    public static Version? GetInstalledVersion()
    {
        string[] roots =
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
        };

        foreach (string root in roots)
        {
            try
            {
                using RegistryKey? key = Registry.LocalMachine.OpenSubKey(root);
                if (key == null) continue;

                foreach (string sub in key.GetSubKeyNames())
                {
                    using RegistryKey? entry = key.OpenSubKey(sub);
                    if (entry?.GetValue("DisplayName") is not string name) continue;
                    if (!name.Contains("PawnIO", StringComparison.OrdinalIgnoreCase)) continue;

                    if (entry.GetValue("DisplayVersion") is string ver && ParseVersion(ver) is { } v)
                        return v;
                }
            }
            catch (Exception ex)
            {
                DebugLog.Write("Reading installed PawnIO version failed.", ex);
            }
        }

        return null;
    }

    private static async Task<Version?> GetLatestVersionAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("TOA-FanControl");

            string json = await http.GetStringAsync(LatestReleaseApi);
            using JsonDocument doc = JsonDocument.Parse(json);
            string? tag = doc.RootElement.GetProperty("tag_name").GetString();
            return ParseVersion(tag);
        }
        catch (Exception ex)
        {
            DebugLog.Write("Checking the latest PawnIO version failed (offline?).", ex);
            return null;
        }
    }

    /// <summary>Parses "v2.2.0" or "2.2.0.0" into a 4-part Version, since a missing part would compare as older.</summary>
    private static Version? ParseVersion(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        string[] parts = raw.TrimStart('v', 'V').Trim().Split('.');
        int[] n = new int[4];
        for (int i = 0; i < 4; i++)
            n[i] = i < parts.Length && int.TryParse(parts[i], out int p) ? p : 0;

        return new Version(n[0], n[1], n[2], n[3]);
    }

    /// <summary>Downloads the official installer, verifies its publisher signature, and runs it.</summary>
    public static async Task<InstallResult> DownloadVerifyInstallAsync(IProgress<string> progress)
    {
        string temp = "";
        try
        {
            temp = Path.Combine(FolderLock.DownloadFolder(), $"PawnIO_setup_{Environment.ProcessId}.exe");
            progress.Report("Downloading the official PawnIO installer…");
            await DownloadAsync(InstallerUrl, temp);
            // Filename only: the full path holds the Windows username.
            DebugLog.Write($"PawnIO installer downloaded ({Path.GetFileName(temp)}).");

            progress.Report("Verifying its signature…");
            if (!IsTrustedAndSignedBy(temp, ExpectedSigner))
            {
                DebugLog.Write("PawnIO installer failed signature verification - NOT running it.");
                return new InstallResult(false, false,
                    "The download didn't pass signature checks, so it wasn't run. " +
                    "Install PawnIO yourself from pawnio.eu instead.");
            }

            DebugLog.Write("PawnIO installer signature OK. Running it.");
            progress.Report("Running the installer…");
            int exit = await RunInstallerAsync(temp);

            if (exit == 0)
                return new InstallResult(true, false, "PawnIO installed.");

            if (exit == RebootRequiredExitCode)
                return new InstallResult(true, true, "PawnIO installed - a reboot will finish it.");

            DebugLog.Write($"PawnIO installer exited with code {exit}.");
            return new InstallResult(false, false,
                $"The installer exited with code {exit}. You can install PawnIO yourself from pawnio.eu.");
        }
        catch (Exception ex)
        {
            DebugLog.Write("PawnIO install failed.", ex);
            return new InstallResult(false, false,
                "Couldn't install PawnIO automatically. You can get it from pawnio.eu. " + ex.Message);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* temp cleanup */ }
        }
    }

    private static async Task DownloadAsync(string url, string dest)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("TOA-FanControl");

        using HttpResponseMessage resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();

        await using Stream src = await resp.Content.ReadAsStreamAsync();
        await using FileStream file = File.Create(dest);
        await src.CopyToAsync(file);
    }

    private static async Task<int> RunInstallerAsync(string path)
    {
        // Inherits our admin rights; runs visibly so the person sees the real PawnIO installer.
        var psi = new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = false,
        };

        using Process? p = Process.Start(psi);
        if (p == null) return -1;
        await p.WaitForExitAsync();
        return p.ExitCode;
    }

    // ---- signature verification ---------------------------------------------

    /// <summary>True only for a valid, trusted Authenticode signature from the expected publisher.</summary>
    public static bool IsTrustedAndSignedBy(string path, string expectedSigner)
    {
        try
        {
            if (!WinVerifyTrustValid(path)) return false;

            // CreateFromSignedFile has no modern replacement; SYSLIB0057 flagged it by accident.
#pragma warning disable SYSLIB0057
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            return cert.Subject.Contains(expectedSigner, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            DebugLog.Write("Signature check threw - treating as untrusted.", ex);
            return false;
        }
    }

    // WinVerifyTrust: the OS's own "is this signature valid and trusted" call.
    private static readonly Guid WintrustActionGenericVerifyV2 =
        new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    private static bool WinVerifyTrustValid(string path)
    {
        var fileInfo = new WINTRUST_FILE_INFO
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
            pcwszFilePath = path,
        };

        IntPtr pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(fileInfo, pFile, false);

            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = WTD_UI_NONE,
                fdwRevocationChecks = WTD_REVOKE_NONE,
                dwUnionChoice = WTD_CHOICE_FILE,
                pFile = pFile,
                dwStateAction = WTD_STATEACTION_VERIFY,
                dwProvFlags = WTD_SAFER_FLAG,
            };

            Guid action = WintrustActionGenericVerifyV2;
            uint result = WinVerifyTrust(IntPtr.Zero, ref action, ref data);

            // Close the state we opened, whatever the verdict.
            data.dwStateAction = WTD_STATEACTION_CLOSE;
            WinVerifyTrust(IntPtr.Zero, ref action, ref data);

            return result == 0; // S_OK == trusted
        }
        finally
        {
            Marshal.FreeHGlobal(pFile);
        }
    }

    private const uint WTD_UI_NONE = 2;
    private const uint WTD_REVOKE_NONE = 0;
    private const uint WTD_CHOICE_FILE = 1;
    private const uint WTD_STATEACTION_VERIFY = 1;
    private const uint WTD_STATEACTION_CLOSE = 2;
    private const uint WTD_SAFER_FLAG = 0x100;

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false)]
    private static extern uint WinVerifyTrust(IntPtr hwnd, [In] ref Guid pgActionID,
        [In] ref WINTRUST_DATA pWVTData);

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }
}
