using System.Globalization;
using LabelScope.Core.Barcodes;
using LabelScope.Core.Fonts;
using LabelScope.Core.Memory;
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
    // Font sizes are not capped here: the font model limits them (10 times a bitmap cell, 2000 dots for a scalable
    // font) and says so, which a silent cap here would hide.
    private const int MaxCoordinate = 100_000;
    private const int MaxFieldBlockLines = 1000;
    /// <summary>Longest text field drawn; longer ^FD text is cut, with a warning (see DrawText).</summary>
    internal const int MaxTextFieldCharacters = 65_536;
    private const int MaxCopies = 99_999_999; // the ZPL limit for ^PQ

    private readonly List<RenderWarning> _warnings;
    // Dots per inch of the simulated printer: the bitmap fonts (and the OCR fonts) have a different matrix per dpi.
    private readonly int _dpi;
    private readonly PaintContext _context;
    // Not readonly: a label whose size the ZPL does not give is drawn on a large sheet and then cut to what was drawn.
    private SKBitmap _bitmap;
    private SKCanvas _canvas;

    // Printer state that persists between fields.
    private int _homeX, _homeY;
    private char _defaultFont = 'A';
    private int _defaultHeight = 9, _defaultWidth = 0;
    private int _copies = 1;
    // Whether this label sent ^LH or ^LR, so only what it set is kept for the next job.
    private bool _homeSet, _reverseAllSet;

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

    private LabelPainter(int width, int height, int dpi, List<RenderWarning> warnings, bool inverted, PaintContext context, PrinterSetup.Values setup)
    {
        _warnings = warnings;
        // Label home and reverse-all carry over from earlier jobs, as on a printer.
        _homeX = setup.HomeX ?? 0;
        _homeY = setup.HomeY ?? 0;
        _reverseAll = setup.ReverseAll ?? false;
        _dpi = dpi;
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

    /// <summary>
    /// Paints one label block and returns its image. The size comes from ^PW and ^LL in this label, or from an earlier
    /// job (a printer keeps them). A side the ZPL never gave ends at the last thing drawn, so nothing is cut off.
    /// </summary>
    public static RenderedLabel Paint(IReadOnlyList<ZplCommand> block, RenderOptions options, List<RenderWarning> warnings, PaintContext context)
    {
        var setup = context.Memory.Setup.Current;
        var (width, height, widthFromZpl, heightFromZpl) = ResolveSize(block, setup, warnings);
        // Only the last ^PO counts, wherever it appears; without one the last job's ^PO still applies.
        var po = block.LastOrDefault(c => c.Name == "^PO");
        var inverted = po is not null ? po.Args.TrimStart().StartsWith("I", StringComparison.OrdinalIgnoreCase) : setup.Inverted ?? false;
        // Turning upside down needs the final size, so a label still to be cut to size is turned after the cut.
        var turnLater = inverted && (width is null || height is null);
        var (sheetWidth, sheetHeight) = SheetSize(width, height, options.Dpi);

        using var painter = new LabelPainter(sheetWidth, sheetHeight, options.Dpi, warnings, inverted && !turnLater, context, setup);
        foreach (var cmd in block) painter.Handle(cmd);
        painter.CutToDrawing(width is null, height is null, options.Dpi);
        if (turnLater) painter.TurnUpsideDown();
        painter.SaveImageIfAsked();

        // What this label set is kept for the jobs that follow.
        context.Memory.Setup.Apply(new PrinterSetup.Values(
            block.Any(c => c.Name == "^PW") ? width : null,
            block.Any(c => c.Name == "^LL") ? height : null,
            painter._homeSet ? painter._homeX : null,
            painter._homeSet ? painter._homeY : null,
            po is not null ? inverted : null,
            painter._reverseAllSet ? painter._reverseAll : null));
        return painter.ToResult(widthFromZpl, heightFromZpl, options.Dpi);
    }

    /// <summary>
    /// The label size from ^PW and ^LL (this label first, then what an earlier job set); null for a side never given.
    /// ^PW and ^LL usually come after ^XA, so the size must be known before drawing starts.
    /// </summary>
    private static (int? Width, int? Height, bool WidthFromZpl, bool HeightFromZpl) ResolveSize(IReadOnlyList<ZplCommand> block, PrinterSetup.Values setup, List<RenderWarning> warnings)
    {
        var width = setup.WidthDots;
        var height = setup.HeightDots;
        foreach (var cmd in block)
        {
            if (cmd.Name == "^PW" && TryInt(Split(cmd.Args), 0, out var pw)) width = Clamp(pw, cmd, warnings);
            else if (cmd.Name == "^LL" && TryInt(Split(cmd.Args), 0, out var ll)) height = Clamp(ll, cmd, warnings);
        }

        // Enforced before any allocation. The height is reduced because the width usually matches the print head.
        if (width is { } w && height is { } h && (long)w * h > MaxPixels)
        {
            var cutHeight = (int)Math.Max(1, MaxPixels / w);
            var source = block.LastOrDefault(c => c.Name is "^PW" or "^LL");
            warnings.Add(new(source?.Line ?? 1,
                $"The label size is too large to draw ({w} x {h} dots); it was cut to {w} x {cutHeight} dots. Check ^PW and ^LL."));
            height = cutHeight;
        }
        return (width, height, width is not null, height is not null);
    }

    /// <summary>
    /// The sheet drawn on. A side the ZPL gives is used as is; a side it does not give gets room for a large label
    /// (8.5 inch wide, 12 inch long) and is cut to the drawing afterwards. The pixel cap still applies.
    /// </summary>
    private static (int Width, int Height) SheetSize(int? width, int? height, int dpi)
    {
        var w = width ?? Math.Min(MaxDots, dpi * 17 / 2);
        var h = height ?? Math.Min(MaxDots, dpi * 12);
        if ((long)w * h > MaxPixels) h = (int)Math.Max(1, MaxPixels / w);
        return (w, h);
    }

    /// <summary>
    /// Cuts the sheet to the drawing on each side the ZPL did not give. The label starts at the top-left corner, so
    /// only the right and bottom edges move. A label with nothing drawn becomes one inch on such a side.
    /// </summary>
    private void CutToDrawing(bool fitWidth, bool fitHeight, int dpi)
    {
        if (!fitWidth && !fitHeight) return;
        _canvas.Flush();
        var (right, bottom) = InkExtent();
        var w = fitWidth ? (right > 0 ? right : Math.Min(dpi, _bitmap.Width)) : _bitmap.Width;
        var h = fitHeight ? (bottom > 0 ? bottom : Math.Min(dpi, _bitmap.Height)) : _bitmap.Height;
        if (w == _bitmap.Width && h == _bitmap.Height) return;
        var old = _bitmap;
        ReplaceSheet(w, h, canvas => canvas.DrawBitmap(old, 0, 0));
    }

    /// <summary>One past the right-most and bottom-most dot that is not white (0, 0 when nothing was drawn).</summary>
    private (int Right, int Bottom) InkExtent()
    {
        var pixels = _bitmap.GetPixelSpan();
        int width = _bitmap.Width, height = _bitmap.Height, stride = _bitmap.RowBytes;
        int right = 0, bottom = 0;
        for (var y = 0; y < height; y++)
        {
            var row = pixels.Slice(y * stride, width * 4);
            // Each row is searched from its right end: the first dot found is the row's right-most one.
            for (var x = width - 1; x >= 0; x--)
            {
                var i = x * 4;
                if (row[i] == 255 && row[i + 1] == 255 && row[i + 2] == 255) continue;
                bottom = y + 1;
                if (x + 1 > right) right = x + 1;
                break;
            }
        }
        return (right, bottom);
    }

    /// <summary>^PO I for a label cut to size: the finished picture is turned half a circle.</summary>
    private void TurnUpsideDown()
    {
        _canvas.Flush();
        int w = _bitmap.Width, h = _bitmap.Height;
        var old = _bitmap;
        ReplaceSheet(w, h, canvas =>
        {
            canvas.Translate(w, h);
            canvas.RotateDegrees(180);
            canvas.DrawBitmap(old, 0, 0);
        });
    }

    /// <summary>Swaps in a new white sheet of the given size, filled by <paramref name="draw"/> from the old one.</summary>
    private void ReplaceSheet(int width, int height, Action<SKCanvas> draw)
    {
        var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        draw(canvas);
        canvas.Flush();
        _canvas.Dispose();
        _bitmap.Dispose();
        (_bitmap, _canvas) = (bitmap, canvas);
    }

    private static int Clamp(int dots, ZplCommand source, List<RenderWarning> warnings)
    {
        var clamped = Math.Clamp(dots, 1, MaxDots);
        if (clamped != dots)
            warnings.Add(new(source.Line, $"{source.Name}{dots} is outside the supported label size (1 to {MaxDots} dots); {clamped} was used instead."));
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
            case "^LH": _homeX = Coord(a, 0); _homeY = Coord(a, 1); _homeSet = true; break;
            case "^FO": _x = Offset(_homeX, Coord(a, 0)); _y = Offset(_homeY, Coord(a, 1)); _baseline = false; break;
            case "^FT": _x = Offset(_homeX, Coord(a, 0)); _y = Offset(_homeY, Coord(a, 1)); _baseline = true; break;
            case "^FR": _reverse = true; break;
            case "^LR": _reverseAll = a.Length > 0 && a[0].Trim().StartsWith("Y", StringComparison.OrdinalIgnoreCase); _reverseAllSet = true; break;
            case "^PQ": _copies = Math.Clamp(Int(a, 0, 1), 1, MaxCopies); break;
            case "^CF": SetDefaultFont(cmd, a); break;
            case "^A": SetFieldFont(cmd, a); break;
            case "^FB": _fieldBlock = new FieldBlock(Math.Clamp(Int(a, 0, 0), 0, MaxDots), Math.Clamp(Int(a, 1, 1), 1, MaxFieldBlockLines), Math.Clamp(Int(a, 2, 0), -MaxDots, MaxDots), Justify(a), cmd.Line); break;
            case "^FD": _data = cmd.Args; _hasData = true; _dataLine = cmd.Line; break;
            case "^FS": EndField(); break;
            case "^GB": DrawBox(cmd, a); break;
            case "^XG": RecallGraphic(cmd, a, scalable: true); break;
            case "^IM": RecallGraphic(cmd, a, scalable: false); break;
            case "^IL": LoadImage(cmd, a); break;
            case "^IS": RememberImageSave(cmd, a); break;
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
        if (FontDots(cmd, "^CF", a, 1, "height") is { } h) _defaultHeight = h;
        if (FontDots(cmd, "^CF", a, 2, "width") is { } w) _defaultWidth = w;
    }

    private void SetFieldFont(ZplCommand cmd, string[] a)
    {
        var first = a.Length > 0 ? a[0] : "";
        // ^A0R: the first character is the font, the second the orientation (absent = follow ^FW).
        _fieldFont = first.Length > 0 ? char.ToUpperInvariant(first[0]) : null;
        _textOrientation = first.Length > 1 ? FieldPlacement.Normalize(first[1]) : null;
        // ^A@o,h,w,d:f.x names a font file; without a name the last one given in this label stays active (the guide).
        // It is kept for this label only, as a printer forgets it when the format ends, so it lives in the painter and is not reset by ^FS.
        if (_fieldFont == '@')
        {
            var file = a.Length > 3 ? a[3].Trim() : "";
            if (file.Length > 0) _lastFontFile = ObjectName.Parse(file, "TTF", 'R');
        }
        var name = "^A" + (_fieldFont?.ToString() ?? "");
        if (FontDots(cmd, name, a, 1, "height") is { } h) _fontHeight = h;
        if (FontDots(cmd, name, a, 2, "width") is { } w) _fontWidth = w;
    }

    /// <summary>
    /// A font height or width from ^A or ^CF: null when it is not given (empty or 0, which mean "follow the font"), and
    /// also null, with a warning, when something is there that is not a size (text, a negative number). Large values
    /// are kept as they are (up to int.MaxValue) for the font model to limit and explain.
    /// </summary>
    private int? FontDots(ZplCommand cmd, string command, string[] a, int index, string what)
    {
        var text = index < a.Length ? a[index].Trim() : "";
        if (text.Length == 0) return null;
        if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) || v < 0)
        {
            // Quoted back to the user, so a hostile megabyte of text is cut to a readable length.
            var shown = text.Length > 20 ? text[..20] + "..." : text;
            _warnings.Add(new(cmd.Line, $"{command}: The font {what} \"{shown}\" is not a size in dots, so it was ignored. Give the {what} as a whole number of dots."));
            return null;
        }
        return v == 0 ? null : (int)Math.Min(v, int.MaxValue);
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
        _fontHeight = null; _fontWidth = null; _fieldFont = null; _fieldBlock = null; _textOrientation = null;
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

        // A text field longer than any label can show is cut before it is checked, wrapped and drawn: every one of
        // those steps works per character, so a hostile multi-megabyte ^FD would otherwise hold a render slot for
        // minutes. 65536 characters is far more than fits on a label even in the smallest font.
        var data = _data;
        if (data.Length > MaxTextFieldCharacters)
        {
            _warnings.Add(new(_dataLine, $"This text field is {data.Length.ToString(CultureInfo.InvariantCulture)} characters long; only the first {MaxTextFieldCharacters} are drawn. Split the text into several fields of {MaxTextFieldCharacters} characters or fewer."));
            // Never cut between the two halves of a surrogate pair (an emoji, for example).
            var cut = char.IsHighSurrogate(data[MaxTextFieldCharacters - 1]) ? MaxTextFieldCharacters - 1 : MaxTextFieldCharacters;
            data = data[..cut];
        }

        // Built for this field only and disposed with it: SKFont is not thread-safe and labels render concurrently.
        using var font = MakeFont(_dataLine);
        var text = font.Prepare(data, out var missing);
        if (missing > 0)
            _warnings.Add(new(_dataLine, $"{missing} character(s) in this text are not in the font, so they print as spaces, as on a printer. Check the field data, or choose a font that has these characters."));
        using var paint = InkPaint();
        var lineHeight = font.LineHeight;

        // Lay the lines out in an upright local box first: (text, x offset inside the box).
        var lines = new List<(string Text, float X)>();
        float boxWidth, lineStep = 0;
        if (_fieldBlock is null)
        {
            lines.Add((text, 0));
            boxWidth = font.Measure(text);
        }
        else
        {
            var b = _fieldBlock;
            lineStep = lineHeight + b.LineSpacing;
            foreach (var line in Wrap(text, font, b.Width, b.MaxLines))
            {
                var lineWidth = font.Measure(line);
                // Whole dots only: a printer cannot start a line between two dots, and a half-dot offset would
                // make aliased cell glyphs land on a different dot column than the same text placed with ^FO.
                lines.Add((line, b.Justify switch
                {
                    'C' => MathF.Round((b.Width - lineWidth) / 2, MidpointRounding.AwayFromZero),
                    'R' => MathF.Round(b.Width - lineWidth, MidpointRounding.AwayFromZero),
                    _ => 0,
                }));
            }
            boxWidth = b.Width;
        }

        // First baseline sits one baseline-height below the top of the box; ^FT anchors exactly there.
        var baseline = (int)Math.Round(font.Baseline);
        var boxHeight = (int)Math.Ceiling(baseline + (lines.Count - 1) * lineStep + font.Descent);

        _canvas.Save();
        try
        {
            FieldPlacement.Apply(_canvas, _textOrientation ?? _fieldOrientation, _x, _y,
                (int)Math.Ceiling(boxWidth), boxHeight, _baseline, baseline);
            for (var i = 0; i < lines.Count; i++)
                font.Draw(_canvas, lines[i].Text, lines[i].X, baseline + i * lineStep, paint);
        }
        finally
        {
            _canvas.Restore();
        }
    }

    /// <summary>
    /// Greedy word wrap into at most <paramref name="maxLines"/> lines; "\&amp;" in ZPL field data forces a line break.
    /// Words are separated by one or more spaces and joined again with exactly one.
    /// </summary>
    /// <remarks>
    /// Each word is measured once and the line width is kept as a running total (line + one space + word), so the
    /// work grows with the length of the text. Measuring the whole line again for every word, as a simple wrap does,
    /// grows with its square: a field of a million short words took minutes. Wrapping stops as soon as
    /// <paramref name="maxLines"/> lines are full, because the rest is never drawn.
    /// </remarks>
    internal static List<string> Wrap(string text, ZplFont font, int width, int maxLines)
    {
        var lines = new List<string>();
        if (maxLines <= 0) return lines;
        var space = font.Measure(" ");
        // A running total of glyph advances can differ from measuring the joined line by a rounding error in the
        // last bit. Within a dot of the edge, where that could decide the break, the line is measured whole, as
        // before. The budget keeps hostile text (for example zero-width words that sit at the edge forever) from
        // making that exact check quadratic again.
        var exactChecks = 256;
        var current = new System.Text.StringBuilder();
        foreach (var paragraph in text.Split("\\&"))
        {
            current.Clear();
            var currentWidth = 0f;
            foreach (var word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var wordWidth = font.Measure(word);
                if (current.Length == 0)
                {
                    current.Append(word);
                    currentWidth = wordWidth;
                    continue;
                }
                var candidateWidth = currentWidth + space + wordWidth;
                if (MathF.Abs(candidateWidth - width) < 1 && exactChecks-- > 0)
                    candidateWidth = font.Measure(current + " " + word);
                if (candidateWidth > width)
                {
                    lines.Add(current.ToString());
                    if (lines.Count == maxLines) return lines;
                    current.Clear().Append(word);
                    currentWidth = wordWidth;
                }
                else
                {
                    current.Append(' ').Append(word);
                    currentWidth = candidateWidth;
                }
            }
            lines.Add(current.ToString());
            if (lines.Count == maxLines) return lines;
        }
        return lines;
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

    private RenderedLabel ToResult(bool widthFromZpl, bool heightFromZpl, int dpi)
    {
        _canvas.Flush();
        // Encode straight from the bitmap's pixels: SKImage.FromBitmap on a mutable bitmap would copy the
        // whole pixel buffer a second time.
        using var pixmap = _bitmap.PeekPixels();
        using var data = pixmap.Encode(SKPngEncoderOptions.Default)
            ?? throw new InvalidOperationException("PNG encoding failed.");
        return new RenderedLabel(data.ToArray(), _bitmap.Width, _bitmap.Height, _copies, widthFromZpl, heightFromZpl, dpi);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _canvas.Dispose();
        _bitmap.Dispose();
        // No typeface is disposed here: the bundled typefaces live for the whole process and are shared read-only.
    }
}
