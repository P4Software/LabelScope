using LabelScope.Core.Rendering;

namespace LabelScope.Core.Memory;

/// <summary>
/// Label setup a real printer keeps from one job to the next: print width (^PW), label length (^LL), label home (^LH),
/// print orientation (^PO), reverse-all (^LR), and the clock: the time set with ^ST, the ^SO2 / ^SO3 offsets and the
/// ^SL language, which the guide says a printer keeps until they are changed or it is switched off. Label programs often send these once, in a setup job of their own,
/// and the labels that follow rely on them; forgetting them would draw those labels on the wrong size.
/// Kept until LabelScope closes or the user clears printer memory, as a printer keeps them until it is switched off.
/// Safe to use from several threads.
/// </summary>
public sealed class PrinterSetup
{
    private readonly object _gate = new();
    private Values _values = Values.None;

    /// <summary>
    /// Held while a job is drawn. A setup job (^PW, ^LL...) and the label after it may arrive on separate connections
    /// and be drawn at the same time; drawing jobs one after another means the label always sees the setup.
    /// </summary>
    internal object JobGate { get; } = new();

    /// <summary>The setup values; null means "never set" (that side of the label is then sized to what is drawn).</summary>
    /// <param name="WidthDots">Last ^PW, in dots.</param>
    /// <param name="HeightDots">Last ^LL, in dots.</param>
    /// <param name="HomeX">Last ^LH x, in dots.</param>
    /// <param name="HomeY">Last ^LH y, in dots.</param>
    /// <param name="Inverted">Last ^PO: true for ^POI (upside down).</param>
    /// <param name="ReverseAll">Last ^LR: true for ^LRY.</param>
    /// <param name="ClockOffset">
    /// What the last ^ST set, as the difference between the set time and the job's time in UTC (see
    /// <c>FieldClock.PrinterNow</c>): later jobs show the set time moved on by the time since, as a printer's battery
    /// clock does. Null when no ^ST was sent, so the clock is the job's own time.
    /// </param>
    /// <param name="ClockSecondary">Last ^SO2 offset; null when none was sent (the secondary clock is then unset).</param>
    /// <param name="ClockThird">Last ^SO3 offset; null when none was sent.</param>
    /// <param name="ClockLanguage">Last ^SL language (1-18); null when none was sent (English).</param>
    public sealed record Values(int? WidthDots, int? HeightDots, int? HomeX, int? HomeY, bool? Inverted, bool? ReverseAll,
                                TimeSpan? ClockOffset = null, ClockShift? ClockSecondary = null, ClockShift? ClockThird = null,
                                int? ClockLanguage = null)
    {
        /// <summary>Nothing set yet.</summary>
        public static readonly Values None = new(null, null, null, null, null, null);
    }

    /// <summary>A consistent copy of the current setup.</summary>
    public Values Current
    {
        get { lock (_gate) return _values; }
    }

    /// <summary>Keeps every value <paramref name="changes"/> sets (non-null) and leaves the others as they were.</summary>
    internal void Apply(Values changes)
    {
        lock (_gate)
        {
            _values = new Values(
                changes.WidthDots ?? _values.WidthDots,
                changes.HeightDots ?? _values.HeightDots,
                changes.HomeX ?? _values.HomeX,
                changes.HomeY ?? _values.HomeY,
                changes.Inverted ?? _values.Inverted,
                changes.ReverseAll ?? _values.ReverseAll,
                changes.ClockOffset ?? _values.ClockOffset,
                changes.ClockSecondary ?? _values.ClockSecondary,
                changes.ClockThird ?? _values.ClockThird,
                changes.ClockLanguage ?? _values.ClockLanguage);
        }
    }

    /// <summary>Forgets everything, as switching the printer off does.</summary>
    internal void Reset()
    {
        lock (_gate) _values = Values.None;
    }
}
