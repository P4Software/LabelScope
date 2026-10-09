using LabelScope.Core.Barcodes;
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
            _warnings.Add(new(cmd.Line, "^BY values were outside the allowed range and were adjusted: module width 1 to 10, ratio 2.0 to 3.0, height at least 1."));
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

    private void DrawBarcode(string data)
    {
        var req = _barcode!;
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

        switch (field)
        {
            case LinearField linear: DrawLinear(linear, req); break;
        }
    }

    private void DrawLinear(LinearField f, BarcodeRequest req)
    {
        var lay = LinearDrawer.Measure(f.Symbol, f.Look, f.Narrow);

        _canvas.Save();
        try
        {
            FieldPlacement.Apply(_canvas, f.Orientation, _x, _y, lay.Width, lay.Height, _baseline, lay.BaseY);

            // Where the box really lands (after rotation) tells whether it fits; Skia clips the rest silently.
            var box = _canvas.TotalMatrix.MapRect(new SKRect(0, 0, lay.Width, lay.Height));
            if (box.Left < 0 || box.Top < 0 || box.Right > _bitmap.Width || box.Bottom > _bitmap.Height)
                _warnings.Add(new(req.Line,
                    $"The barcode from {req.Command} ({lay.Width} x {lay.Height} dots) does not fit on the label; the part outside is cut off. Use a smaller module width in ^BY, shorter data, or move the field."));

            _typeface ??= SKTypeface.FromFamilyName("Arial");
            using var ink = InkPaint();
            LinearDrawer.Draw(_canvas, f.Symbol, f.Look, f.Narrow, ink, _typeface);
        }
        finally
        {
            _canvas.Restore();
        }
    }
}
