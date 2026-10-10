using LabelScope.Core.Rendering;

namespace LabelScope.Core.Jobs;

/// <summary>
/// Puts the labels of one send back together into one job. The listener reports each ^XA..^XZ label as soon as it
/// has arrived (so a long job shows its first label at once); every label of one TCP connection carries the same
/// connection number, and this class appends each new label to the job of that connection.
/// </summary>
/// <remarks>
/// <para>
/// Only the new label is drawn, then its labels, warnings, field records and memory notes are appended with their
/// line numbers moved down by the lines that came before. Drawing the whole job again would repeat the earlier labels'
/// printer-memory commands (a ~DG download, an ^ID delete) and cost time that grows with every label. Line numbers
/// refer to the formatted ZPL shown in the ZPL tab. The formatter starts afresh after a ^XZ, so formatting the joined
/// text gives the two formatted parts joined by the blank line it puts between labels; that is checked cheaply at the
/// boundary (the job's formatted text ends with ^XZ, the new label's starts with a command, neither holds a binary
/// download, which is shown unformatted). Anything else starts a new job instead.
/// </para>
/// <para>
/// Cost and memory are bounded: a job takes at most <see cref="MaxLabelsPerJob"/> labels and
/// <see cref="MaxCharsPerJob"/> characters; beyond that the connection's next label starts a continuation job. Each
/// append copies the job's text once (it has to: <see cref="LabelJob.Zpl"/> is one string), so one append costs at
/// most about <see cref="MaxCharsPerJob"/> characters of work, and a connection streaming thousands of labels costs
/// time in proportion to what it sends. The window counts every job in its history budget and trims old ones.
/// </para>
/// <para>
/// Called from socket threads; the labels of one connection arrive one after the other, never at the same time.
/// Connection numbers are never reused while the listener runs, so a forgotten connection (see
/// <see cref="MaxOpenConnections"/>) simply starts a new job with its next label.
/// </para>
/// </remarks>
public sealed class JobAssembler
{
    /// <summary>Most labels one job takes; the next label of the same send starts a continuation job.</summary>
    public const int MaxLabelsPerJob = 100;

    /// <summary>Most ZPL characters one job takes (about 1 MB); a bigger send continues in a new job.</summary>
    public const int MaxCharsPerJob = 1024 * 1024;

    /// <summary>
    /// Connections remembered at most; the listener serves at most 16 at a time, so older ones have finished. The
    /// oldest is forgotten first, and nothing of it stays referenced here.
    /// </summary>
    public const int MaxOpenConnections = 32;

    // What is kept per connection: the job and the facts about its formatted text needed for the next append. The
    // formatted text itself is not kept (it can be a megabyte per connection).
    private sealed record Open(LabelJob Job, int FormattedLines, bool EndsWithLabelEnd, bool HasBinary, int Part);

    private readonly object _lock = new();
    private readonly Dictionary<long, Open> _open = new();
    private readonly Queue<long> _order = new();

    /// <summary>Number of connections remembered now (for tests and diagnostics).</summary>
    public int OpenConnections
    {
        get { lock (_lock) return _open.Count; }
    }

    /// <summary>
    /// Returns the job of connection <paramref name="connectionId"/> with the new label added, or null when the label
    /// starts a new job (first label of the connection, a job that is already full, or text that does not line up).
    /// </summary>
    /// <param name="connectionId">The connection the label came over; 0 never joins anything.</param>
    /// <param name="zpl">The new label's ZPL as received.</param>
    /// <param name="formatted">The new label's ZPL as formatted for display; the label was drawn from this text.</param>
    /// <param name="result">What drawing <paramref name="formatted"/> produced.</param>
    /// <param name="complete">False when the connection ended before this label's ^XZ.</param>
    public LabelJob? TryAppend(long connectionId, string zpl, string formatted, RenderResult result, bool complete)
    {
        ArgumentNullException.ThrowIfNull(zpl);
        ArgumentNullException.ThrowIfNull(formatted);
        ArgumentNullException.ThrowIfNull(result);
        if (connectionId == 0) return null;
        Open open;
        lock (_lock)
            if (!_open.TryGetValue(connectionId, out open!)) return null;

        var previous = open.Job;
        if (previous.LabelCount >= MaxLabelsPerJob || (long)previous.Zpl.Length + zpl.Length + 1 > MaxCharsPerJob) return null;
        // The boundary check described in the remarks: only then is the line shift right.
        var pieceHasBinary = BinaryDownloads.ContainsAny(zpl);
        if (open.HasBinary || pieceHasBinary || !open.EndsWithLabelEnd || !StartsWithCommand(formatted)) return null;

        var offset = open.FormattedLines + 1; // the blank line between labels
        var before = previous.Result;
        var merged = new RenderResult(
            [.. before.Labels, .. result.Labels.Select(l => Shift(l, offset))],
            [.. before.Warnings, .. result.Warnings.Select(w => w with { Line = w.Line + offset })])
        {
            MemoryNotes = [.. before.MemoryNotes, .. result.MemoryNotes],
        };

        var job = new LabelJob
        {
            Id = previous.Id,
            // A name of its own (none for printed jobs today) is kept; otherwise the name comes from the joined ZPL,
            // which is the first label's name.
            Name = previous.Name == JobNamer.NameFromZpl(previous.Zpl) ? "" : previous.Name,
            RemoteAddress = previous.RemoteAddress,
            ResolvedHost = previous.ResolvedHost,
            Origin = previous.Origin,
            ReceivedAt = previous.ReceivedAt,
            Zpl = previous.Zpl + "\n" + zpl,
            Complete = complete,
            Result = merged,
        };
        // Replacing the entry lets go of the previous job object; the window replaces its card's job the same way.
        lock (_lock)
            if (_open.ContainsKey(connectionId))
                _open[connectionId] = new Open(job, open.FormattedLines + 1 + CountLines(formatted), EndsWithLabelEnd(formatted), false, open.Part);
        return job;
    }

    /// <summary>
    /// The part number a new job of connection <paramref name="connectionId"/> would get: 1 for the first job of a
    /// send, 2 and up for continuation jobs (the send was larger than one job holds), so the window can say why one
    /// print made several cards.
    /// </summary>
    /// <param name="connectionId">The connection; 0 always gives 1.</param>
    public int NextPart(long connectionId) => Current(connectionId) is { } open ? open.Part + 1 : 1;

    /// <summary>
    /// The job connection <paramref name="connectionId"/> is filling now, or null; a continuation job takes its name,
    /// so every part of one send carries the same name.
    /// </summary>
    /// <param name="connectionId">The connection; 0 always gives null.</param>
    public LabelJob? CurrentJob(long connectionId) => Current(connectionId)?.Job;

    private Open? Current(long connectionId)
    {
        if (connectionId == 0) return null;
        lock (_lock) return _open.TryGetValue(connectionId, out var open) ? open : null;
    }

    /// <summary>Remembers <paramref name="job"/> as the job of its connection, so the connection's next label joins it.</summary>
    /// <param name="connectionId">The connection; 0 is ignored.</param>
    /// <param name="job">The job that now holds the connection's labels (a new job, or a continuation job).</param>
    /// <param name="formatted">The job's formatted ZPL (what its line numbers refer to).</param>
    /// <param name="part">The job's part of the send (see <see cref="NextPart"/>); 1 for the first job.</param>
    public void Remember(long connectionId, LabelJob job, string formatted, int part = 1)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(formatted);
        if (connectionId == 0) return;
        var entry = new Open(job, CountLines(formatted), EndsWithLabelEnd(formatted), BinaryDownloads.ContainsAny(job.Zpl), Math.Max(1, part));
        lock (_lock)
        {
            if (!_open.ContainsKey(connectionId))
            {
                _order.Enqueue(connectionId);
                while (_order.Count > MaxOpenConnections) _open.Remove(_order.Dequeue());
            }
            _open[connectionId] = entry;
        }
    }

    /// <summary>Forgets every connection, so labels still arriving start new jobs (used by Clear jobs).</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _open.Clear();
            _order.Clear();
        }
    }

    private static RenderedLabel Shift(RenderedLabel label, int offset) =>
        label with { Fields = label.Fields.Select(f => f with { Line = f.Line + offset }).ToList() };

    /// <summary>True when the formatted text's last command is ^XZ (any case), so the formatter starts afresh after it.</summary>
    private static bool EndsWithLabelEnd(string formatted) =>
        formatted.TrimEnd().EndsWith("^XZ", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the formatted text starts with a command, not with stray text the formatter would join.</summary>
    private static bool StartsWithCommand(string formatted) => formatted.Length > 0 && formatted[0] is '^' or '~';

    /// <summary>Lines of formatted text (the formatter writes only \n).</summary>
    private static int CountLines(string text)
    {
        if (text.Length == 0) return 0;
        var lines = 1;
        foreach (var c in text) if (c == '\n') lines++;
        return lines;
    }
}
