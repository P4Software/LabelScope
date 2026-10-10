namespace LabelScope.Core.Rendering;

/// <summary>
/// Finds the field under a point of a rendered label, so a click on the picture can select the field and its ZPL line.
/// </summary>
public static class FieldHitTest
{
    /// <summary>
    /// Returns the field whose box contains the point (<paramref name="x"/>, <paramref name="y"/>) in label dots, or
    /// null when no field does. When boxes overlap, the smallest box wins: a text field drawn inside a frame (^GB) is
    /// what a person points at, not the frame around it. Equal sizes go to the field drawn last, which is the one on top.
    /// </summary>
    /// <param name="fields">The fields of one label (<see cref="RenderedLabel.Fields"/>).</param>
    /// <param name="x">Horizontal position in label dots.</param>
    /// <param name="y">Vertical position in label dots.</param>
    /// <param name="slop">
    /// Extra dots around every box that still count as a hit. A 4-dot line shown at 50 % is two screen pixels tall,
    /// too thin to click exactly; a little slop makes it reachable. 0 means exact boxes.
    /// </param>
    public static LabelField? Find(IReadOnlyList<LabelField> fields, int x, int y, int slop = 0)
    {
        ArgumentNullException.ThrowIfNull(fields);
        slop = Math.Max(0, slop);
        LabelField? best = null;
        var bestArea = long.MaxValue;
        foreach (var f in fields)
        {
            // A field that was not drawn has an empty box; there is nothing on the label to point at.
            if (f.Width <= 0 || f.Height <= 0) continue;
            // Half-open boxes: a point on the right or bottom edge belongs to the neighbour, as with pixels.
            if (x < f.X - slop || x >= f.X + f.Width + slop || y < f.Y - slop || y >= f.Y + f.Height + slop) continue;
            var area = (long)f.Width * f.Height;
            if (area <= bestArea)
            {
                best = f;
                bestArea = area;
            }
        }
        return best;
    }

    /// <summary>
    /// The first field (in drawing order) whose ZPL line is <paramref name="line"/>, or null when that line drew none;
    /// used when a person clicks a line in the ZPL tab.
    /// </summary>
    /// <param name="fields">The fields of one label.</param>
    /// <param name="line">1-based line of the ZPL text as shown.</param>
    public static LabelField? FirstOnLine(IReadOnlyList<LabelField> fields, int line)
    {
        ArgumentNullException.ThrowIfNull(fields);
        foreach (var f in fields)
            if (f.Line == line) return f;
        return null;
    }
}
