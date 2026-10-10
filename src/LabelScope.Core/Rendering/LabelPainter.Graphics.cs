using LabelScope.Core.Graphics;
using LabelScope.Core.Memory;
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
        if (!ZplArgs.TryLong(a, 2, out var total) || !ZplArgs.TryLong(a, 3, out var perRow))
        {
            Warn(cmd, "^GF needs the graphic size (total bytes and bytes per row) before the data; without it a printer ignores the command, and so does LabelScope.");
            return;
        }
        if (!GraphicLimits.TryRows(total, perRow, out var rows, out var problem, out var sizeNote))
        {
            Warn(cmd, $"^GF: The graphic was not drawn because {problem}.");
            return;
        }
        // A size with nothing after it is a different mistake from a missing size, and saying "needs the size" for it
        // would send the user looking at the wrong part of the command.
        if (a.Length < 5 || a[4].Trim().Length == 0)
        {
            Warn(cmd, "^GF has no graphic data after its size, so the graphic was not drawn. Put the graphic data after the bytes-per-row value.");
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

    /// <summary>
    /// ^XGd:o.x,mx,my (with magnification) and ^IMd:o.x (original size): a stored graphic at the field origin.
    /// A missing graphic costs this one field, never the label.
    /// </summary>
    private void RecallGraphic(ZplCommand cmd, string[] a, bool scalable)
    {
        var name = ObjectName.Parse(a.Length > 0 ? a[0] : "", "GRF", null);
        int magX = 1, magY = 1;
        if (scalable)
        {
            var clamped = false;
            var unreadable = new List<string>();
            magX = Magnification(a, 1, ref clamped, unreadable);
            magY = Magnification(a, 2, ref clamped, unreadable);
            if (unreadable.Count > 0)
            {
                var what = unreadable.Count == 1
                    ? $"The magnification \"{unreadable[0]}\" is not a whole number"
                    : $"The magnifications \"{unreadable[0]}\" and \"{unreadable[1]}\" are not whole numbers";
                Warn(cmd, $"{cmd.Name}: {what}, so {magX},{magY} was used. Give a magnification from 1 to 10.");
            }
            else if (clamped)
                Warn(cmd, $"{cmd.Name}: The magnification must be 1 to 10; {magX},{magY} was used.");
        }

        var found = _context.Memory.Find(name, out var elsewhere);
        if (found is StoredGraphic graphic)
        {
            // The magnified size is computed in long arithmetic and judged BEFORE any bitmap is made: an 8000 x 8000
            // dot graphic at x10 would otherwise ask for 6.4 billion dots.
            var w = (long)graphic.Image.Width * magX;
            var h = (long)graphic.Image.Height * magY;
            if (w > GraphicLimits.MaxDots || h > GraphicLimits.MaxDots || w * h > GraphicLimits.MaxGraphicDots)
            {
                // Both limits are named: a long thin graphic is refused for its side alone, well inside the dot total.
                Warn(cmd, $"{cmd.Name}: {name.Display} at magnification {magX} x {magY} would be {w} x {h} dots ({w * h} dots); " +
                          $"LabelScope draws graphics up to {GraphicLimits.MaxDots} dots per side and {GraphicLimits.MaxGraphicDots} dots in total, so it was not drawn. Use a smaller magnification.");
                return;
            }
            // ^FW never turns graphics (Decision 5).
            DrawImage(cmd.Name, cmd.Line, graphic.Image, magX, magY, _x, _y, 'N', _baseline);
            return;
        }
        Warn(cmd, MissingGraphic(cmd.Name, name, found, elsewhere));
    }

    /// <summary>
    /// ^XG magnification: 1 to 10 (the guide's range), 1 when omitted. A value that is there but is not a whole number
    /// also becomes 1, and is added to <paramref name="unreadable"/> so the warning can quote it.
    /// </summary>
    private static int Magnification(string[] a, int index, ref bool clamped, List<string> unreadable)
    {
        if (!ZplArgs.TryLong(a, index, out var v))
        {
            var text = index < a.Length ? a[index].Trim() : "";
            // Quoted back to the user, so a hostile megabyte of text is cut to a readable length.
            if (text.Length > 0) unreadable.Add(text.Length > 20 ? text[..20] + "..." : text);
            return 1;
        }
        if (v is >= 1 and <= 10) return (int)v;
        clamped = true;
        return v < 1 ? 1 : 10;
    }

    /// <summary>The plain-language reason why a stored graphic could not be drawn, with the fix.</summary>
    private static string MissingGraphic(string command, ObjectName name, StoredObject? found, ObjectName? elsewhere)
    {
        if (found is not null)
            return $"{command}: {name.Display} in LabelScope's printer memory is a {found.Kind}, not a graphic, so nothing was drawn for this field.";
        var where = elsewhere is { } other
            ? $" LabelScope does have {other.Display}, but this label asks for drive {name.Drive}:."
            : "";
        return $"{command}: The graphic {name.Display} is not in LabelScope's printer memory, so it was not drawn.{where} " +
               "A printer keeps graphics that were sent to it earlier (with ~DG or ~DY) until it is switched off; " +
               "send that download job to LabelScope first, then this label again.";
    }

    // ^IS asks for the finished label to be saved; remembered until the last command has been drawn.
    private (ObjectName Name, bool Print, int Line)? _imageSave;

    /// <summary>
    /// ^ILd:o.x: a stored image as the background of the whole label. The guide places it at ^FO0,0 (so ^LH
    /// applies) and it is never turned. Only black dots are drawn, so where it appears among the fields does not
    /// change the picture, except under ^FR / ^LR, where it reverses like any field. A missing image costs the
    /// background, never the label.
    /// </summary>
    private void LoadImage(ZplCommand cmd, string[] a)
    {
        var name = ObjectName.Parse(a.Length > 0 ? a[0] : "", "GRF", 'R');
        var found = _context.Memory.Find(name, out var elsewhere);
        if (found is StoredGraphic graphic)
        {
            // ^IL is the format's background, loaded before any field on a real printer, so ^FR and ^LR (which act on
            // fields) must not reverse it. Reverse is switched off for this one draw and restored right after.
            var (reverse, reverseAll) = (_reverse, _reverseAll);
            (_reverse, _reverseAll) = (false, false);
            try { DrawImage(cmd.Name, cmd.Line, graphic.Image, 1, 1, _homeX, _homeY, 'N', fromBase: false); }
            finally { (_reverse, _reverseAll) = (reverse, reverseAll); }
            return;
        }
        if (found is not null)
        {
            Warn(cmd, $"^IL: {name.Display} in LabelScope's printer memory is a {found.Kind}, not an image, so the label is drawn without a background.");
            return;
        }
        var where = elsewhere is { } other ? $" LabelScope does have {other.Display}, but this label asks for drive {name.Drive}:." : "";
        Warn(cmd, $"^IL: This label loads the image {name.Display} from the printer's memory as its background. " +
                  "That image lives in the printer's memory and was never sent to LabelScope (a label program or WMS usually sends it once, before the labels), so the label is drawn without it." + where +
                  " To see the full label, download the image first (the job that stored it, with ~DG, ~DY or ^IS), then send this label again.");
    }

    /// <summary>^ISd:o.x,p: remembers that the finished label must be saved as an image (done in <see cref="SaveImageIfAsked"/>).</summary>
    private void RememberImageSave(ZplCommand cmd, string[] a)
    {
        var name = ObjectName.Parse(a.Length > 0 ? a[0] : "", "GRF", 'R');
        _imageSave = (name, ZplArgs.Letter(a, 1, 'Y') != 'N', cmd.Line);
    }

    /// <summary>
    /// Runs after the last command of the label: ^IS saves the whole finished label, so it must see every field,
    /// including the ones after ^IS. The picture is stored in black and white as drawn (a ^PO I label is stored
    /// already turned), the way a printer stores its bitmap.
    /// </summary>
    private void SaveImageIfAsked()
    {
        if (_imageSave is not { } save) return;
        _canvas.Flush();
        MonoImage image;
        try
        {
            image = MonoImage.FromRgba(_bitmap.GetPixelSpan(), _bitmap.Width, _bitmap.Height, _bitmap.RowBytes, premultiplied: true);
        }
        catch (GraphicDataException ex)
        {
            // A label larger than the graphic ceiling cannot be stored; the label itself must still be shown.
            _warnings.Add(new(save.Line, $"^IS {save.Name.Display}: {ex.Message}"));
            return;
        }
        var cmd = new ZplCommand("^IS", "", save.Line);
        StorageCommands.StoreGraphic(cmd, _context, _warnings, save.Name, image, $"^IS {save.Name.Display}");
        if (!save.Print)
            _warnings.Add(new(save.Line, $"^IS {save.Name.Display},N: a printer stores this label as an image without printing it; LabelScope shows it anyway."));
    }

    private void Warn(ZplCommand cmd, string message) => _warnings.Add(new(cmd.Line, message));

    /// <summary>
    /// Draws the black dots of <paramref name="image"/>, each enlarged to <paramref name="magX"/> x <paramref name="magY"/>
    /// dots, as a field at (<paramref name="x"/>, <paramref name="y"/>). White dots are transparent, so a graphic never
    /// erases what is under it; the ink follows ^FR and ^LR like every other field.
    /// </summary>
    private void DrawImage(string command, int line, MonoImage image, int magX, int magY, int x, int y, char orientation, bool fromBase)
    {
        // At most 8000 dots x 10 magnification per side, far inside int.
        var w = image.Width * magX;
        var h = image.Height * magY;

        var ink = image.InkBounds();
        if (ink is not { } inkRect)
        {
            // A printer prints nothing for an all-white graphic, and neither does LabelScope; but an empty field is
            // usually a mistake in the data (inverted bits, the wrong graphic), so it is said rather than left silent.
            _warnings.Add(new(line, $"{command}: The graphic ({w} x {h} dots) is completely white, so nothing shows on the label. If a picture was expected, check the graphic data."));
            return;
        }

        _canvas.Save();
        try
        {
            // For ^FT the origin is the bottom-left corner of a graphic, as for ^GB and 2D barcodes.
            FieldPlacement.Apply(_canvas, orientation, x, y, w, h, fromBase, h);

            // Judged on the black dots only: rows are padded to whole bytes, and that white padding may hang over
            // the label edge without anything being lost.
            var inked = new SKRect(inkRect.Left * magX, inkRect.Top * magY, inkRect.Right * magX, inkRect.Bottom * magY);
            var box = _canvas.TotalMatrix.MapRect(inked);
            if (box.Left < 0 || box.Top < 0 || box.Right > _bitmap.Width || box.Bottom > _bitmap.Height)
                _warnings.Add(new(line, $"{command}: The graphic ({w} x {h} dots) does not fit on the label; the part outside is cut off."));

            // Only the dots that land on the label (and hold ink) become a drawing mask. A stored 40-million-dot
            // graphic recalled by many fields of a small label would otherwise cost a 40 MB mask per field: the
            // hostile-input sweep measured 22 seconds for 50 recalls. The same dots, at the same whole-dot scale,
            // are drawn either way, so the picture does not change.
            if (!_canvas.TotalMatrix.TryInvert(out var inverse)) return;
            var visible = inverse.MapRect(new SKRect(0, 0, _bitmap.Width, _bitmap.Height));
            var part = SKRectI.Intersect(inkRect, new SKRectI(
                Dot(Math.Floor(visible.Left / magX), image.Width), Dot(Math.Floor(visible.Top / magY), image.Height),
                Dot(Math.Ceiling(visible.Right / magX), image.Width), Dot(Math.Ceiling(visible.Bottom / magY), image.Height)));
            if (part.Width <= 0 || part.Height <= 0) return; // nothing of it lands on the label

            using var mask = image.ToMask(part);
            using var paint = InkPaint();
            paint.IsAntialias = false;
            paint.FilterQuality = SKFilterQuality.None; // enlarged dots stay sharp squares, as on a printer
            _canvas.DrawBitmap(mask, new SKRect(part.Left * magX, part.Top * magY, part.Right * magX, part.Bottom * magY), paint);
        }
        finally
        {
            _canvas.Restore();
        }
    }

    /// <summary>A dot position from the inverse-mapped label edge, kept inside 0 to <paramref name="limit"/> before the int cast.</summary>
    private static int Dot(double value, int limit) => (int)Math.Clamp(value, 0, limit);
}
