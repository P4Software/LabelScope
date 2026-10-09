namespace LabelScope.Core.Rendering;

/// <summary>Arithmetic for the measuring grid the window can draw over a label picture.</summary>
public static class LabelGrid
{
    /// <summary>
    /// Number of printer dots between two grid lines. A label is measured in dots, but people measure in
    /// millimetres, so the grid is laid out in millimetres and converted with the print resolution.
    /// </summary>
    /// <param name="dpi">Print resolution in dots per inch.</param>
    /// <param name="cellMm">Distance between lines in millimetres (10 by default).</param>
    public static double CellDots(int dpi, double cellMm = 10) => dpi * cellMm / 25.4;
}
