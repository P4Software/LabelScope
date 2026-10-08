// src/LabelScope.Core/Printing/PowerShellRunner.cs
using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace LabelScope.Core.Printing;

/// <summary>Runs scripts with the Windows PowerShell that ships with every Windows installation.</summary>
public sealed class PowerShellRunner : IPowerShellRunner
{
    private const int UserCancelledError = 1223; // ERROR_CANCELLED: user said No to the Windows prompt

    /// <inheritdoc />
    public async Task<string> RunAsync(string script, bool elevated, CancellationToken ct = default)
    {
        // An elevated process cannot share stdout with us, so every run writes its output to a file.
        // Only the *result* goes through this file; the script itself is passed encoded on the command
        // line, so there is no script file on disk that another program could change before it runs elevated.
        var resultPath = Path.Combine(Path.GetTempPath(), "LabelScope-" + Guid.NewGuid().ToString("N") + ".txt");
        var wrapped = $"& {{\n{script}\n}} | Out-File -LiteralPath ({PrinterInstaller.DecodeExpression(resultPath)}) -Encoding utf8";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(wrapped));

        var psi = new ProcessStartInfo("powershell.exe",
            $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand {encoded}")
        {
            UseShellExecute = true,           // required for Verb = "runas"
            WindowStyle = ProcessWindowStyle.Hidden,
            Verb = elevated ? "runas" : "",
        };

        try
        {
            using var process = Process.Start(psi)
                                ?? throw new InvalidOperationException("Windows PowerShell could not be started.");
            await process.WaitForExitAsync(ct);
            // Trim a leading BOM too: Out-File -Encoding utf8 writes one on Windows PowerShell 5.1.
            return File.Exists(resultPath) ? (await File.ReadAllTextAsync(resultPath, ct)).Trim().Trim('\uFEFF').Trim() : "";
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == UserCancelledError)
        {
            throw new OperationCanceledException("The user declined the Windows permission prompt.", ex);
        }
        finally
        {
            try { File.Delete(resultPath); } catch (IOException) { /* a leftover temp file is harmless */ }
        }
    }
}
