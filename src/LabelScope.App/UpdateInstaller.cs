using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using LabelScope.Core.Updating;

namespace LabelScope.App;

/// <summary>
/// Downloads a new installer, checks that it is really signed by the publisher, and starts it.
/// Nothing is run unless the signature is valid, so a damaged or swapped download is never executed.
/// </summary>
public static class UpdateInstaller
{
    /// <summary>The certificate's name must be exactly this; it is the publisher of every signed release.</summary>
    private const string ExpectedSigner = "Barrdega Sistemas NA LLC";

    /// <summary>
    /// Downloads to a temporary file and verifies it. Returns the file path, or throws
    /// <see cref="InvalidOperationException"/> with a plain-language message.
    /// </summary>
    public static async Task<string> DownloadAsync(UpdateInfo update, IProgress<int>? progress, CancellationToken cancel)
    {
        var folder = Path.Combine(Path.GetTempPath(), "LabelScope-update");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, $"LabelScope-Setup-{update.Version.ToString(3)}.exe");

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("LabelScope-Updater");
            using var response = await http.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancel);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? 0;
            await using var input = await response.Content.ReadAsStreamAsync(cancel);
            await using var output = File.Create(file);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, cancel)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancel);
                done += read;
                if (total > 0) progress?.Report((int)(done * 100 / total));
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            TryDelete(file);
            throw new InvalidOperationException("The new version could not be downloaded. Check the internet connection and try again.");
        }

        if (!IsSignedByPublisher(file))
        {
            TryDelete(file);
            throw new InvalidOperationException("The downloaded installer is not signed by the LabelScope publisher, so it was deleted and not run. Download LabelScope again from the GitHub releases page.");
        }
        return file;
    }

    /// <summary>
    /// Starts the installer quietly. It closes LabelScope (the caller exits the app right after this),
    /// replaces the files, keeps settings.json and opens the new version.
    /// </summary>
    public static void Launch(string installerPath)
    {
        // /SILENT shows only a progress bar. /relaunch=1 is read by the installer script to start the app afterwards.
        Process.Start(new ProcessStartInfo(installerPath, "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /relaunch=1") { UseShellExecute = true });
    }

    /// <summary>Real Authenticode check (file hash and certificate chain) plus the publisher name.</summary>
    private static bool IsSignedByPublisher(string path)
    {
        try
        {
            if (!WinTrust.IsValid(path)) return false;
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
            // Exact match on the parsed name, not a substring: "Evil Barrdega Sistemas NA LLC Ltd" must not pass.
            // The certificate is not pinned by thumbprint because Azure Artifact Signing certificates are short-lived
            // and renewed constantly; the chain check in WinTrust plus this exact name is what can be relied on.
            return string.Equals(cert.GetNameInfo(X509NameType.SimpleName, false), ExpectedSigner, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or IOException)
        {
            return false; // unsigned or unreadable
        }
    }

    private static void TryDelete(string file)
    {
        try { File.Delete(file); } catch (IOException) { /* leftover temp file; harmless */ }
    }

    /// <summary>Windows' own signature check (WinVerifyTrust). It validates the file hash and the certificate chain.</summary>
    private static class WinTrust
    {
        private static readonly Guid ActionGenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        public static bool IsValid(string path)
        {
            var fileInfo = new WinTrustFileInfo { cbStruct = (uint)Marshal.SizeOf<WinTrustFileInfo>(), pcwszFilePath = path };
            var pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
            try
            {
                Marshal.StructureToPtr(fileInfo, pFile, false);
                var data = new WinTrustData
                {
                    cbStruct = (uint)Marshal.SizeOf<WinTrustData>(),
                    dwUIChoice = 2,          // WTD_UI_NONE: never show a dialog
                    fdwRevocationChecks = 1, // WTD_REVOKE_WHOLECHAIN: a revoked certificate is refused (the PC is online, it just downloaded)
                    dwUnionChoice = 1,       // WTD_CHOICE_FILE
                    pFile = pFile,
                };
                var action = ActionGenericVerifyV2;
                return WinVerifyTrust(IntPtr.Zero, ref action, ref data) == 0;
            }
            finally { Marshal.FreeHGlobal(pFile); }
        }

        [DllImport("wintrust.dll", ExactSpelling = true)]
        private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WinTrustData data);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WinTrustFileInfo
        {
            public uint cbStruct;
            [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WinTrustData
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
        }
    }
}
