using System.Globalization;
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
/// may hold several labels (^XA..^XZ blocks). The job keeps raw facts (the ZPL exactly as received, the sender's
/// address, a name that is empty when the default applies); everything a person reads is produced from them when it
/// is read, in the current language, so a language switch changes every card without losing jobs.
/// </summary>
public sealed class LabelJob
{
    private readonly string _zpl = "";
    private readonly string _zplName = "";
    private string? _name;
    private RenderResult _result = new([], []);
    private string? _resolvedHost;

    // The Log, together with everything it was built from. Replaced as one reference, so a reader on another thread
    // always sees a log that matches one result, never a mix of an old log and a new result.
    private LogCache? _logCache;

    private sealed record LogCache(RenderResult Result, string? Host, CultureInfo Culture, IReadOnlyList<JobLogEntry> Log);

    /// <summary>Identifies the job across saves, so a reloaded job is the same job.</summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>
    /// The stored name: by default the name found in the ZPL (<see cref="JobNamer.NameFromZpl"/>), or a name set by
    /// the caller. Empty when the localized default applies; show <see cref="DisplayName"/>.
    /// </summary>
    public string Name
    {
        get => _name ?? _zplName;
        // Empty means "no name of its own": the name found in the ZPL (or the default) applies.
        init => _name = string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>The name shown on the card, in the current language ("Label" when the ZPL gives none).</summary>
    public string DisplayName => JobNamer.DisplayName(Name);

    /// <summary>The sender's IP address as received; null for a file or a paste (and for an unknown sender).</summary>
    public string? RemoteAddress { get; init; }

    /// <summary>
    /// The sender's short host name, once found by <see cref="ResolveHostAsync"/> (or restored from jobs.json); null
    /// while unknown or when the sender has none. Never looked up on read.
    /// </summary>
    public string? ResolvedHost
    {
        get => Volatile.Read(ref _resolvedHost);
        init => _resolvedHost = value;
    }

    /// <summary>Where the job came from, as shown on the card, in the current language (see <see cref="JobNamer.DisplaySource"/>).</summary>
    public string DisplaySource => JobNamer.DisplaySource(Origin, RemoteAddress, ResolvedHost);

    /// <summary>How the job reached LabelScope; decides the source text and the wording of the "received" log line.</summary>
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
            // Worked out once here: a job can be 16 MB, and the card asks for these on every repaint.
            LineCount = CountLines(ZplFormatter.Format(_zpl));
            ByteCount = Encoding.UTF8.GetByteCount(_zpl);
            _zplName = JobNamer.NameFromZpl(_zpl);
        }
    }

    /// <summary>
    /// False when the sender stopped before the last ^XZ (the connection closed or went quiet), so the last label may
    /// be missing parts.
    /// </summary>
    public bool Complete { get; init; } = true;

    /// <summary>
    /// True when the job's bytes were not valid UTF-8 and were read as Windows-1252. Kept with the job because every
    /// redraw must pass it on (<see cref="RenderOptions.TextReadAsWindows1252"/>), or a ^CI28 label would lose its
    /// warning after a change of language or label size.
    /// </summary>
    public bool ReadAsWindows1252 { get; init; }

    /// <summary>
    /// The drawn labels, warnings and memory notes. Replace it after drawing <see cref="Zpl"/> again (language or label
    /// size changed); the <see cref="Log"/> follows at once. It may be replaced from any thread (a re-render can finish
    /// on a worker thread while the window reads the job): every reader sees either the old or the new result whole.
    /// </summary>
    public required RenderResult Result
    {
        get => Volatile.Read(ref _result);
        set => Volatile.Write(ref _result, value ?? throw new ArgumentNullException(nameof(value)));
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
    /// printer-memory note, then each warning with its line. Built from the current <see cref="Result"/> in the
    /// current language, and rebuilt when either (or the host name) changes.
    /// </summary>
    public IReadOnlyList<JobLogEntry> Log
    {
        get
        {
            // Each input is read once, so the log and the cache key describe the same state even if another thread
            // replaces the result meanwhile.
            var result = Result;
            var host = ResolvedHost;
            var culture = Text.Culture;
            var cache = Volatile.Read(ref _logCache);
            if (cache is not null && ReferenceEquals(cache.Result, result) && cache.Host == host && Equals(cache.Culture, culture))
                return cache.Log;
            var log = BuildLog(result, host);
            Volatile.Write(ref _logCache, new LogCache(result, host, culture, log));
            return log;
        }
    }

    /// <summary>
    /// Looks up the sender's host name once and keeps it in <see cref="ResolvedHost"/>; later calls, a file, a paste or
    /// a sender on this computer return at once. Runs without blocking; never throws.
    /// </summary>
    /// <param name="resolver">Looks up host names; null uses <see cref="HostNameResolver.Default"/> (system DNS, 300 ms).</param>
    /// <param name="cancellationToken">Stops waiting; the IP address stays the source.</param>
    public async Task ResolveHostAsync(HostNameResolver? resolver = null, CancellationToken cancellationToken = default)
    {
        if (ResolvedHost is not null || !JobNamer.NeedsLookup(Origin, RemoteAddress)) return;
        var host = await (resolver ?? HostNameResolver.Default).ResolveAsync(RemoteAddress!, cancellationToken).ConfigureAwait(false);
        if (host is not null) Interlocked.CompareExchange(ref _resolvedHost, host, null);
    }

    /// <summary>The raw facts to write to jobs.json; the drawn result is not saved because it is redrawn on load.</summary>
    public SavedJob ToSaved() => new(Id, Name, RemoteAddress, ResolvedHost, Origin, ReceivedAt, Zpl, Complete, ReadAsWindows1252);

    /// <summary>Rebuilds a job read from jobs.json, with <paramref name="result"/> from drawing its ZPL again.</summary>
    public static LabelJob FromSaved(SavedJob saved, RenderResult result)
    {
        ArgumentNullException.ThrowIfNull(saved);
        return new LabelJob
        {
            Id = saved.Id,
            Name = saved.Name ?? "",
            RemoteAddress = saved.RemoteAddress,
            ResolvedHost = saved.ResolvedHost,
            Origin = saved.Origin,
            ReceivedAt = saved.ReceivedAt,
            Zpl = saved.Zpl ?? "",
            Complete = saved.Complete,
            ReadAsWindows1252 = saved.ReadAsWindows1252,
            Result = result,
        };
    }

    private List<JobLogEntry> BuildLog(RenderResult result, string? host)
    {
        var log = new List<JobLogEntry>
        {
            new(ReceivedAt, Origin switch
            {
                JobOrigin.OpenedFromFile => Text.Get("Log_OpenedFromFile", ByteCount),
                JobOrigin.Pasted => Text.Get("Log_Pasted", ByteCount),
                _ => Text.Get("Log_Received", JobNamer.DisplaySource(Origin, RemoteAddress, host), ByteCount),
            }, null),
        };
        if (!Complete) log.Add(new(ReceivedAt, Text.Get("Log_Incomplete"), null));
        foreach (var note in result.MemoryNotes) log.Add(new(ReceivedAt, note, null));
        foreach (var w in result.Warnings) log.Add(new(ReceivedAt, w.Message, w.Line));
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
