// src/LabelScope.Core/Printing/PowerShellRunner.cs
using System.ComponentModel;
using System.Diagnostics;

namespace LabelScope.Core.Printing;

/// <summary>Runs scripts with the Windows PowerShell that ships with every Windows installation.</summary>
public sealed class PowerShellRunner : IPowerShellRunner
{
    private const int UserCancelledError = 1223;     // ERROR_CANCELLED: user said No to the Windows prompt
    private const int CommandLineTooLongError = 206; // ERROR_FILENAME_EXCED_RANGE

    /// <summary>
    /// Runs the script. A declined Windows permission prompt throws <see cref="OperationCanceledException"/>;
    /// every other failure throws <see cref="InvalidOperationException"/> with a plain-language message.
    /// </summary>
    public async Task<string> RunAsync(string script, bool elevated, CancellationToken ct = default)
    {
        // Never start a bare "powershell.exe": ShellExecute searches the current and application folders first,
        // so a planted powershell.exe there would run ELEVATED. Use the copy in the Windows system folder.
        var exe = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(exe))
            throw new InvalidOperationException("Windows PowerShell was not found on this computer, so the printer cannot be managed. Repair Windows and try again.");

        // An elevated process cannot share stdout with us, so every run writes its output to a file.
        // Only the *result* goes through this file; the script itself is passed encoded on the command
        // line, so there is no script file on disk that another program could change before it runs elevated.
        var resultPath = Path.Combine(Path.GetTempPath(), "LabelScope-" + Guid.NewGuid().ToString("N") + ".txt");

        var psi = new ProcessStartInfo(exe, BuildArguments(script, resultPath))
        {
            UseShellExecute = true,           // required for Verb = "runas"
            WindowStyle = ProcessWindowStyle.Hidden,
            Verb = elevated ? "runas" : "",
            WorkingDirectory = Environment.SystemDirectory, // never run from a folder an attacker may control
        };

        try
        {
            using var process = Process.Start(psi)
                                ?? throw new InvalidOperationException("Windows PowerShell could not be started.");
            await process.WaitForExitAsync(ct);
            // Trim a leading BOM too: Out-File -Encoding utf8 writes one on Windows PowerShell 5.1.
            return File.Exists(resultPath) ? (await File.ReadAllTextAsync(resultPath, ct)).Trim().Trim('﻿').Trim() : "";
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == UserCancelledError)
        {
            throw new OperationCanceledException("The user declined the Windows permission prompt.", ex);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == CommandLineTooLongError)
        {
            throw new InvalidOperationException("The printer name is too long for Windows to install. Use a shorter PrinterName in settings.json.", ex);
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException($"Windows PowerShell could not be started ({ex.Message.TrimEnd('.')}).", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"The answer from Windows PowerShell could not be read ({ex.Message.TrimEnd('.')}).", ex);
        }
        finally
        {
            try { File.Delete(resultPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* a leftover temp file is harmless */ }
        }
    }

    /// <summary>
    /// Builds the command-line arguments for <paramref name="script"/>. Kept separate (and internal) so the
    /// length can be tested without starting a process: ShellExecute has historically refused parameter strings
    /// of about 2048 characters or more.
    /// </summary>
    /// <remarks>
    /// The script is passed as plain text in double quotes, not with -EncodedCommand. The encoded form is the
    /// UTF-16 bytes in base64, about 2.7 times longer than the text, which would not fit. Plain text is safe here
    /// because the installer's scripts are pure ASCII and contain no double quote (every variable value travels
    /// as base64), which is checked below. The path of the result file is also passed as base64 so that no
    /// character in it (a user name with a typographic quote, say) can end the quoted string. Only the switches
    /// that are needed are used: <c>-ExecutionPolicy</c> only affects script files, and the window is already
    /// hidden through ProcessStartInfo.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The script contains a double quote or a non-ASCII character.</exception>
    internal static string BuildArguments(string script, string resultPath)
    {
        if (script.Any(ch => ch == '"' || ch > 126 || (ch < 32 && ch != '\n' && ch != '\r' && ch != '\t')))
            throw new InvalidOperationException("The printer script contains a character that cannot be passed to Windows PowerShell safely.");

        // Out-File wraps lines at the console width (80 on Windows PowerShell 5.1) which would cut the status
        // JSON in two, so -Width is raised. -NoClobber refuses to overwrite a file somebody pre-created.
        var wrapped = $"&{{{script}}}|Out-File -LiteralPath ({PrinterInstaller.DecodeExpression(resultPath)}) -Enc utf8 -Width 4096 -NoClobber";
        return "-NoProfile -Command \"" + wrapped + "\"";
    }
}
