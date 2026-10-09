using System.Runtime.InteropServices;
using System.Text;

namespace LabelScope.Core.Diagnostics;

/// <summary>
/// Last-resort error log: appends unexpected exceptions to a plain text file. It uses nothing but file
/// operations, so it still works when settings, Serilog or the window are the thing that broke.
/// </summary>
public sealed class CrashLog
{
    /// <summary>Name of the log file.</summary>
    public const string FileName = "crash.log";

    private const string OldFileName = "crash.old.log";

    /// <summary>Longest entry written, in characters. A runaway message must not fill the disk or make the file unreadable.</summary>
    public const int MaxEntryChars = 20_000;

    /// <summary>Longest error message shown to a user in a dialog, in characters.</summary>
    public const int MaxDialogMessageChars = 300;

    // One lock for all instances and threads: a dying socket thread, the finalizer thread (unobserved tasks) and the
    // window thread can report at the same moment. File.AppendAllText only allows readers, so without this one of them
    // would fail with an IOException and its entry would land in the fallback folder, splitting the evidence.
    private static readonly object WriteLock = new();

    private readonly string[] _folders;
    private readonly long _maxBytes;
    private readonly string _appVersion;

    /// <summary>Creates a crash log that writes to <paramref name="primaryFolder"/>, or to <paramref name="fallbackFolder"/> if that fails.</summary>
    /// <param name="primaryFolder">Preferred folder, normally the program's own logs folder.</param>
    /// <param name="fallbackFolder">Used when the preferred folder is not writable (for example a read-only install folder).</param>
    /// <param name="maxBytes">Size at which the file is moved to <c>crash.old.log</c> before the next entry.</param>
    /// <param name="appVersion">Program version, printed in every entry so support knows which build failed.</param>
    public CrashLog(string primaryFolder, string fallbackFolder, long maxBytes = 1_000_000, string appVersion = "")
    {
        _folders = new[] { primaryFolder, fallbackFolder };
        _maxBytes = maxBytes;
        _appVersion = appVersion;
    }

    /// <summary>
    /// Appends an entry for <paramref name="exception"/>. Never throws.
    /// </summary>
    /// <param name="source">Plain-language description of where the error happened.</param>
    /// <param name="exception">The error.</param>
    /// <returns>The file that was written, or null when no folder could be written.</returns>
    public string? Write(string source, Exception? exception)
    {
        string text;
        try
        {
            text = new StringBuilder()
                .Append("==== ").Append(DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss")).Append("Z  ").AppendLine(source)
                .Append("LabelScope ").Append(_appVersion).Append(" | ").Append(RuntimeInformation.OSDescription)
                .Append(" | ").AppendLine(RuntimeInformation.FrameworkDescription)
                .AppendLine(exception?.ToString() ?? "(no exception details)")
                .AppendLine()
                .ToString();
        }
        catch (Exception)
        {
            // Even describing the error failed (a hostile ToString); still leave a trace of where it happened.
            text = "==== " + source + Environment.NewLine + "(the error could not be described)" + Environment.NewLine;
        }

        if (text.Length > MaxEntryChars)
            text = text.Substring(0, MaxEntryChars) + Environment.NewLine + "(truncated)" + Environment.NewLine;

        lock (WriteLock)
        {
            foreach (var folder in _folders)
            {
                try
                {
                    Directory.CreateDirectory(folder);
                    var path = Path.Combine(folder, FileName);
                    Rotate(folder, path);
                    File.AppendAllText(path, text, Encoding.UTF8);
                    return path;
                }
                catch (Exception)
                {
                    // This folder does not work; try the next. There is nobody left to tell if all fail.
                }
            }
        }
        return null;
    }

    /// <summary>Shortens an error message for a dialog so a huge message cannot produce a window larger than the screen.</summary>
    /// <param name="message">The original message, may be null.</param>
    /// <returns>The message, cut after <see cref="MaxDialogMessageChars"/> characters with an ellipsis.</returns>
    public static string ShortMessage(string? message)
    {
        if (string.IsNullOrEmpty(message)) return "";
        return message.Length <= MaxDialogMessageChars ? message : message.Substring(0, MaxDialogMessageChars) + "...";
    }

    /// <summary>Moves an oversized log aside so the file stays small enough to e-mail.</summary>
    private void Rotate(string folder, string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length <= _maxBytes) return;
            File.Move(path, Path.Combine(folder, OldFileName), overwrite: true);
        }
        catch (Exception)
        {
            // Rotation is housekeeping (for example crash.old.log is open in an editor). Never lose the new entry
            // because of it: keep appending to the current, somewhat larger file.
        }
    }
}
