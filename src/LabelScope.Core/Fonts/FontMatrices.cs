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

    // The only matrices that differ from 8 dots/mm (Font Matrices page): the OCR fonts E and H.
    private static readonly Dictionary<(int Dpmm, char Id), (int Height, int Width)> OtherDensities = new()
    {
        [(6, 'E')] = (21, 10), [(6, 'H')] = (17, 11),
        [(12, 'E')] = (42, 20), [(12, 'H')] = (34, 22),
        [(24, 'E')] = (42, 20), [(24, 'H')] = (34, 22),
    };

    /// <summary>The 203 dpi table, as Zebra prints it.</summary>
    public static IReadOnlyList<CellFontSpec> At203 => At8;

    /// <summary>The cell of bitmap font <paramref name="id"/> (A to H) at <paramref name="dpi"/>, or null for any other letter.</summary>
    public static CellFontSpec? Cell(char id, int dpi)
    {
        var spec = At8.FirstOrDefault(s => s.Id == char.ToUpperInvariant(id));
        if (spec is null) return null;
        if (!OtherDensities.TryGetValue((DotsPerMm(dpi), spec.Id), out var m)) return spec;

        // Zebra gives the gap and baseline for 8 dots/mm only. For a different matrix they are scaled with it: an
        // estimate, compared with a real printer in the plan's Task 18.
        return spec with
        {
            Height = m.Height,
            Width = m.Width,
            Gap = Math.Max(1, (int)Math.Round(spec.Gap * (double)m.Width / spec.Width, MidpointRounding.AwayFromZero)),
            Baseline = (int)Math.Round(spec.Baseline * (double)m.Height / spec.Height, MidpointRounding.AwayFromZero),
        };
    }

    /// <summary>
    /// True for the built-in fonts LabelScope draws as scalable: font 0 (scalable on every printer) and P to V, which the
    /// guide lists for 203 dpi and above; their matrices are larger sizes of the same upper/lower-case design.
    /// </summary>
    public static bool IsScalableBuiltIn(char id) => char.ToUpperInvariant(id) is '0' or (>= 'P' and <= 'V');
}
