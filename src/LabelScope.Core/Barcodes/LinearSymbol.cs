namespace LabelScope.Core.Barcodes;

/// <summary>
/// Builds the alternating bar/space widths of a linear barcode in dots. The first element is always a bar.
/// Encoders describe symbols in whatever form the standard uses (n/w patterns, module counts or bits) and
/// this class turns them into dot widths.
/// </summary>
internal sealed class RunList
{
    private readonly List<int> _runs = new();

    /// <summary>True when the next element added will be a bar (false: a space).</summary>
    public bool NextIsBar => _runs.Count % 2 == 0;

    /// <summary>Adds one element of <paramref name="dots"/> width.</summary>
    public RunList Add(int dots)
    {
        if (dots <= 0) throw new ArgumentOutOfRangeException(nameof(dots));
        _runs.Add(dots);
        return this;
    }

    /// <summary>Adds elements written as 'n' (narrow) and 'w' (wide), alternating bar and space.</summary>
    public RunList AddPattern(string nw, int narrow, int wide)
    {
        foreach (var c in nw) Add(c is 'w' or 'W' ? wide : narrow);
        return this;
    }

    /// <summary>Adds elements written as digits that are widths in module units (Code 128 style "212222").</summary>
    public RunList AddUnits(string widths, int unit)
    {
        foreach (var c in widths) Add((c - '0') * unit);
        return this;
    }

    /// <summary>
    /// Adds modules written as bits ('1' bar, '0' space). Neighbouring equal bits merge into one run, so
    /// "1101" is a bar of two modules, a space and a bar. A symbol must start with a bar.
    /// </summary>
    public RunList AddBits(string bits, int unit)
    {
        foreach (var bit in bits)
        {
            var isBar = bit == '1';
            if (_runs.Count == 0)
            {
                if (!isBar) throw new InvalidOperationException("A barcode must start with a bar.");
                _runs.Add(unit);
                continue;
            }
            var lastIsBar = (_runs.Count - 1) % 2 == 0;
            if (lastIsBar == isBar) _runs[^1] += unit; else _runs.Add(unit);
        }
        return this;
    }

    /// <summary>The finished widths.</summary>
    public int[] ToArray() => _runs.ToArray();
}

/// <summary>A piece of the interpretation line with the horizontal range (in dots from the first bar) it is centred in.</summary>
/// <param name="Text">The characters to print.</param>
/// <param name="X0">Left end of the range; negative for a digit that hangs to the left of the first bar.</param>
/// <param name="X1">Right end of the range; may exceed the symbol width for a digit hanging to the right.</param>
internal sealed record TextSpan(string Text, int X0, int X1);

/// <summary>A linear barcode ready to draw.</summary>
/// <param name="Runs">Bar, space, bar, ... widths in dots; index 0 is a bar.</param>
/// <param name="Text">Interpretation line, centred under the whole symbol when <paramref name="Spans"/> is null.</param>
/// <param name="Spans">EAN/UPC digit groups; when set they replace <paramref name="Text"/> for drawing.</param>
/// <param name="GuardBars">Indexes (into <paramref name="Runs"/>) of bars that reach down into the text zone.</param>
internal sealed record LinearSymbol(
    int[] Runs,
    string Text,
    IReadOnlyList<TextSpan>? Spans = null,
    IReadOnlySet<int>? GuardBars = null)
{
    /// <summary>Total width in dots, from the left edge of the first bar to the right edge of the last.</summary>
    public int Width => Runs.Sum();
}
