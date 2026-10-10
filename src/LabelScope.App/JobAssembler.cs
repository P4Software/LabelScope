using LabelScope.Core.Jobs;
using LabelScope.Core.Rendering;

namespace LabelScope.App;

/// <summary>
/// Puts the labels of one send back together into one job. The listener reports each ^XA..^XZ label as soon as it
/// has arrived (so a long job shows its first label at once); every label of one TCP connection carries the same
/// connection number, and this class appends each new label to the job of that connection.
/// </summary>
/// <remarks>
/// Only the new label is drawn, then its labels, warnings, field records and memory notes are appended with their
/// line numbers moved down by the lines that came before. Drawing the whole job again would repeat the earlier labels'
/// printer-memory commands (a ~DG download, an ^ID delete) and cost time that grows with every label. Line numbers
/// refer to the formatted ZPL shown in the ZPL tab; formatting the joined text gives the formatted parts joined by
/// the blank line the formatter puts between labels, which is checked before every append. Called from socket
/// threads; the labels of one connection arrive one after the other, never at the same time.
/// </remarks>
public sealed class JobAssembler
{
    // A send can carry thousands of labels. Each append copies the job's text, so beyond these limits the next labels
    // start a new job (a continuation card) rather than making every append slower.
    private const int MaxLabelsPerJob = 200;
    private const int MaxCharsPerJob = 8 * 1024 * 1024;

    // At most this many connections are remembered; the listener serves at most 16 at a time, so older ones are done.
    private const int MaxOpenConnections = 32;

    private readonly object _lock = new();
    private readonly Dictionary<long, (LabelJob Job, string Formatted)> _open = new();
    private readonly Queue<long> _order = new();

    /// <summary>
    /// Returns the job of connection <paramref name="connectionId"/> with the new label added, or null when the label
    /// starts a new job (first label of the connection, a job that is already large, or text that does not line up).
    /// </summary>
    /// <param name="connectionId">The connection the label came over; 0 never joins anything.</param>
    /// <param name="zpl">The new label's ZPL as received.</param>
    /// <param name="formatted">The new label's ZPL as formatted for display; the label was drawn from this text.</param>
    /// <param name="result">What drawing <paramref name="formatted"/> produced.</param>
    /// <param name="complete">False when the connection ended before this label's ^XZ.</param>
    public LabelJob? TryAppend(long connectionId, string zpl, string formatted, RenderResult result, bool complete)
    {
        if (connectionId == 0) return null;
        (LabelJob Job, string Formatted) open;
        lock (_lock)
            if (!_open.TryGetValue(connectionId, out open)) return null;

        var previous = open.Job;
        if (previous.LabelCount >= MaxLabelsPerJob || previous.Zpl.Length + zpl.Length > MaxCharsPerJob) return null;

        var joined = previous.Zpl + "\n" + zpl;
        var joinedFormatted = ZplFormatter.Format(joined);
        // The line shift is only right when the joined text formats to the two parts plus the blank line between
        // labels; binary downloads are shown unformatted and may not line up. Such a label simply starts a new job.
        if (!string.Equals(joinedFormatted, open.Formatted + "\n\n" + formatted, StringComparison.Ordinal)) return null;

        var offset = CountLines(open.Formatted) + 1;
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
            Name = previous.Name == JobNamer.NameFromZpl(previous.Zpl) ? "" : previous.Name, // keep a name of its own
            RemoteAddress = previous.RemoteAddress,
            ResolvedHost = previous.ResolvedHost,
            Origin = previous.Origin,
            ReceivedAt = previous.ReceivedAt,
            Zpl = joined,
            Complete = complete,
            Result = merged,
        };
        Remember(connectionId, job, joinedFormatted);
        return job;
    }

    /// <summary>Remembers <paramref name="job"/> as the job of its connection, so the connection's next label joins it.</summary>
    /// <param name="connectionId">The connection; 0 is ignored.</param>
    /// <param name="job">The job that now holds the connection's labels.</param>
    /// <param name="formatted">The job's formatted ZPL (what its line numbers refer to).</param>
    public void Remember(long connectionId, LabelJob job, string formatted)
    {
        if (connectionId == 0) return;
        lock (_lock)
        {
            if (!_open.ContainsKey(connectionId))
            {
                _order.Enqueue(connectionId);
                while (_order.Count > MaxOpenConnections) _open.Remove(_order.Dequeue());
            }
            _open[connectionId] = (job, formatted);
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

    /// <summary>Lines of formatted text (the formatter writes only \n).</summary>
    private static int CountLines(string text)
    {
        if (text.Length == 0) return 0;
        var lines = 1;
        foreach (var c in text) if (c == '\n') lines++;
        return lines;
    }
}
