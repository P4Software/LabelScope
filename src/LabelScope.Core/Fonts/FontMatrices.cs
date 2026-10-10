namespace LabelScope.Core.Fonts;

/// <summary>One of Zebra's fixed-cell bitmap fonts (A to H) at one print density, all values in dots.</summary>
/// <param name="Id">Font letter.</param>
/// <param name="Height">Cell height (the 1x character height).</param>
/// <param name="Width">Cell width (the 1x character width).</param>
/// <param name="Gap">Fixed space between characters.</param>
/// <param name="Baseline">Distance from the top of the cell to the baseline.</param>
/// <param name="UppercaseOnly">True for fonts that have capital letters only (type "U").</param>
internal sealed record CellFontSpec(char Id, int Height, int Width, int Gap, int Baseline, bool UppercaseOnly);

/// <summary>
/// Zebra's built-in font sizes (from the ZPL guide's "Font Matrices" and "Proportional and Fixed Spacing"
/// pages). These drive line widths, wrapping and placement; the letter shapes come from the bundled fonts.
/// </summary>
internal static class FontMatrices
{
    /// <summary>Print density in dots per millimetre for a printer resolution (152, 203, 300 or 600 dpi).</summary>
    public static int DotsPerMm(int dpi) => dpi switch { < 178 => 6, < 250 => 8, < 450 => 12, _ => 24 };

    // 8 dots/mm (203 dpi): the only density for which Zebra lists both the gap and the baseline.
    private static readonly CellFontSpec[] At8 =
    [
        new('A', 9, 5, 1, 7, false),
        new('B', 11, 7, 2, 11, true),
        new('C', 18, 10, 2, 14, false),
        new('D', 18, 10, 2, 14, false),
        new('E', 28, 15, 5, 23, false),
        new('F', 26, 13, 3, 21, false),
        new('G', 60, 40, 8, 48, false),
        new('H', 21, 13, 6, 21, false),
    ];

    // The only matrices that differ from 8 dots/mm (Font Matrices page): the OCR fonts E and H. Zebra lists the gap
    // for 8 dots/mm only, so the gap at the other densities is worked out from the "characters per inch" column of
    // the same page: width + gap = dots per inch / characters per inch, rounded to whole dots, where dots per inch is
    // dots/mm x 25.4 (the columns are headed 152, 300 and 600 dpi, but a 12 dots/mm head has 304.8 dots per inch).
    // The guide's 300 and 600 dpi E and H entries are not consistent with each other, which suggests trusting only the
    // 203 dpi column. This table uses the other columns where they check out, entry by entry:
    // - 6 dots/mm (152.4 dpi): the whole column fits. A, B, C, F and G give their 8 dots/mm cells exactly (6, 9, 12,
    //   16, 48), so E 152.4 / 11.7 = 13.0 -> 10 + 3 and H 152.4 / 10.2 = 14.9 -> 15 = 11 + 4 are used.
    // - 12 dots/mm (304.8 dpi): A, B, C, F and G fit again at 304.8 (G: 304.8 / 6.36 = 47.9 -> 48; at 300 it would be
    //   47.2, which is why 304.8 and not 300 is right). H 304.8 / 10.20 = 29.9 -> 30 = 22 + 8 fits a 22-dot cell with
    //   a gap in proportion to its 8 dots/mm gap, so it is used. E's 23.4 gives 13 dots, narrower than its own 20-dot
    //   cell: that is evidently a misprint, so E keeps the gap scaled from 8 dots/mm (5 x 20 / 15
    //   = 6.7 -> 7).
    // - 24 dots/mm (609.6 dpi): the same E and H matrices as 12 dots/mm, so the same gaps. The 600 dpi E and H entries
    //   fit no cell of these fonts (H would be 57 dots per character) and are not used.
    // The heights and widths are the guide's own; only the gaps are derived as described above.
    private static readonly Dictionary<(int Dpmm, char Id), (int Height, int Width, int Gap)> OtherDensities = new()
    {
        [(6, 'E')] = (21, 10, 3), [(6, 'H')] = (17, 11, 4),
        [(12, 'E')] = (42, 20, 7), [(12, 'H')] = (34, 22, 8),
        [(24, 'E')] = (42, 20, 7), [(24, 'H')] = (34, 22, 8),
    };

    // Every cell at every density, worked out once: Cell runs for every text field of every label.
    private static readonly Dictionary<(int Dpmm, char Id), CellFontSpec> Cells = BuildCells();

    /// <summary>The 203 dpi table, as Zebra prints it (read-only: the array behind it is shared by every label).</summary>
    public static IReadOnlyList<CellFontSpec> At203 { get; } = Array.AsReadOnly(At8);

    /// <summary>The cell of bitmap font <paramref name="id"/> (A to H) at <paramref name="dpi"/>, or null for any other letter.</summary>
    public static CellFontSpec? Cell(char id, int dpi) =>
        Cells.TryGetValue((DotsPerMm(dpi), char.ToUpperInvariant(id)), out var spec) ? spec : null;

    private static Dictionary<(int Dpmm, char Id), CellFontSpec> BuildCells()
    {
        var cells = new Dictionary<(int Dpmm, char Id), CellFontSpec>();
        foreach (var dpmm in new[] { 6, 8, 12, 24 })
            foreach (var spec in At8)
            {
                if (!OtherDensities.TryGetValue((dpmm, spec.Id), out var m))
                {
                    cells[(dpmm, spec.Id)] = spec;
                    continue;
                }
                // Zebra gives the baseline for 8 dots/mm only; for a different matrix it is scaled with the height,
                // an estimate that keeps the baseline at the same fraction of the character height.
                cells[(dpmm, spec.Id)] = spec with
                {
                    Height = m.Height,
                    Width = m.Width,
                    Gap = m.Gap,
                    Baseline = (int)Math.Round(spec.Baseline * (double)m.Height / spec.Height, MidpointRounding.AwayFromZero),
                };
            }
        return cells;
    }

    /// <summary>
    /// True for the built-in fonts LabelScope draws as scalable: font 0 (scalable on every printer) and P to V, which the
    /// guide lists for 203 dpi and above; their matrices are larger sizes of the same upper/lower-case design.
    /// </summary>
    public static bool IsScalableBuiltIn(char id) => char.ToUpperInvariant(id) is '0' or (>= 'P' and <= 'V');
}
