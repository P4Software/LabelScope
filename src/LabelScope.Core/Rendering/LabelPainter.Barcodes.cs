using LabelScope.Core.Barcodes;
using LabelScope.Core.Fonts;
using SkiaSharp;

namespace LabelScope.Core.Rendering;

internal sealed partial class LabelPainter
{
    // ^BY values stay in effect for every following barcode of the label.
    private BarDefaults _by = new();

    // ^FW: default orientation for fields that do not name one.
    private char _fieldOrientation = 'N';

    // ^FH: escape character for the next field only (null = no hex escapes).
    private char? _hexIndicator;

    // The ^B? command waiting for its ^FD / ^FS. Skip is true for symbologies planned for a later release.
    private BarcodeRequest? _barcode;

    private sealed record BarcodeRequest(string Command, string[] Args, int Line, bool Skip);

    private void SetBarDefaults(ZplCommand cmd, string[] a)
    {
        // WithArgs always goes through BarDefaults.Clamped, so absurd values can never reach the drawers.
        _by = _by.WithArgs(a, out var clamped);
        if (clamped)
            _warnings.Add(new(cmd.Line, "^BY values were outside the allowed range and were adjusted: module width 1 to 10, ratio 2.0 to 3.0, height 1 to 32000."));
    }

    /// <summary>
    /// Remembers a barcode command for the field being built. Returns false when the command is not a barcode
    /// command LabelScope knows, so the caller can show the generic warning.
    /// </summary>
    private bool TryStartBarcode(ZplCommand cmd, string[] a)
    {
        if (BarcodeFactory.IsSupported(cmd.Name))
        {
            _barcode = new BarcodeRequest(cmd.Name, a, cmd.Line, Skip: false);
            return true;
        }

        var deferred = BarcodeFactory.DeferredName(cmd.Name);
        if (deferred is null) return false;

        // Marked Skip so the field data that follows is not drawn as ordinary text.
        _barcode = new BarcodeRequest(cmd.Name, a, cmd.Line, Skip: true);
        _warnings.Add(new(cmd.Line, $"{cmd.Name} ({deferred}) is planned for a later release of LabelScope; nothing was drawn for this field."));
        return true;
    }

    /// <summary>
    /// Longest field data an encoder is asked to handle. Above every real symbol maximum (QR 7089 digits,
    /// Data Matrix 3116, PDF417 2710), so valid data is never refused, while hostile input cannot cost
    /// gigabytes or minutes inside an encoder.
    /// </summary>
    internal const int MaxBarcodeDataLength = 8000;

    private void DrawBarcode(string data)
    {
        if (data.Length == 0) return; // a field such as ^FD^FS has nothing to draw
        var req = _barcode!;
        if (data.Length > MaxBarcodeDataLength)
        {
            _warnings.Add(new(req.Line, $"{req.Command}: The barcode data is {data.Length} characters long; no barcode holds more than about 7000. The barcode was not drawn."));
            return;
        }
        BarcodeField field;
        try
        {
            field = BarcodeFactory.Build(req.Command, req.Args, data, _by, _fieldOrientation);
        }
        catch (BarcodeDataException ex)
        {
            _warnings.Add(new(req.Line, $"{req.Command}: {ex.Message} The barcode was not drawn."));
            return;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // An encoder bug must cost this one field, not the whole label (ZplRenderer.PaintSafely is the last resort).
            _warnings.Add(new(req.Line, $"{req.Command}: This barcode could not be drawn because of an internal error; the rest of the label is shown."));
            return;
        }
        if (field.Note is not null) _warnings.Add(new(req.Line, $"{req.Command}: {field.Note}"));

        switch (field)
        {
            case LinearField linear:
            {
                var lay = LinearDrawer.Measure(linear.Symbol, linear.Look, linear.Narrow);
                // The interpretation line uses the bundled fixed-width font, like a printer's own fixed-width font, so it no longer
                // depends on Arial being installed.
                PlaceAndDraw(req, linear.Orientation, lay.Width, lay.Height, lay.BaseY, ink =>
                    LinearDrawer.Draw(_canvas, linear.Symbol, linear.Look, linear.Narrow, ink, BundledFonts.Mono));
                break;
            }
            case MatrixField matrix:
            {
                var w = matrix.Modules.Width * matrix.ModuleWidth;
                var h = matrix.Modules.Height * matrix.ModuleHeight;
                // No quiet zone is added: the symbol's own corner sits at the origin. For ^FT the origin is the
                // bottom-left corner of a 2D symbol, so the anchor is its bottom edge.
                PlaceAndDraw(req, matrix.Orientation, w, h, h, ink =>
                    MatrixDrawer.Draw(_canvas, matrix.Modules, matrix.ModuleWidth, matrix.ModuleHeight, ink));
                break;
            }
        }
    }

    /// <summary>Applies the rotation transform for a w x h field, warns when it leaves the label, and runs <paramref name="draw"/>.</summary>
    private void PlaceAndDraw(BarcodeRequest req, char orientation, int w, int h, int baseY, Action<SKPaint> draw)
    {
        _canvas.Save();
        try
        {
            FieldPlacement.Apply(_canvas, orientation, _x, _y, w, h, _baseline, baseY);

            // Where the box really lands (after rotation) tells whether it fits; Skia clips the rest silently.
            var box = _canvas.TotalMatrix.MapRect(new SKRect(0, 0, w, h));
            if (box.Left < 0 || box.Top < 0 || box.Right > _bitmap.Width || box.Bottom > _bitmap.Height)
                _warnings.Add(new(req.Line,
                    $"The barcode from {req.Command} ({w} x {h} dots) does not fit on the label; the part outside is cut off. Use a smaller module width, shorter data, or move the field."));

            using var ink = InkPaint();
            draw(ink);
        }
        finally
        {
            _canvas.Restore();
        }
    }
}
