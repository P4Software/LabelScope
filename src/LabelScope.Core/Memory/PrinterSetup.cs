namespace LabelScope.Core.Memory;

/// <summary>
/// Label setup a real printer keeps from one job to the next: print width (^PW), label length (^LL), label home (^LH),
/// print orientation (^PO) and reverse-all (^LR). Label programs often send these once, in a setup job of their own,
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
    public sealed record Values(int? WidthDots, int? HeightDots, int? HomeX, int? HomeY, bool? Inverted, bool? ReverseAll)
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
                changes.ReverseAll ?? _values.ReverseAll);
        }
    }

    /// <summary>Forgets everything, as switching the printer off does.</summary>
    internal void Reset()
    {
        lock (_gate) _values = Values.None;
    }
}
