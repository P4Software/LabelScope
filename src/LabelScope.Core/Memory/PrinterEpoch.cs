namespace LabelScope.Core.Memory;

/// <summary>
/// The window's "printer epoch": the stretch of time between two resets of printer memory. A new epoch starts when
/// LabelScope starts and each time the user presses "Clear printer memory". Every job is stamped with the epoch it was
/// drawn in (<see cref="Jobs.LabelJob.Epoch"/>), so a redraw can clear its fresh memory at the same points the live
/// memory was cleared (see <see cref="EpochReplay"/>). Without that, a redraw would replay printer state (a changed
/// command prefix, a ^CI, a stored graphic) into jobs that were drawn after the user had cleared it.
/// </summary>
/// <remarks>
/// An epoch is a <see cref="Guid"/>, not a counter: jobs kept from an earlier session keep their epoch in jobs.json,
/// and a counter restarting at 1 each session would make an old job look like it belongs to today's first epoch.
/// Safe to use from several threads: arrivals read <see cref="Current"/> on socket threads while the UI thread starts
/// a new epoch.
/// </remarks>
public sealed class PrinterEpoch
{
    private readonly object _gate = new();
    private Guid _current = Guid.NewGuid();

    /// <summary>The epoch in force now. A new holder starts its own epoch, which is how each session starts one.</summary>
    public Guid Current
    {
        get
        {
            // Guid is 16 bytes, so a plain read could tear while StartNew writes; the lock keeps it whole.
            lock (_gate) return _current;
        }
    }

    /// <summary>Starts a new epoch (printer memory was just cleared) and returns it.</summary>
    public Guid StartNew()
    {
        var next = Guid.NewGuid();
        lock (_gate) _current = next;
        return next;
    }
}

/// <summary>
/// Clears the printer memory of a redraw at every epoch change, so jobs drawn again oldest first see the printer state
/// the live drawing saw. Use one instance per pass and call <see cref="Enter"/> right before each job is drawn.
/// </summary>
/// <remarks>
/// Jobs without an epoch (kept by a LabelScope older than 0.5.0) all count as one earlier epoch. The first job of a pass
/// never clears anything: the pass starts from a fresh memory anyway. A setup job that was trimmed out of the list is
/// not replayed, so a label that relied on it warns on redraw; that is accepted (the job is no longer there to show).
/// </remarks>
public sealed class EpochReplay
{
    private bool _started;
    private Guid? _last;

    /// <summary>
    /// Call before drawing a job of epoch <paramref name="epoch"/> with <paramref name="memory"/>: when the epoch
    /// differs from the previous job's, the memory is cleared, as the user's Clear (or a restart) cleared it live.
    /// </summary>
    /// <param name="memory">The redraw's own printer memory, never the live one.</param>
    /// <param name="epoch">The job's epoch; null for a job kept by an older LabelScope.</param>
    /// <returns>True when the memory was cleared.</returns>
    public bool Enter(PrinterMemory memory, Guid? epoch)
    {
        ArgumentNullException.ThrowIfNull(memory);
        var changed = _started && _last != epoch;
        _started = true;
        _last = epoch;
        if (changed) memory.Clear();
        return changed;
    }
}
