using LabelScope.Core.Graphics;
using SkiaSharp;

namespace LabelScope.Core.Rendering;

internal sealed partial class LabelPainter
{
    /// <summary>^GFa,b,c,d,data: a bitmap placed at the field origin.</summary>
    private void DrawGraphicField(ZplCommand cmd)
    {
        var a = ZplArgs.SplitFirst(cmd.Args, 4);
        switch (ZplArgs.Letter(a, 0, 'A'))
        {
            case 'A':
                break;
            case 'B':
                Warn(cmd, "^GF: Binary graphic data (format B) is planned for a later release of LabelScope; this graphic was not drawn. Programs such as ZebraDesigner can send the same graphic as ASCII hex (format A).");
                return;
            case 'C':
                Warn(cmd, "^GF: Compressed binary graphic data (format C) uses a compression method that Zebra does not publish, so LabelScope cannot draw it. Send the graphic as ASCII hex (format A) instead.");
                return;
            case var other:
                Warn(cmd, $"^GF: '{other}' is not a graphic data format (A, B or C); the graphic was not drawn.");
                return;
        }

        // The byte count b (a[1]) only matters for binary data; c and d give the size of the bitmap.
        if (a.Length < 5 || !ZplArgs.TryLong(a, 2, out var total) || !ZplArgs.TryLong(a, 3, out var perRow))
        {
            Warn(cmd, "^GF needs the graphic size (total bytes and bytes per row) before the data; without it a printer ignores the command, and so does LabelScope.");
            return;
        }
        if (!GraphicLimits.TryRows(total, perRow, out var rows, out var problem, out var sizeNote))
        {
            Warn(cmd, $"^GF: The graphic was not drawn because {problem}.");
            return;
        }

        var notes = new List<string>();
        if (sizeNote is not null) notes.Add(sizeNote);
        MonoImage image;
        try
        {
            image = GraphicData.DecodeBitmap(a[4], (int)perRow, rows, notes);
        }
        catch (GraphicDataException ex)
        {
            Warn(cmd, $"^GF: {ex.Message} The graphic was not drawn.");
            return;
        }
        foreach (var note in notes) Warn(cmd, "^GF: " + note);

        // Graphics have no rotation parameter, so ^FW does not turn them (Decision 5).
        DrawImage(cmd.Name, cmd.Line, image, 1, 1, _x, _y, 'N', _baseline);
    }

    private void Warn(ZplCommand cmd, string message) => _warnings.Add(new(cmd.Line, message));

    /// <summary>
    /// Draws the black dots of <paramref name="image"/>, each enlarged to <paramref name="magX"/> x <paramref name="magY"/>
    /// dots, as a field at (<paramref name="x"/>, <paramref name="y"/>). White dots are transparent, so a graphic never
    /// erases what is under it; the ink follows ^FR and ^LR like every other field.
    /// </summary>
    private void DrawImage(string command, int line, MonoImage image, int magX, int magY, int x, int y, char orientation, bool fromBase)
    {
        var ink = image.InkBounds();
        if (ink is null) return; // an all-white graphic changes nothing

        // At most 8000 dots x 10 magnification per side, far inside int.
        var w = image.Width * magX;
        var h = image.Height * magY;
        _canvas.Save();
        try
        {
            // For ^FT the origin is the bottom-left corner of a graphic, as for ^GB and 2D barcodes.
            FieldPlacement.Apply(_canvas, orientation, x, y, w, h, fromBase, h);

            // Judged on the black dots only: rows are padded to whole bytes, and that white padding may hang over
            // the label edge without anything being lost.
            var inked = new SKRect(ink.Value.Left * magX, ink.Value.Top * magY, ink.Value.Right * magX, ink.Value.Bottom * magY);
            var box = _canvas.TotalMatrix.MapRect(inked);
            if (box.Left < 0 || box.Top < 0 || box.Right > _bitmap.Width || box.Bottom > _bitmap.Height)
                _warnings.Add(new(line, $"{command}: The graphic ({w} x {h} dots) does not fit on the label; the part outside is cut off."));

            using var mask = image.ToMask();
            using var paint = InkPaint();
            paint.IsAntialias = false;
            paint.FilterQuality = SKFilterQuality.None; // enlarged dots stay sharp squares, as on a printer
            _canvas.DrawBitmap(mask, new SKRect(0, 0, w, h), paint);
        }
        finally
        {
            _canvas.Restore();
        }
    }
}
