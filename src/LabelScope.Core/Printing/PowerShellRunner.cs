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
            throw new InvalidOperationException(Text.Get("Runner_PowerShellMissing"));

        // An elevated process cannot share stdout with us, so every run writes its output to a file.
        // Only the *result* goes through this file; the script itself is passed as plain -Command text on the
        // command line (ASCII only, no double quote, values only as base64; see BuildArguments), so there is no
        // script file on disk that another program could change before it runs elevated.
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
                                ?? throw new InvalidOperationException(Text.Get("Runner_NotStarted"));
            try
            {
                await process.WaitForExitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                // Cancelled by the caller or by the installer's timeout: do not leave a hidden PowerShell running.
                // Only the process object this runner started is touched. An elevated process cannot be ended
                // from this unelevated program; Windows then refuses and the script simply finishes on its own.
                // Ending the process is best effort, so ANY failure is ignored. Killing an elevated process is refused
                // with an AggregateException (not a plain Win32Exception); letting it escape would hide the real
                // reason (timeout or cancel) behind "One or more errors occurred".
                try { process.Kill(entireProcessTree: true); }
                catch (Exception) { /* already gone, or not ours to end */ }
                throw;
            }
            // Trim a leading BOM too: Out-File -Encoding utf8 writes one on Windows PowerShell 5.1.
            return File.Exists(resultPath) ? (await File.ReadAllTextAsync(resultPath, ct)).Trim().Trim('\uFEFF').Trim() : "";
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == UserCancelledError)
        {
            throw new OperationCanceledException("The user declined the Windows permission prompt.", ex);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == CommandLineTooLongError)
        {
            throw new InvalidOperationException(Text.Get("Runner_NameTooLong"), ex);
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException(Text.Get("Runner_StartFailed", ex.Message.TrimEnd('.')), ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(Text.Get("Runner_AnswerUnreadable", ex.Message.TrimEnd('.')), ex);
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
            throw new InvalidOperationException(Text.Get("Runner_UnsafeScript"));

        // Out-File wraps lines at the console width (80 on Windows PowerShell 5.1) which would cut the status
        // JSON in two, so -Width is raised. -NoClobber refuses to overwrite a file somebody pre-created.
        var wrapped = $"&{{{script}}}|Out-File -LiteralPath ({PrinterInstaller.DecodeExpression(resultPath)}) -Enc utf8 -Width 4096 -NoClobber";
        return "-NoProfile -Command \"" + wrapped + "\"";
    }
}
