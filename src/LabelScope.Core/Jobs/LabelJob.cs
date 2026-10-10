using System.Text;
using LabelScope.Core.Rendering;

namespace LabelScope.Core.Jobs;

/// <summary>How a job reached LabelScope.</summary>
public enum JobOrigin
{
    /// <summary>Sent to the printer port, by the Windows printer or another computer.</summary>
    Printed,

    /// <summary>Loaded with "Open ZPL file".</summary>
    OpenedFromFile,

    /// <summary>Taken from the clipboard with "Paste ZPL".</summary>
    Pasted,
}

/// <summary>One line in a job's Log tab.</summary>
/// <param name="At">When it happened (the time the job arrived; rendering takes no noticeable time).</param>
/// <param name="Message">Plain-language text in the language that was active when the log was built.</param>
/// <param name="Line">1-based line in the ZPL the entry is about, or null when it is about the whole job.</param>
public sealed record JobLogEntry(DateTimeOffset At, string Message, int? Line);

/// <summary>
/// One print job: everything that came in with one send (one TCP connection, one opened file or one paste), which
/// may hold several labels (^XA..^XZ blocks). The ZPL is kept exactly as received so the job can be drawn again when
/// the language or the label size changes; assigning <see cref="Result"/> rebuilds the <see cref="Log"/>.
/// </summary>
public sealed class LabelJob
{
    private readonly string _zpl = "";
    private RenderResult _result = new([], []);
    // Built on first read, after the object initializer has set every property, and dropped when Result changes.
    private IReadOnlyList<JobLogEntry>? _log;

    /// <summary>Identifies the job across saves, so a reloaded job is the same job.</summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>The name shown on the card (see <see cref="JobNamer.Name"/>).</summary>
    public required string Name { get; init; }

    /// <summary>Where the job came from, as shown on the card (see <see cref="JobNamer.Source"/>).</summary>
    public required string Source { get; init; }

    /// <summary>How the job reached LabelScope; decides the wording of the "received" log line.</summary>
    public JobOrigin Origin { get; init; }

    /// <summary>When the job arrived.</summary>
    public DateTimeOffset ReceivedAt { get; init; }

    /// <summary>The ZPL exactly as received, never reformatted, so a re-render draws the same labels.</summary>
    public required string Zpl
    {
        get => _zpl;
        init
        {
            _zpl = value ?? "";
            // Counted once here: a job can be 16 MB, and the card asks for this number on every repaint.
            LineCount = CountLines(ZplFormatter.Format(_zpl));
            ByteCount = Encoding.UTF8.GetByteCount(_zpl);
        }
    }

    /// <summary>
    /// False when the sender stopped before the last ^XZ (the connection closed or went quiet), so the last label may
    /// be missing parts.
    /// </summary>
    public bool Complete { get; init; } = true;

    /// <summary>
    /// The drawn labels, warnings and memory notes. Replace it after drawing <see cref="Zpl"/> again (language or label
    /// size changed); the <see cref="Log"/> is rebuilt at once, in the current language.
    /// </summary>
    public required RenderResult Result
    {
        get => _result;
        set
        {
            _result = value ?? throw new ArgumentNullException(nameof(value));
            _log = null;
        }
    }

    /// <summary>Lines of the formatted ZPL (as shown in the ZPL tab); 0 for empty ZPL.</summary>
    public int LineCount { get; private init; }

    /// <summary>
    /// Size of the ZPL in bytes, counted as UTF-8. The listener does not pass on the raw byte count, so a label sent in
    /// Windows-1252 with accented letters reads a few bytes larger than what went over the wire.
    /// </summary>
    public int ByteCount { get; private init; }

    /// <summary>Number of labels in the job (^XA..^XZ blocks drawn).</summary>
    public int LabelCount => Result.Labels.Count;

    /// <summary>Number of problems found while drawing; the card shows "Rendered OK" when 0.</summary>
    public int ErrorCount => Result.Warnings.Count;

    /// <summary>
    /// What happened with the job, in order: received (time, source, size), a note when the job was cut off, each
    /// printer-memory note, then each warning with its line.
    /// </summary>
    public IReadOnlyList<JobLogEntry> Log => _log ??= BuildLog();

    /// <summary>The fields to write to jobs.json; the drawn result is not saved because it is redrawn on load.</summary>
    public SavedJob ToSaved() => new(Id, Name, Source, Origin, ReceivedAt, Zpl, Complete);

    /// <summary>Rebuilds a job read from jobs.json, with <paramref name="result"/> from drawing its ZPL again.</summary>
    public static LabelJob FromSaved(SavedJob saved, RenderResult result)
    {
        ArgumentNullException.ThrowIfNull(saved);
        return new LabelJob
        {
            Id = saved.Id,
            Name = saved.Name,
            Source = saved.Source,
            Origin = saved.Origin,
            ReceivedAt = saved.ReceivedAt,
            Zpl = saved.Zpl,
            Complete = saved.Complete,
            Result = result,
        };
    }

    private List<JobLogEntry> BuildLog()
    {
        // Messages are looked up now, not when the job arrived, so a re-render after a language switch reads in the
        // new language.
        var log = new List<JobLogEntry>
        {
            new(ReceivedAt, Origin switch
            {
                JobOrigin.OpenedFromFile => Text.Get("Log_OpenedFromFile", ByteCount),
                JobOrigin.Pasted => Text.Get("Log_Pasted", ByteCount),
                _ => Text.Get("Log_Received", Source, ByteCount),
            }, null),
        };
        if (!Complete) log.Add(new(ReceivedAt, Text.Get("Log_Incomplete"), null));
        foreach (var note in _result.MemoryNotes) log.Add(new(ReceivedAt, note, null));
        foreach (var w in _result.Warnings) log.Add(new(ReceivedAt, w.Message, w.Line));
        return log;
    }

    private static int CountLines(string text)
    {
        if (text.Length == 0) return 0;
        var lines = 1;
        for (var i = 0; i < text.Length; i++)
        {
            // CR LF counts once; a lone CR or LF (formatter output, or raw binary downloads) counts too.
            if (text[i] == '\n') lines++;
            else if (text[i] == '\r' && (i + 1 >= text.Length || text[i + 1] != '\n')) lines++;
        }
        return lines;
    }
}
