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

    /// <summary>A barcode command waiting for its data.</summary>
    /// <param name="Command">The ^B command, for example ^BC.</param>
    /// <param name="Args">Its parameters, split at the commas.</param>
    /// <param name="Line">ZPL line of the command.</param>
    /// <param name="Skip">True for a symbology planned for a later release: the data is neither drawn nor shown as text.</param>
    /// <param name="SkipReason">The warning given for a symbology planned for a later release, reused as the field's problem.</param>
    private sealed record BarcodeRequest(string Command, string[] Args, int Line, bool Skip, string? SkipReason = null);

    private void SetBarDefaults(ZplCommand cmd, string[] a)
    {
        // WithArgs always goes through BarDefaults.Clamped, so absurd values can never reach the drawers.
        _by = _by.WithArgs(a, out var clamped);
        if (clamped)
            _warnings.Add(new(cmd.Line, Text.Get("Painter_ByAdjusted")));
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
        var reason = Text.Get("Painter_BarcodeDeferred", cmd.Name, deferred);
        _barcode = new BarcodeRequest(cmd.Name, a, cmd.Line, Skip: true, reason);
        _warnings.Add(new(cmd.Line, reason));
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
        // Counted before any early return, so a field that is not drawn still takes its place in the Fields list.
        var record = RoomForField(req.Line);
        if (data.Length > MaxBarcodeDataLength)
        {
            var why = Text.Get("Painter_BarcodeDataTooLong", req.Command, data.Length);
            _warnings.Add(new(req.Line, why));
            if (record) RecordBarcodeNotDrawn(req, data, why);
            return;
        }
        BarcodeField field;
        try
        {
            field = BarcodeFactory.Build(req.Command, req.Args, data, _by, _fieldOrientation);
        }
        catch (BarcodeDataException ex)
        {
            var why = Text.Get("Painter_BarcodeNotDrawn", req.Command, ex.Message);
            _warnings.Add(new(req.Line, why));
            if (record) RecordBarcodeNotDrawn(req, data, why);
            return;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // An encoder bug must cost this one field, not the whole label (ZplRenderer.PaintSafely is the last resort).
            var why = Text.Get("Painter_BarcodeInternalError", req.Command);
            _warnings.Add(new(req.Line, why));
            if (record) RecordBarcodeNotDrawn(req, data, why);
            return;
        }
        if (field.Note is not null) _warnings.Add(new(req.Line, Text.Get("Common_Prefixed", req.Command, field.Note)));
        var name = BarcodeFactory.SymbologyName(req.Command);

        switch (field)
        {
            case LinearField linear:
            {
                var lay = LinearDrawer.Measure(linear.Symbol, linear.Look, linear.Narrow);
                // The interpretation line uses the bundled fixed-width font, like a printer's own fixed-width font, so it no longer
                // depends on Arial being installed.
                // The measured layout includes the interpretation line, so the recorded box covers it too.
                var detail = record ? Text.Get("Field_BarcodeLinearDetail", name, linear.Narrow, linear.Look.BarHeight) : null;
                PlaceAndDraw(req, linear.Orientation, lay.Width, lay.Height, lay.BaseY, data, name, detail, ink =>
                {
                    var (left, right) = LinearDrawer.Draw(_canvas, linear.Symbol, linear.Look, linear.Narrow, ink, BundledFonts.Mono);
                    return new SKRect(left, 0, right, lay.Height);
                });
                break;
            }
            case MatrixField matrix:
            {
                var w = matrix.Modules.Width * matrix.ModuleWidth;
                var h = matrix.Modules.Height * matrix.ModuleHeight;
                // No quiet zone is added: the symbol's own corner sits at the origin. For ^FT the origin is the
                // bottom-left corner of a 2D symbol, so the anchor is its bottom edge.
                var detail = record
                    ? Text.Get("Field_BarcodeMatrixDetail", name, matrix.ModuleWidth, matrix.ModuleHeight, matrix.Modules.Width, matrix.Modules.Height)
                    : null;
                PlaceAndDraw(req, matrix.Orientation, w, h, h, data, name, detail, ink =>
                {
                    MatrixDrawer.Draw(_canvas, matrix.Modules, matrix.ModuleWidth, matrix.ModuleHeight, ink);
                    return new SKRect(0, 0, w, h);
                });
                break;
            }
        }
    }

    /// <summary>
    /// Applies the rotation transform for a w x h field, runs <paramref name="draw"/> (which returns the local box of
    /// everything it drew), warns when that box leaves the label, and records the field (when <paramref name="detail"/>
    /// is given, that is, while there is room in the Fields list).
    /// </summary>
    private void PlaceAndDraw(BarcodeRequest req, char orientation, int w, int h, int baseY, string data, string name,
                              string? detail, Func<SKPaint, SKRect> draw)
    {
        _canvas.Save();
        try
        {
            FieldPlacement.Apply(_canvas, orientation, _x, _y, w, h, _baseline, baseY);

            // Drawn first: the drawer reports how far the symbol really reaches, interpretation line included.
            SKRect drawn;
            using (var ink = InkPaint()) drawn = draw(ink);

            // Where the whole symbol really lands (after rotation) tells whether it fits; Skia clips the rest silently.
            var box = _canvas.TotalMatrix.MapRect(drawn);
            if (box.Left < -EdgeSlack || box.Top < -EdgeSlack || box.Right > _bitmap.Width + EdgeSlack || box.Bottom > _bitmap.Height + EdgeSlack)
                _warnings.Add(new(req.Line,
                    Text.Get("Painter_BarcodeOffLabel", req.Command, w, h)));
            if (detail is not null)
                RecordField(FieldKind.Barcode, Text.Get("Field_BarcodeSummary", name), data, drawn, req.Line, detail);
        }
        finally
        {
            _canvas.Restore();
        }
    }

    /// <summary>Records a barcode that was not drawn, with the warning it caused as the field's problem.</summary>
    private void RecordBarcodeNotDrawn(BarcodeRequest req, string data, string why)
    {
        var name = BarcodeFactory.SymbologyName(req.Command);
        RecordNotDrawn(FieldKind.Barcode, Text.Get("Field_BarcodeSummary", name), data, req.Line,
            Text.Get("Field_BarcodeNotDrawnDetail", name), why);
    }
}
