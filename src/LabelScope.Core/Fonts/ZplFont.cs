// src/LabelScope.Core/Fonts/ZplFont.cs
using System.Text;
using SkiaSharp;

namespace LabelScope.Core.Fonts;

/// <summary>A font as ZPL lays text out: line height, baseline, text width, and how to draw one line.</summary>
internal abstract class ZplFont : IDisposable
{
    /// <summary>The typeface whose glyphs are drawn (owned elsewhere; never disposed here).</summary>
    protected abstract SKTypeface Face { get; }

    /// <summary>From the top of one line to the top of the next, before ^FB adds its own spacing.</summary>
    public abstract float LineHeight { get; }

    /// <summary>From the top of a line down to its baseline (where ^FT anchors text).</summary>
    public abstract float Baseline { get; }

    /// <summary>From the baseline down to the bottom of the line.</summary>
    public abstract float Descent { get; }

    /// <summary>Width of <paramref name="text"/> in dots.</summary>
    public abstract float Measure(string text);

    /// <summary>Draws one line with its baseline at <paramref name="baselineY"/>.</summary>
    public abstract void Draw(SKCanvas canvas, string text, float x, float baselineY, SKPaint paint);

    /// <summary>
    /// The text as this font prints it. Characters the font has no glyph for become spaces, as the guide says a
    /// printer prints unknown characters; <paramref name="missing"/> counts them so the painter can say so.
    /// </summary>
    public virtual string Prepare(string text, out int missing)
    {
        missing = 0;
        var sb = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            var s = rune.ToString();
            if (rune.Value == ' ' || (!Rune.IsControl(rune) && Face.GetGlyphs(s)[0] != 0)) sb.Append(s);
            else { sb.Append(' '); missing++; }
        }
        return sb.ToString();
    }

    /// <inheritdoc />
    public abstract void Dispose();
}

/// <summary>
/// One of Zebra's bitmap fonts A to H: every character takes one fixed cell (width + gap), so line widths, wrapping
/// and centring match a printer exactly. The letter shapes are the bundled mono font, sized so a capital is as tall
/// as the cell's baseline height and stretched to the cell width.
/// </summary>
internal sealed class CellFont : ZplFont
{
    private readonly CellFontSpec _spec;
    private readonly int _magX, _magY;
    private readonly SKTypeface _face;
    private readonly SKFont _font;

    /// <summary>Creates font <paramref name="spec"/> enlarged <paramref name="magX"/> x <paramref name="magY"/> times.</summary>
    public CellFont(CellFontSpec spec, int magX, int magY, SKTypeface face)
    {
        _spec = spec;
        _magX = magX;
        _magY = magY;
        _face = face;

        // Measured once at size 100: cap height and the advance of a digit (the face is fixed-width).
        using var probe = new SKFont(face, 100);
        var capHeight = probe.Metrics.CapHeight > 0 ? probe.Metrics.CapHeight : 70f;
        using var measure = new SKPaint(probe);
        var advance = measure.MeasureText("0");

        var size = spec.Baseline * magY * 100f / capHeight;
        _font = new SKFont(face, size) { ScaleX = spec.Width * magX / (advance * size / 100f) };
    }

    /// <inheritdoc />
    protected override SKTypeface Face => _face;

    /// <summary>Distance from one character to the next: (cell width + gap) x horizontal magnification.</summary>
    public int Advance => (_spec.Width + _spec.Gap) * _magX;

    /// <inheritdoc />
    public override float LineHeight => _spec.Height * _magY;

    /// <inheritdoc />
    public override float Baseline => _spec.Baseline * _magY;

    /// <inheritdoc />
    public override float Descent => (_spec.Height - _spec.Baseline) * _magY;

    /// <inheritdoc />
    public override float Measure(string text) => CountRunes(text) * Advance;

    /// <inheritdoc />
    public override void Draw(SKCanvas canvas, string text, float x, float baselineY, SKPaint paint)
    {
        var i = 0;
        foreach (var rune in text.EnumerateRunes())
            canvas.DrawText(rune.ToString(), x + i++ * Advance, baselineY, _font, paint);
    }

    /// <inheritdoc />
    public override string Prepare(string text, out int missing) =>
        base.Prepare(_spec.UppercaseOnly ? text.ToUpperInvariant() : text, out missing);

    /// <inheritdoc />
    public override void Dispose() => _font.Dispose();

    private static int CountRunes(string text)
    {
        var n = 0;
        foreach (var _ in text.EnumerateRunes()) n++;
        return n;
    }
}

/// <summary>A scalable font: font 0 and P to V (bundled) or a TrueType font. Height is the font size; width / height stretches it.</summary>
internal sealed class ScalableFont : ZplFont
{
    private readonly SKTypeface _face;
    private readonly SKFont _font;

    /// <summary>Creates the font at <paramref name="height"/> dots; <paramref name="width"/> equal to the height keeps the face's own proportions.</summary>
    public ScalableFont(SKTypeface face, int height, int width)
    {
        _face = face;
        _font = new SKFont(face, height);
        if (width != height) _font.ScaleX = Math.Clamp(width / (float)height, 0.1f, 10f);
    }

    /// <inheritdoc />
    protected override SKTypeface Face => _face;

    /// <inheritdoc />
    public override float LineHeight => _font.Size;

    /// <inheritdoc />
    public override float Baseline => -_font.Metrics.Ascent;

    /// <inheritdoc />
    public override float Descent => _font.Metrics.Descent;

    /// <inheritdoc />
    public override float Measure(string text)
    {
        // SkiaSharp 2.88 has no string overload on SKFont.MeasureText; a paint built from the font does it.
        using var paint = new SKPaint(_font);
        return paint.MeasureText(text);
    }

    /// <inheritdoc />
    public override void Draw(SKCanvas canvas, string text, float x, float baselineY, SKPaint paint) =>
        canvas.DrawText(text, x, baselineY, _font, paint);

    /// <inheritdoc />
    public override void Dispose() => _font.Dispose();
}

/// <summary>Builds the font a ZPL font letter and size ask for.</summary>
internal static class ZplFontFactory
{
    /// <summary>Largest scalable size, the same cap the painter has always used for hostile values.</summary>
    public const int MaxScalableDots = 2000;

    /// <summary>
    /// The built-in font <paramref name="letter"/> (A to H as cell fonts; 0 and P to V scalable), or null when the
    /// printer has no such built-in font. <paramref name="note"/> explains a size that had to be limited.
    /// </summary>
    public static ZplFont? TryBuiltIn(char letter, int height, int width, int dpi, out string? note)
    {
        note = null;
        var id = char.ToUpperInvariant(letter);
        if (FontMatrices.Cell(id, dpi) is { } spec)
        {
            var (magX, magY, clamped) = Magnify(spec, height, width);
            if (clamped)
                note = $"Font {id} can be enlarged up to 10 times ({spec.Height * 10} dots high, {spec.Width * 10} dots wide); 10 times was used.";
            return new CellFont(spec, magX, magY, BundledFonts.Mono);
        }
        return FontMatrices.IsScalableBuiltIn(id) ? Scalable(BundledFonts.Scalable, height, width) : null;
    }

    /// <summary>A scalable font; when only the height or only the width is given, the other follows it (the face's own proportion).</summary>
    public static ScalableFont Scalable(SKTypeface face, int height, int width)
    {
        if (height <= 0) height = width > 0 ? width : 9;   // 9 dots: Zebra's ^CF default height
        if (width <= 0) width = height;
        return new ScalableFont(face, Math.Min(height, MaxScalableDots), Math.Min(width, MaxScalableDots));
    }

    /// <summary>
    /// Bitmap magnification (plan Decision 9): the nearest whole multiple of the cell (ties round up), 1 to 10. A size
    /// given in one direction only sets both, as the guide says the other follows the standard matrix.
    /// </summary>
    internal static (int MagX, int MagY, bool Clamped) Magnify(CellFontSpec spec, int height, int width)
    {
        int? my = height > 0 ? Multiple(height, spec.Height) : null;
        int? mx = width > 0 ? Multiple(width, spec.Width) : null;
        var y = my ?? mx ?? 1;
        var x = mx ?? my ?? 1;
        return (Math.Min(x, 10), Math.Min(y, 10), x > 10 || y > 10);
    }

    private static int Multiple(int dots, int cell) =>
        (int)Math.Max(1, Math.Round(dots / (double)cell, MidpointRounding.AwayFromZero));
}
