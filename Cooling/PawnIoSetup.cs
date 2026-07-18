using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using FanControlApp.Infrastructure;
using Microsoft.Win32;

namespace FanControlApp.Cooling;

/// <summary>
/// PawnIO is the signed kernel driver LHM uses to reach the motherboard. Without
/// it, the app sees no fans at all - so on any machine that doesn't have it, we
/// offer to install it.
///
/// We do NOT bundle it. The signed build's redistribution terms are unclear, and
/// the GPL build is unsigned so Windows won't load it. Instead we fetch the
/// author's OWN signed installer from his GitHub release at setup time - no
/// redistribution on our part - and we verify its Authenticode signature before
/// running it, because downloading and executing anything unchecked is exactly
/// how you get burned.
/// </summary>
public static class PawnIoSetup
{
    // GitHub's /latest/download/<asset> always redirects to the newest release's
    // asset, so we never hardcode a version that goes stale.
    private const string InstallerUrl =
        "https://github.com/namazso/PawnIO.Setup/releases/latest/download/PawnIO_setup.exe";

    // The installer must be signed by this publisher, or we refuse to run it.
    private const string ExpectedSigner = "namazso";

    private const int RebootRequiredExitCode = 3010; // ERROR_SUCCESS_REBOOT_REQUIRED

    public sealed record InstallResult(bool Success, bool RebootRequired, string Message);

    /// <summary>
    /// True if the PawnIO kernel service is registered. The installer creates a
    /// service literally named "PawnIO"; its presence is the authoritative
    /// "is it installed" signal.
    /// </summary>
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
            return true; // fail safe: don't badger the user if the check itself broke
        }
    }

    /// <summary>
    /// Download the official installer, verify it's signed by the expected
    /// publisher, run it, and report what happened. progress gets short status
    /// strings for the UI.
    /// </summary>
    public static async Task<InstallResult> DownloadVerifyInstallAsync(IProgress<string> progress)
    {
        string temp = Path.Combine(Path.GetTempPath(),
            $"PawnIO_setup_{Environment.ProcessId}.exe");

        try
        {
            progress.Report("Downloading the official PawnIO installer…");
            await DownloadAsync(InstallerUrl, temp);
            DebugLog.Write($"PawnIO installer downloaded to {temp}.");

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
        // The app is already elevated, so the installer inherits admin - no extra
        // UAC prompt. Run it interactively (not silent) so the person sees the
        // real, signed PawnIO installer doing the work.
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

    /// <summary>
    /// Valid Authenticode signature (chains to a trusted root, file not tampered)
    /// AND signed by the expected publisher. Both must hold.
    /// </summary>
    private static bool IsTrustedAndSignedBy(string path, string expectedSigner)
    {
        try
        {
            if (!WinVerifyTrustValid(path)) return false;

            // CreateFromSignedFile is the right tool to read a PE's signer, and has
            // no clean modern replacement - the SYSLIB0057 obsoletion swept it up
            // with unrelated cert-loading APIs.
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
