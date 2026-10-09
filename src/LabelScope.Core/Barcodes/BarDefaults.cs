namespace LabelScope.Core.Barcodes;

/// <summary>The values set by <c>^BY</c>: module width, wide-to-narrow ratio and default bar height.</summary>
/// <param name="ModuleWidth">Width of the narrowest bar in dots (1 to 10).</param>
/// <param name="Ratio">Wide-to-narrow ratio (2.0 to 3.0); only symbologies with two bar widths use it.</param>
/// <param name="Height">Bar height in dots used when a barcode command gives none.</param>
internal sealed record BarDefaults(int ModuleWidth = 2, double Ratio = 3.0, int Height = 10)
{
    /// <summary>Largest module width Zebra accepts.</summary>
    public const int MaxModuleWidth = 10;

    /// <summary>Largest bar height we draw; anything above is off every real label.</summary>
    public const int MaxHeight = 32000;

    /// <summary>Width of a narrow bar or space in dots.</summary>
    public int Narrow => ModuleWidth;

    /// <summary>
    /// Width of a wide bar or space: w x r rounded to whole dots (printers cannot print fractions), and always
    /// at least one dot wider than the narrow one so the two widths stay distinguishable.
    /// </summary>
    public int Wide => Math.Max(Narrow + 1, (int)Math.Round(ModuleWidth * Ratio, MidpointRounding.AwayFromZero));

    /// <summary>Builds a clamped copy; out-of-range input never throws, it is pulled into range.</summary>
    public static BarDefaults Clamped(int width, double ratio, int height) =>
        new(Math.Clamp(width, 1, MaxModuleWidth),
            double.IsNaN(ratio) ? 3.0 : Math.Clamp(ratio, 2.0, 3.0),
            Math.Clamp(height, 1, MaxHeight));
}
