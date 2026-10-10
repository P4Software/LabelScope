namespace LabelScope.Core.Graphics;

/// <summary>Ceilings that keep hostile or mistyped graphic sizes from costing memory or time.</summary>
internal static class GraphicLimits
{
    /// <summary>Widest and highest graphic in dots: the same edge limit as the label itself.</summary>
    public const int MaxDots = 8000;

    /// <summary>Most bytes in one row (8 dots per byte).</summary>
    public const int MaxBytesPerRow = MaxDots / 8;

    /// <summary>Most rows (dots high).</summary>
    public const int MaxRows = MaxDots;

    /// <summary>
    /// Most dots in one graphic: the same 40 million dot budget as the largest label LabelScope draws. A graphic
    /// never needs more dots than the label it lands on, and the drawing mask costs one byte per dot.
    /// </summary>
    public const long MaxGraphicDots = 40_000_000;

    /// <summary>
    /// Largest decoded graphic in bytes (1 bit per dot). The ZPL guide lists 99,999 bytes as the top of the ^GF range,
    /// but a full 4 x 6 inch background at 203 dpi is already about 124,000 bytes and printers accept it, so LabelScope
    /// uses its own ceiling (plan Decision 3).
    /// </summary>
    public const long MaxGraphicBytes = MaxGraphicDots / 8;

    /// <summary>
    /// Checks the size a command declares (total bytes and bytes per row, as ^GF, ~DG and ~DY give them) and returns
    /// the number of rows. Everything is done in <see cref="long"/>, so values near the integer limits cannot overflow.
    /// </summary>
    /// <param name="totalBytes">Declared total bytes of the decoded graphic.</param>
    /// <param name="bytesPerRow">Declared bytes per row.</param>
    /// <param name="rows">Number of whole rows (only valid when the method returns true).</param>
    /// <param name="problem">Why the graphic cannot be drawn, finishing the sentence "The graphic was not drawn because ...".</param>
    /// <param name="note">A harmless oddity worth a warning (a partial last row), or null.</param>
    public static bool TryRows(long totalBytes, long bytesPerRow, out int rows, out string? problem, out string? note)
    {
        rows = 0;
        note = null;
        problem = bytesPerRow < 1 ? "the bytes-per-row value is missing or 0"
            : bytesPerRow > MaxBytesPerRow ? $"it is {bytesPerRow} bytes ({bytesPerRow * 8L} dots) wide and LabelScope draws graphics up to {MaxDots} dots wide"
            : totalBytes < 1 ? "the total byte count is missing or 0"
            : totalBytes > MaxGraphicBytes ? $"it is {totalBytes} bytes and LabelScope draws graphics up to {MaxGraphicBytes} bytes ({MaxGraphicDots} dots)"
            : totalBytes < bytesPerRow ? "the total byte count is smaller than one row"
            : totalBytes / bytesPerRow > MaxRows ? $"it is {totalBytes / bytesPerRow} dots high and LabelScope draws graphics up to {MaxRows} dots high"
            : null;
        if (problem is not null) return false;

        rows = (int)(totalBytes / bytesPerRow);
        var rest = totalBytes % bytesPerRow;
        if (rest != 0)
            note = $"The total byte count ({totalBytes}) is not a whole number of rows of {bytesPerRow} bytes; the last {rest} bytes were left out.";
        return true;
    }
}
