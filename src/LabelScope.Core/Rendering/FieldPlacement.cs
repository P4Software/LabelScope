using SkiaSharp;

namespace LabelScope.Core.Rendering;

/// <summary>
/// Turns a ZPL field orientation (N, R, I, B) and origin into a canvas transform. Drawing code then paints the
/// field upright in local coordinates and never has to know about rotation.
/// </summary>
internal static class FieldPlacement
{
    /// <summary>Upper-cases <paramref name="orientation"/>; anything but R, I or B means N (normal).</summary>
    public static char Normalize(char orientation)
    {
        var c = char.ToUpperInvariant(orientation);
        return c is 'R' or 'I' or 'B' ? c : 'N';
    }

    /// <summary>Clockwise rotation in degrees: N 0, R 90, I 180, B 270.</summary>
    public static int Degrees(char orientation) => Normalize(orientation) switch { 'R' => 90, 'I' => 180, 'B' => 270, _ => 0 };

    /// <summary>
    /// Sets the transform of <paramref name="canvas"/> so that local <c>(0,0)-(w,h)</c> is the unrotated field.
    /// Call <c>canvas.Save()</c> before and <c>canvas.Restore()</c> after.
    /// </summary>
    /// <param name="canvas">Canvas to transform.</param>
    /// <param name="orientation">N, R, I or B.</param>
    /// <param name="x">Field origin x on the label, in dots.</param>
    /// <param name="y">Field origin y on the label, in dots.</param>
    /// <param name="w">Unrotated field width.</param>
    /// <param name="h">Unrotated field height.</param>
    /// <param name="fromBase">
    /// True for <c>^FT</c>: the origin is the base-left point of the field (local <c>(0, anchorY)</c>) and the field
    /// rotates around it. False for <c>^FO</c>: the origin is the top-left corner of the rotated bounding box.
    /// </param>
    /// <param name="anchorY">Local y of the base point (text baseline, bottom of the bars) used when <paramref name="fromBase"/> is true.</param>
    public static void Apply(SKCanvas canvas, char orientation, int x, int y, int w, int h, bool fromBase, int anchorY)
    {
        var o = Normalize(orientation);
        if (fromBase)
        {
            canvas.Translate(x, y);
            canvas.RotateDegrees(Degrees(o));
            canvas.Translate(0, -anchorY);
            return;
        }

        // The translations make the rotated box start exactly at (x, y): after turning, the box extends to the
        // left of / above the pivot, so the pivot is moved by the box size to compensate.
        switch (o)
        {
            case 'R': canvas.Translate(x + h, y); canvas.RotateDegrees(90); break;
            case 'I': canvas.Translate(x + w, y + h); canvas.RotateDegrees(180); break;
            case 'B': canvas.Translate(x, y + w); canvas.RotateDegrees(270); break;
            default: canvas.Translate(x, y); break;
        }
    }
}
