using LabelScope.Core.Memory;
using System.Globalization;
using LabelScope.Core.Barcodes;
using SkiaSharp;

namespace LabelScope.Core.Rendering;

/// <summary>Draws the commands of a single <c>^XA…^XZ</c> block onto a bitmap.</summary>
internal sealed partial class LabelPainter : IDisposable
{
    /// <summary>Largest label edge we will allocate; protects against typos such as ^PW99999.</summary>
    private const int MaxDots = 8000;

    /// <summary>
    /// Cap on width x height. The per-edge limit alone still allows 8000 x 8000 (256 MB of pixels), and several
    /// labels may render at once on socket threads. 40 megapixels (160 MB) is far above any real label
    /// (4 x 6 inch at 600 dpi is under 9 megapixels).
    /// </summary>
    private const long MaxPixels = 40_000_000;

    // Limits for hostile or mistyped numbers. Without them a value such as ^GB999999999,999999999 or
    // ^A0N,99999 would make Skia work on absurd geometry, and ^FO2147483647 would overflow int maths.
    private const int MaxCoordinate = 100_000;
    private const int MaxFontDots = 2000;
    private const int MaxFieldBlockLines = 1000;
    private const int MaxCopies = 99_999_999; // the ZPL limit for ^PQ

    private readonly List<RenderWarning> _warnings;
    private readonly PaintContext _context;
    private readonly SKBitmap _bitmap;
    private readonly SKCanvas _canvas;

    // One typeface per painter instead of one per text field; created on first use and disposed with the painter.
    private SKTypeface? _typeface;

    // Printer state that persists between fields.
    private int _homeX, _homeY;
    private char _defaultFont = 'A';
    private int _defaultHeight = 9, _defaultWidth = 0;
    private int _copies = 1;

    // State of the field being built; reset by ^FS.
    private int _x, _y;
    private bool _baseline;       // true for ^FT (y is the text baseline), false for ^FO (y is the top)
    private bool _reverse;        // ^FR
    private bool _reverseAll;     // ^LRY: every field is reversed until ^LRN
    private string _data = "";
    private bool _hasData;
    private int? _fontHeight, _fontWidth;
    private FieldBlock? _fieldBlock;
    // Orientation named by ^A for the field being built (null = use the ^FW default). Reset by ^FS.
    private char? _textOrientation;

    private sealed record FieldBlock(int Width, int MaxLines, int LineSpacing, char Justify, int Line);

    private LabelPainter(int width, int height, List<RenderWarning> warnings, bool inverted, PaintContext context)
    {
        _warnings = warnings;
        _context = context;
        _bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        _canvas = new SKCanvas(_bitmap);
        _canvas.Clear(SKColors.White);
        if (inverted)
        {
            // ^PO I prints the label upside down: every point (x, y) goes to (W - x, H - y). Doing it as one
            // canvas transform means text, boxes and barcodes all follow without knowing about it.
            _canvas.Translate(width, height);
            _canvas.RotateDegrees(180);
        }
    }

    /// <summary>Paints one label block and returns its image.</summary>
    public static RenderedLabel Paint(IReadOnlyList<ZplCommand> block, RenderOptions options, List<RenderWarning> warnings, PaintContext context)
    {
        var (w, h, widthFromZpl, heightFromZpl) = ResolveSize(block, options, warnings);
        // Only the last ^PO of a label counts, wherever it appears.
        var inverted = block.LastOrDefault(c => c.Name == "^PO") is { } po &&
                       po.Args.TrimStart().StartsWith("I", StringComparison.OrdinalIgnoreCase);
        using var painter = new LabelPainter(w, h, warnings, inverted, context);
        foreach (var cmd in block) painter.Handle(cmd);
        return painter.ToResult(w, h, widthFromZpl, heightFromZpl, options.Dpi);
    }

    /// <summary>
    /// ^PW and ^LL usually come after ^XA, so the bitmap size must be known before drawing starts.
    /// </summary>
    private static (int Width, int Height, bool WidthFromZpl, bool HeightFromZpl) ResolveSize(IReadOnlyList<ZplCommand> block, RenderOptions o, List<RenderWarning> warnings)
    {
        var width = (int)Math.Round(o.LabelWidthMm * o.Dpi / 25.4);
        var height = (int)Math.Round(o.LabelHeightMm * o.Dpi / 25.4);
        // Remembered so the window can tell the user whether the sender declared the size or settings.json supplied it.
        var widthFromZpl = false;
        var heightFromZpl = false;

        foreach (var cmd in block)
        {
            if (cmd.Name == "^PW" && TryInt(Split(cmd.Args), 0, out var pw)) { width = Clamp(pw, cmd, warnings); widthFromZpl = true; }
            else if (cmd.Name == "^LL" && TryInt(Split(cmd.Args), 0, out var ll)) { height = Clamp(ll, cmd, warnings); heightFromZpl = true; }
        }
        // A size that still comes from settings.json is clamped here; say which setting to fix.
        width = Clamp(width, null, warnings, "DefaultLabelWidthMm");
        height = Clamp(height, null, warnings, "DefaultLabelHeightMm");

        // Enforced before any allocation. The height is reduced because the width usually matches the print head.
        if ((long)width * height > MaxPixels)
        {
            var cutHeight = (int)Math.Max(1, MaxPixels / width);
            var source = block.LastOrDefault(c => c.Name is "^PW" or "^LL");
            warnings.Add(new(source?.Line ?? 1,
                $"The label size is too large to draw ({width} x {height} dots); it was cut to {width} x {cutHeight} dots. Check ^PW and ^LL."));
            height = cutHeight;
        }
        return (width, height, widthFromZpl, heightFromZpl);
    }

    private static int Clamp(int dots, ZplCommand? source, List<RenderWarning> warnings, string? setting = null)
    {
        var clamped = Math.Clamp(dots, 1, MaxDots);
        if (clamped != dots && source is not null)
            warnings.Add(new(source.Line, $"{source.Name}{dots} is outside the supported label size (1 to {MaxDots} dots); {clamped} was used instead."));
        else if (clamped != dots && setting is not null)
            warnings.Add(new(1, $"The label size from {setting} in settings.json ({dots} dots) is outside the supported range (1 to {MaxDots} dots); {clamped} dots were used instead. Change {setting} in settings.json."));
        return clamped;
    }

    private void Handle(ZplCommand cmd)
    {
        // Commands that carry bulk data are handled before the generic comma split: their data may be megabytes
        // of compressed hex in which ',' is a fill character, not a separator.
        if (cmd.Name == "^GF") { DrawGraphicField(cmd); return; }

        // Downloads and deletions act on printer memory wherever they appear, inside a label as well as outside.
        if (StorageCommands.TryHandle(cmd, _context, _warnings)) return;

        var a = Split(cmd.Args);
        switch (cmd.Name)
        {
            case "^PW": case "^LL": break; // already applied in ResolveSize
            case "^LH": _homeX = Coord(a, 0); _homeY = Coord(a, 1); break;
            case "^FO": _x = Offset(_homeX, Coord(a, 0)); _y = Offset(_homeY, Coord(a, 1)); _baseline = false; break;
            case "^FT": _x = Offset(_homeX, Coord(a, 0)); _y = Offset(_homeY, Coord(a, 1)); _baseline = true; break;
            case "^FR": _reverse = true; break;
            case "^LR": _reverseAll = a.Length > 0 && a[0].Trim().StartsWith("Y", StringComparison.OrdinalIgnoreCase); break;
            case "^PQ": _copies = Math.Clamp(Int(a, 0, 1), 1, MaxCopies); break;
            case "^CF": SetDefaultFont(cmd, a); break;
            case "^A": SetFieldFont(cmd, a); break;
            case "^FB": _fieldBlock = new FieldBlock(Math.Clamp(Int(a, 0, 0), 0, MaxDots), Math.Clamp(Int(a, 1, 1), 1, MaxFieldBlockLines), Math.Clamp(Int(a, 2, 0), -MaxDots, MaxDots), Justify(a), cmd.Line); break;
            case "^FD": _data = cmd.Args; _hasData = true; break;
            case "^FS": EndField(); break;
            case "^GB": DrawBox(cmd, a); break;
            case "^GC": DrawCircle(a); break;
            case "^GD": DrawDiagonal(a); break;
            case "^PO": break; // already applied in Paint
            case "^BY": SetBarDefaults(cmd, a); break;
            case "^FW": _fieldOrientation = a.Length > 0 && a[0].Length > 0 ? FieldPlacement.Normalize(a[0][0]) : 'N'; break;
            case "^FH": _hexIndicator = a.Length > 0 && a[0].Length > 0 ? a[0][0] : '_'; break;
            default:
                if (TryStartBarcode(cmd, a)) break;
                if (!SilentCommands.IsSilent(cmd.Name, cmd.Args))
                    _warnings.Add(new(cmd.Line, $"{cmd.Name} is not supported yet and was ignored."));
                break;
        }
    }

    // ---- fonts -------------------------------------------------------------------------------

    private void SetDefaultFont(ZplCommand cmd, string[] a)
    {
        if (a.Length > 0 && a[0].Length > 0) _defaultFont = a[0][0];
        if (TryInt(a, 1, out var h) && h > 0) _defaultHeight = Math.Min(h, MaxFontDots);
        if (TryInt(a, 2, out var w) && w > 0) _defaultWidth = Math.Min(w, MaxFontDots);
    }

    private void SetFieldFont(ZplCommand cmd, string[] a)
    {
        var first = a.Length > 0 ? a[0] : "";
        // ^A0R: first character is the font, the second the orientation (absent = follow ^FW).
        _textOrientation = first.Length > 1 ? FieldPlacement.Normalize(first[1]) : null;
        if (TryInt(a, 1, out var h) && h > 0) _fontHeight = Math.Min(h, MaxFontDots);
        if (TryInt(a, 2, out var w) && w > 0) _fontWidth = Math.Min(w, MaxFontDots);
    }

    /// <summary>
    /// Approximation until the bundled-font release: Arial scaled so that its size equals the ZPL font height.
    /// </summary>
    private SKFont MakeFont()
    {
        var height = _fontHeight ?? _defaultHeight;
        var width = _fontWidth ?? _defaultWidth;
        // SKFont does not take ownership of the typeface, so the painter keeps and disposes it.
        _typeface ??= SKTypeface.FromFamilyName("Arial");
        var font = new SKFont(_typeface, height);
        // ZPL gives width in dots per character; Arial's average character is about 0.6 of its height.
        if (width > 0) font.ScaleX = Math.Clamp(width / (0.6f * height), 0.3f, 3f);
        return font;
    }

    // ---- fields ------------------------------------------------------------------------------

    private SKPaint InkPaint(bool white = false)
    {
        // ^LRY is the same as ^FR on every field, so both switches lead to the same paint.
        var reverse = _reverse || _reverseAll;
        return new SKPaint
        {
            IsAntialias = true,
            Color = reverse || white ? SKColors.White : SKColors.Black,
            // White drawn with Difference inverts whatever is underneath: this is what ^FR means.
            BlendMode = reverse ? SKBlendMode.Difference : SKBlendMode.SrcOver,
        };
    }

    private void EndField()
    {
        // ^FH escapes are resolved once, here, so text and barcodes see the same characters.
        var data = FieldData.Decode(_data, _hexIndicator);
        if (_barcode is not null)
        {
            if (_hasData && !_barcode.Skip) DrawBarcode(data);
        }
        else if (_hasData && data.Length > 0)
        {
            _data = data;
            DrawText();
        }
        _data = ""; _hasData = false; _reverse = false;
        _fontHeight = null; _fontWidth = null; _fieldBlock = null; _textOrientation = null;
        _barcode = null; _hexIndicator = null; // both apply to one field only
    }

    private void DrawText()
    {
        if (_fieldBlock is { Width: 0 } zero)
        {
            // Zebra: a block width of 0 is "unset" and nothing prints. Drawing one long line instead would
            // show something a printer never prints.
            _warnings.Add(new(zero.Line, "^FB has a width of 0, which prints nothing on a Zebra printer, so this text was not drawn. Give ^FB a width in dots (for example ^FB400,3)."));
            return;
        }

        using var font = MakeFont();
        using var paint = InkPaint();
        var lineHeight = font.Size;
        var ascent = -font.Metrics.Ascent;
        var descent = font.Metrics.Descent;

        // Lay the lines out in an upright local box first: (text, x offset inside the box).
        var lines = new List<(string Text, float X)>();
        float boxWidth, lineStep = 0;
        if (_fieldBlock is null)
        {
            lines.Add((_data, 0));
            boxWidth = Measure(font, _data);
        }
        else
        {
            var b = _fieldBlock;
            lineStep = lineHeight + b.LineSpacing;
            foreach (var text in Wrap(_data, font, b.Width, b.MaxLines))
            {
                var textWidth = Measure(font, text);
                lines.Add((text, b.Justify switch
                {
                    'C' => (b.Width - textWidth) / 2,
                    'R' => b.Width - textWidth,
                    _ => 0,
                }));
            }
            boxWidth = b.Width;
        }

        // First baseline sits one ascent below the top of the box; ^FT anchors exactly there.
        var baseline = (int)Math.Round(ascent);
        var boxHeight = (int)Math.Ceiling(baseline + (lines.Count - 1) * lineStep + descent);

        _canvas.Save();
        try
        {
            FieldPlacement.Apply(_canvas, _textOrientation ?? _fieldOrientation, _x, _y,
                (int)Math.Ceiling(boxWidth), boxHeight, _baseline, baseline);
            for (var i = 0; i < lines.Count; i++)
                _canvas.DrawText(lines[i].Text, lines[i].X, baseline + i * lineStep, font, paint);
        }
        finally
        {
            _canvas.Restore();
        }
    }

    /// <summary>Greedy word wrap; "\&amp;" in ZPL field data forces a line break.</summary>
    private static List<string> Wrap(string text, SKFont font, int width, int maxLines)
    {
        var lines = new List<string>();
        foreach (var paragraph in text.Split("\\&"))
        {
            var current = "";
            foreach (var word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = current.Length == 0 ? word : current + " " + word;
                if (current.Length > 0 && Measure(font, candidate) > width)
                {
                    lines.Add(current);
                    current = word;
                }
                else current = candidate;
            }
            lines.Add(current);
        }
        return lines.Take(maxLines).ToList();
    }

    /// <summary>
    /// Text width in dots. SkiaSharp 2.88 has no string overload on SKFont.MeasureText, so a short-lived
    /// SKPaint built from the font does the measuring (it carries the size, typeface and horizontal scale).
    /// </summary>
    private static float Measure(SKFont font, string text)
    {
        using var paint = new SKPaint(font);
        return paint.MeasureText(text);
    }

    private static char Justify(string[] a) =>
        a.Length > 3 && a[3].Length > 0 ? char.ToUpperInvariant(a[3][0]) : 'L';

    // ---- graphics ----------------------------------------------------------------------------

    private void DrawBox(ZplCommand cmd, string[] a)
    {
        // Clamped to the largest label edge: anything bigger is off the label anyway, and Skia clips it.
        var thickness = Math.Clamp(Int(a, 2, 1), 1, MaxDots);
        var width = Math.Clamp(Int(a, 0, thickness), thickness, MaxDots);
        var height = Math.Clamp(Int(a, 1, thickness), thickness, MaxDots);
        var white = a.Length > 3 && a[3].Length > 0 && char.ToUpperInvariant(a[3][0]) == 'W';
        if (Int(a, 4, 0) > 0)
            _warnings.Add(new(cmd.Line, "Rounded corners on ^GB are not supported yet; square corners are drawn."));

        // For graphics ^FT names the bottom-left corner (for text it is the baseline), so the box grows upwards.
        var y = _baseline ? _y - height : _y;

        using var paint = InkPaint(white);
        paint.IsAntialias = false; // boxes are made of whole dots on a real printer
        if (thickness * 2 >= Math.Min(width, height))
        {
            _canvas.DrawRect(_x, y, width, height, paint);
            return;
        }
        // Outline: four bars. Drawing them as rectangles keeps edges crisp.
        _canvas.DrawRect(_x, y, width, thickness, paint);
        _canvas.DrawRect(_x, y + height - thickness, width, thickness, paint);
        _canvas.DrawRect(_x, y + thickness, thickness, height - 2 * thickness, paint);
        _canvas.DrawRect(_x + width - thickness, y + thickness, thickness, height - 2 * thickness, paint);
    }

    /// <summary>
    /// ^GD: a line across the corners of a w x h box. Drawn as a parallelogram whose horizontal width is the
    /// thickness (the extract does not say how thickness is measured; this matches how thin lines look on paper).
    /// </summary>
    private void DrawDiagonal(string[] a)
    {
        var t = Math.Clamp(Int(a, 2, 1), 1, MaxDots);
        var w = Math.Clamp(Int(a, 0, t), 3, MaxDots);
        var h = Math.Clamp(Int(a, 1, t), 3, MaxDots);
        var white = a.Length > 3 && a[3].Length > 0 && char.ToUpperInvariant(a[3][0]) == 'W';
        var left = a.Length > 4 && a[4].Length > 0 && (char.ToUpperInvariant(a[4][0]) == 'L' || a[4][0] == (char)92);

        t = Math.Min(t, w);
        var top = _baseline ? _y - h : _y; // ^FT names the bottom-left corner of a graphic
        using var path = new SKPath();
        if (left)
        {
            path.MoveTo(_x, top); path.LineTo(_x + t, top);
            path.LineTo(_x + w, top + h); path.LineTo(_x + w - t, top + h);
        }
        else
        {
            path.MoveTo(_x, top + h); path.LineTo(_x + t, top + h);
            path.LineTo(_x + w, top); path.LineTo(_x + w - t, top);
        }
        path.Close();

        using var paint = InkPaint(white);
        paint.Style = SKPaintStyle.Fill;
        _canvas.DrawPath(path, paint);
    }

    private void DrawCircle(string[] a)
    {
        var diameter = Math.Clamp(Int(a, 0, 3), 3, MaxDots);
        var thickness = Math.Clamp(Int(a, 1, 1), 1, MaxDots);
        var white = a.Length > 2 && a[2].Length > 0 && char.ToUpperInvariant(a[2][0]) == 'W';

        using var paint = InkPaint(white);
        var radius = diameter / 2f;
        // ^FT names the bottom-left corner of a graphic, so the circle's box grows upwards from _y.
        var top = _baseline ? _y - diameter : _y;
        var centre = (X: _x + radius, Y: top + radius);
        if (thickness >= radius)
        {
            _canvas.DrawCircle(centre.X, centre.Y, radius, paint);
            return;
        }
        paint.Style = SKPaintStyle.Stroke;
        paint.StrokeWidth = thickness;
        _canvas.DrawCircle(centre.X, centre.Y, radius - thickness / 2f, paint);
    }

    // ---- helpers -----------------------------------------------------------------------------

    private static string[] Split(string args) => args.Split(',');

    private static bool TryInt(string[] a, int index, out int value)
    {
        value = 0;
        return index < a.Length &&
               int.TryParse(a[index].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static int Int(string[] a, int index, int fallback) => TryInt(a, index, out var v) ? v : fallback;

    /// <summary>Reads a position argument, clamped so later additions cannot overflow.</summary>
    private static int Coord(string[] a, int index) => Math.Clamp(Int(a, index, 0), -MaxCoordinate, MaxCoordinate);

    /// <summary>Adds the label home to a field position and keeps the result in the safe range.</summary>
    private static int Offset(int home, int value) => Math.Clamp(home + value, -MaxCoordinate, MaxCoordinate);

    private RenderedLabel ToResult(int width, int height, bool widthFromZpl, bool heightFromZpl, int dpi)
    {
        _canvas.Flush();
        // Encode straight from the bitmap's pixels: SKImage.FromBitmap on a mutable bitmap would copy the
        // whole pixel buffer a second time.
        using var pixmap = _bitmap.PeekPixels();
        using var data = pixmap.Encode(SKPngEncoderOptions.Default)
            ?? throw new InvalidOperationException("PNG encoding failed.");
        return new RenderedLabel(data.ToArray(), width, height, _copies, widthFromZpl, heightFromZpl, dpi);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _canvas.Dispose();
        _bitmap.Dispose();
        _typeface?.Dispose();
    }
}
