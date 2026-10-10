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
    /// uses its own ceiling, tied to the dot budget above.
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
        problem = bytesPerRow < 1 ? Text.Get("Graphics_Limit_RowMissing")
            : bytesPerRow > MaxBytesPerRow ? Text.Get("Graphics_Limit_TooWide", bytesPerRow, bytesPerRow * 8L, MaxDots)
            : totalBytes < 1 ? Text.Get("Graphics_Limit_TotalMissing")
            : totalBytes > MaxGraphicBytes ? Text.Get("Graphics_Limit_TooManyBytes", totalBytes, MaxGraphicBytes, MaxGraphicDots)
            : totalBytes < bytesPerRow ? Text.Get("Graphics_Limit_LessThanRow")
            : totalBytes / bytesPerRow > MaxRows ? Text.Get("Graphics_Limit_TooHigh", totalBytes / bytesPerRow, MaxRows)
            : null;
        if (problem is not null) return false;

        rows = (int)(totalBytes / bytesPerRow);
        var rest = totalBytes % bytesPerRow;
        if (rest != 0)
            note = Text.Get("Graphics_Limit_PartialRow", totalBytes, bytesPerRow, rest);
        return true;
    }
}
