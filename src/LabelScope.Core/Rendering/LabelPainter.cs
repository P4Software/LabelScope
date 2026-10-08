// src/LabelScope.Core/Rendering/LabelPainter.cs
using System.Globalization;
using SkiaSharp;

namespace LabelScope.Core.Rendering;

/// <summary>Draws the commands of a single <c>^XA…^XZ</c> block onto a bitmap.</summary>
internal sealed class LabelPainter : IDisposable
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

    // Commands that change printer behaviour but not the picture; warning about them would be noise.
    private static readonly HashSet<string> NoVisualEffect = new() { "^MN", "^MM", "^MD", "^MT", "^PR", "^JU", "^PM" };

    private readonly List<RenderWarning> _warnings;
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
    private string _data = "";
    private bool _hasData;
    private int? _fontHeight, _fontWidth;
    private FieldBlock? _fieldBlock;

    private sealed record FieldBlock(int Width, int MaxLines, int LineSpacing, char Justify);

    private LabelPainter(int width, int height, List<RenderWarning> warnings)
    {
        _warnings = warnings;
        _bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        _canvas = new SKCanvas(_bitmap);
        _canvas.Clear(SKColors.White);
    }

    /// <summary>Paints one label block and returns its image.</summary>
    public static RenderedLabel Paint(IReadOnlyList<ZplCommand> block, RenderOptions options, List<RenderWarning> warnings)
    {
        var (w, h) = ResolveSize(block, options, warnings);
        using var painter = new LabelPainter(w, h, warnings);
        foreach (var cmd in block) painter.Handle(cmd);
        return painter.ToResult(w, h);
    }

    /// <summary>
    /// ^PW and ^LL usually come after ^XA, so the bitmap size must be known before drawing starts.
    /// </summary>
    private static (int Width, int Height) ResolveSize(IReadOnlyList<ZplCommand> block, RenderOptions o, List<RenderWarning> warnings)
    {
        var width = (int)Math.Round(o.LabelWidthMm * o.Dpi / 25.4);
        var height = (int)Math.Round(o.LabelHeightMm * o.Dpi / 25.4);

        foreach (var cmd in block)
        {
            if (cmd.Name == "^PW" && TryInt(Split(cmd.Args), 0, out var pw)) width = Clamp(pw, cmd, warnings);
            else if (cmd.Name == "^LL" && TryInt(Split(cmd.Args), 0, out var ll)) height = Clamp(ll, cmd, warnings);
        }
        width = Clamp(width, null, warnings);
        height = Clamp(height, null, warnings);

        // Enforced before any allocation. The height is reduced because the width usually matches the print head.
        if ((long)width * height > MaxPixels)
        {
            var cutHeight = (int)Math.Max(1, MaxPixels / width);
            var source = block.LastOrDefault(c => c.Name is "^PW" or "^LL");
            warnings.Add(new(source?.Line ?? 1,
                $"The label size is too large to draw ({width} x {height} dots); it was cut to {width} x {cutHeight} dots. Check ^PW and ^LL."));
            height = cutHeight;
        }
        return (width, height);
    }

    private static int Clamp(int dots, ZplCommand? source, List<RenderWarning> warnings)
    {
        var clamped = Math.Clamp(dots, 1, MaxDots);
        if (clamped != dots && source is not null)
            warnings.Add(new(source.Line, $"{source.Name}{dots} is outside the supported label size (1 to {MaxDots} dots); {clamped} was used instead."));
        return clamped;
    }

    private void Handle(ZplCommand cmd)
    {
        var a = Split(cmd.Args);
        switch (cmd.Name)
        {
            case "^PW": case "^LL": break; // already applied in ResolveSize
            case "^LH": _homeX = Coord(a, 0); _homeY = Coord(a, 1); break;
            case "^FO": _x = Offset(_homeX, Coord(a, 0)); _y = Offset(_homeY, Coord(a, 1)); _baseline = false; break;
            case "^FT": _x = Offset(_homeX, Coord(a, 0)); _y = Offset(_homeY, Coord(a, 1)); _baseline = true; break;
            case "^FR": _reverse = true; break;
            case "^PQ": _copies = Math.Clamp(Int(a, 0, 1), 1, MaxCopies); break;
            case "^CF": SetDefaultFont(cmd, a); break;
            case "^A": SetFieldFont(cmd, a); break;
            case "^FB": _fieldBlock = new FieldBlock(Math.Clamp(Int(a, 0, 0), 1, MaxDots), Math.Clamp(Int(a, 1, 1), 1, MaxFieldBlockLines), Math.Clamp(Int(a, 2, 0), -MaxDots, MaxDots), Justify(a)); break;
            case "^FD": _data = cmd.Args; _hasData = true; break;
            case "^FS": EndField(); break;
            case "^GB": DrawBox(cmd, a); break;
            case "^GC": DrawCircle(a); break;
            default:
                if (!NoVisualEffect.Contains(cmd.Name))
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
        if (first.Length > 1 && char.ToUpperInvariant(first[1]) != 'N')
            _warnings.Add(new(cmd.Line, "Rotated text (^A orientation R, I or B) is not supported yet; it is drawn unrotated."));
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

    private SKPaint InkPaint(bool white = false) => new()
    {
        IsAntialias = true,
        Color = _reverse || white ? SKColors.White : SKColors.Black,
        // White drawn with Difference inverts whatever is underneath: this is what ^FR means.
        BlendMode = _reverse ? SKBlendMode.Difference : SKBlendMode.SrcOver,
    };

    private void EndField()
    {
        if (_hasData && _data.Length > 0) DrawText();
        _data = ""; _hasData = false; _reverse = false;
        _fontHeight = null; _fontWidth = null; _fieldBlock = null;
    }

    private void DrawText()
    {
        using var font = MakeFont();
        using var paint = InkPaint();
        var lineHeight = font.Size;

        if (_fieldBlock is null)
        {
            var baseline = _baseline ? _y : _y - font.Metrics.Ascent;
            _canvas.DrawText(_data, _x, baseline, font, paint);
            return;
        }

        var b = _fieldBlock;
        var lines = Wrap(_data, font, b.Width, b.MaxLines);
        for (var i = 0; i < lines.Count; i++)
        {
            var textWidth = Measure(font, lines[i]);
            var x = b.Justify switch
            {
                'C' => _x + (b.Width - textWidth) / 2,
                'R' => _x + b.Width - textWidth,
                _ => _x,
            };
            var firstBaseline = _baseline ? _y : _y - font.Metrics.Ascent;
            _canvas.DrawText(lines[i], x, firstBaseline + i * (lineHeight + b.LineSpacing), font, paint);
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

        using var paint = InkPaint(white);
        paint.IsAntialias = false; // boxes are made of whole dots on a real printer
        if (thickness * 2 >= Math.Min(width, height))
        {
            _canvas.DrawRect(_x, _y, width, height, paint);
            return;
        }
        // Outline: four bars. Drawing them as rectangles keeps edges crisp.
        _canvas.DrawRect(_x, _y, width, thickness, paint);
        _canvas.DrawRect(_x, _y + height - thickness, width, thickness, paint);
        _canvas.DrawRect(_x, _y + thickness, thickness, height - 2 * thickness, paint);
        _canvas.DrawRect(_x + width - thickness, _y + thickness, thickness, height - 2 * thickness, paint);
    }

    private void DrawCircle(string[] a)
    {
        var diameter = Math.Clamp(Int(a, 0, 3), 3, MaxDots);
        var thickness = Math.Clamp(Int(a, 1, 1), 1, MaxDots);
        var white = a.Length > 2 && a[2].Length > 0 && char.ToUpperInvariant(a[2][0]) == 'W';

        using var paint = InkPaint(white);
        var radius = diameter / 2f;
        var centre = (X: _x + radius, Y: _y + radius);
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

    private RenderedLabel ToResult(int width, int height)
    {
        _canvas.Flush();
        // Encode straight from the bitmap's pixels: SKImage.FromBitmap on a mutable bitmap would copy the
        // whole pixel buffer a second time.
        using var pixmap = _bitmap.PeekPixels();
        using var data = pixmap.Encode(SKPngEncoderOptions.Default)
            ?? throw new InvalidOperationException("PNG encoding failed.");
        return new RenderedLabel(data.ToArray(), width, height, _copies);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _canvas.Dispose();
        _bitmap.Dispose();
        _typeface?.Dispose();
    }
}
