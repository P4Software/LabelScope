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
/// Zebra's built-in font sizes (reference part 7: the guide's "Font Matrices" and "Proportional and Fixed Spacing"
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
    // the same page, which gives width + gap (dots per inch / characters per inch, rounded to whole dots):
    // - 6 dots/mm (152 dpi): E 152 / 11.7 = 13.0 -> 10 + 3; H 152 / 10.2 = 14.9 -> 15 = 11 + 4. Fonts A, B, C, F and G
    //   in that column match their 8 dots/mm cells exactly, so the column is trusted.
    // - 12 dots/mm (300 dpi): H 300 / 10.20 = 29.4 -> 29 = 22 + 7. E's 23.4 gives 13 dots, less than its own 20-dot
    //   cell, so that one entry is a misprint and E keeps the gap scaled from 8 dots/mm (5 x 20 / 15 = 6.7 -> 7).
    // - 24 dots/mm (600 dpi): the same matrices as 12 dots/mm, so the same gaps. The 600 dpi E and H entries do not
    //   fit any cell of these fonts and are not used.
    // These values are checked against a real printer in the plan's Task 18.
    private static readonly Dictionary<(int Dpmm, char Id), (int Height, int Width, int Gap)> OtherDensities = new()
    {
        [(6, 'E')] = (21, 10, 3), [(6, 'H')] = (17, 11, 4),
        [(12, 'E')] = (42, 20, 7), [(12, 'H')] = (34, 22, 7),
        [(24, 'E')] = (42, 20, 7), [(24, 'H')] = (34, 22, 7),
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
                // an estimate compared with a real printer in the plan's Task 18.
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
