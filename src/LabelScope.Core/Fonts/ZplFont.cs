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

    // Characters of the prepared text that are drawn from an installed Windows font, by code point, with that font.
    // Null when there are none (almost every field), so plain text keeps the one-face path, unchanged.
    private Dictionary<int, SKTypeface>? _fallback;

    /// <summary>True when <see cref="Prepare(string, FallbackBudget, out int, out int)"/> took some characters from an installed font.</summary>
    protected bool HasFallback => _fallback is not null;

    /// <summary>True when the last prepared text draws <paramref name="rune"/> from an installed Windows font.</summary>
    public bool IsBorrowed(Rune rune) => FallbackFace(rune) is not null;

    /// <summary>The installed typeface <paramref name="rune"/> is drawn with, or null for the font's own face.</summary>
    protected SKTypeface? FallbackFace(Rune rune) =>
        _fallback is not null && _fallback.TryGetValue(rune.Value, out var face) ? face : null;

    /// <summary>
    /// The text as this font prints it, with a lookup budget of its own (see the overload taking a
    /// <see cref="FallbackBudget"/>, which the painter uses).
    /// </summary>
    public string Prepare(string text, out int missing) => Prepare(text, new FallbackBudget(), out missing, out _);

    /// <summary>
    /// The text as this font prints it. A character the font has no glyph for is taken from an installed Windows font
    /// that has it (see <see cref="FallbackFaces"/>), and <paramref name="borrowed"/> counts it, because a printer
    /// prints it only with a font that has it loaded. When no installed font has it, it becomes a space, as the guide
    /// says a printer prints unknown characters, and <paramref name="missing"/> counts it. The painter warns about both.
    /// </summary>
    /// <param name="text">The field text.</param>
    /// <param name="budget">New fallback lookups still allowed; shared by every field of a label.</param>
    /// <param name="missing">Characters printed as spaces.</param>
    /// <param name="borrowed">Characters drawn from an installed Windows font.</param>
    public virtual string Prepare(string text, FallbackBudget budget, out int missing, out int borrowed)
    {
        missing = 0;
        borrowed = 0;
        var sb = new StringBuilder(text.Length);
        // Asking the typeface costs two allocations per character; a field repeats few distinct characters, so
        // each answer is remembered for the rest of this text (a 60000-character field went from 100 ms to a few).
        var known = new Dictionary<int, Glyph>();
        Span<char> utf16 = stackalloc char[2];
        foreach (var rune in text.EnumerateRunes())
        {
            if (!known.TryGetValue(rune.Value, out var glyph))
            {
                var control = Rune.IsControl(rune);
                glyph = rune.Value == ' ' || (!control && Face.GetGlyphs(rune.ToString())[0] != 0) ? Glyph.Own : Glyph.None;
                // Only characters the font lacks are looked up, within the label's budget; an answer already known costs nothing.
                if (glyph == Glyph.None && !control && (FallbackFaces.IsKnown(rune.Value) || budget.TryTake())
                    && FallbackFaces.For(rune.Value) is { } face)
                {
                    (_fallback ??= [])[rune.Value] = face;
                    glyph = Glyph.Borrowed;
                }
                known[rune.Value] = glyph;
            }
            switch (glyph)
            {
                case Glyph.None: sb.Append(' '); missing++; break;
                case Glyph.Borrowed: sb.Append(utf16[..rune.EncodeToUtf16(utf16)]); borrowed++; break;
                default: sb.Append(utf16[..rune.EncodeToUtf16(utf16)]); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>Where a character's glyph comes from.</summary>
    private enum Glyph : byte { None, Own, Borrowed }

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
    // Fonts for characters drawn from installed faces (see ZplFont.Prepare), one per face, made when first drawn.
    private Dictionary<SKTypeface, SKFont>? _fallbackFonts;

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
        // A Zebra bitmap font prints whole dots, black or white, never grey. Anti-aliased, the thin strokes of the
        // regular-weight face fall between dots at 1x and come out light grey, so cell fonts are drawn aliased.
        _font = new SKFont(face, size) { ScaleX = spec.Width * magX / (advance * size / 100f), Edging = SKFontEdging.Alias };
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
        // Each glyph is clipped to its own cell. The letter shapes come from a scalable face sized so a capital
        // fills the cell's baseline height, so accents, rings and brackets reach above the cell; a printer's bitmap
        // font never prints outside its cell. Clipping keeps the capital height (shrinking the face to fit its
        // ascent would make every ordinary letter smaller than on the printer).
        var top = baselineY - Baseline;
        var cellWidth = _spec.Width * _magX;
        // Cells wholly outside the drawable area would be clipped away anyway; skipping them avoids a save, clip and
        // draw for each of thousands of characters that run off the label. LocalClipBounds is the clip in this
        // field's own (possibly rotated) coordinates, slightly enlarged, so nothing that could show is skipped.
        var clip = canvas.LocalClipBounds;
        if (top > clip.Bottom || top + LineHeight < clip.Top) return;
        var i = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var left = x + i++ * Advance;
            // Cells only move right (Advance is positive), so once one starts past the clip, all later ones do too.
            if (left > clip.Right) break;
            if (left + cellWidth < clip.Left) continue;
            canvas.Save();
            canvas.ClipRect(new SKRect(left, top, left + cellWidth, top + LineHeight), SKClipOperation.Intersect, antialias: false);
            var glyph = rune.ToString();
            var font = HasFallback && FallbackFace(rune) is { } face ? FallbackFont(face, glyph, cellWidth) : _font;
            canvas.DrawText(glyph, left, baselineY, font, paint);
            canvas.Restore();
        }
    }

    /// <summary>
    /// The font that draws <paramref name="glyph"/> from an installed face in one cell: the cell font's size and
    /// stretch, narrowed when the glyph would be wider than the cell (a Japanese character is about twice as wide as a
    /// Latin one). The cell, and so every width and position on the line, stays the bitmap font's own.
    /// </summary>
    private SKFont FallbackFont(SKTypeface face, string glyph, int cellWidth)
    {
        _fallbackFonts ??= [];
        if (!_fallbackFonts.TryGetValue(face, out var font))
        {
            font = new SKFont(face, _font.Size) { Edging = SKFontEdging.Alias };
            _fallbackFonts[face] = font;
        }
        font.ScaleX = 1f;
        using var measure = new SKPaint(font);
        var width = measure.MeasureText(glyph);
        font.ScaleX = width > 0 ? Math.Clamp(Math.Min(_font.ScaleX, cellWidth / width), 0.1f, 10f) : _font.ScaleX;
        return font;
    }

    /// <inheritdoc />
    public override string Prepare(string text, FallbackBudget budget, out int missing, out int borrowed) =>
        base.Prepare(_spec.UppercaseOnly ? text.ToUpperInvariant() : text, budget, out missing, out borrowed);

    /// <inheritdoc />
    public override void Dispose()
    {
        _font.Dispose();
        if (_fallbackFonts is not null)
            foreach (var font in _fallbackFonts.Values) font.Dispose();
    }

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
    // Set when the font is laid out like the printer's font 0 (see the constructor); null for a face's own layout.
    private readonly float? _capTop;
    private readonly int _height;
    // Fonts for characters drawn from installed faces (see ZplFont.Prepare), one per face, made when first needed.
    private Dictionary<SKTypeface, SKFont>? _fallbackFonts;

    /// <summary>
    /// Creates the font at <paramref name="height"/> dots; <paramref name="width"/> equal to the height keeps the face's
    /// own proportions. <paramref name="faceWidth"/> narrows or widens the face itself, so a stand-in face can match the
    /// letter widths of the printer font it replaces (1 = the face as designed).
    /// <paramref name="capHeight"/>, when given, lays the face out like the printer's font 0 instead of by its own
    /// metrics: capitals are that fraction of the height, and their tops sit on the top of the field (^FO), so text
    /// lands where the printer prints it whatever space the stand-in face leaves above its capitals.
    /// </summary>
    public ScalableFont(SKTypeface face, int height, int width, float faceWidth = 1f, float? capHeight = null)
    {
        _face = face;
        _height = height;
        var size = (float)height;
        if (capHeight is { } cap)
        {
            using var probe = new SKFont(face, 1000);
            var capPerEm = probe.Metrics.CapHeight / 1000f;
            if (capPerEm > 0)
            {
                size = height * cap / capPerEm;
                _capTop = height * cap;
            }
        }
        _font = new SKFont(face, size);
        // The width asked for is measured against the height, so the stretch is relative to the height, not the size.
        var scaleX = width / (float)height * faceWidth * (height / size);
        if (scaleX != 1f) _font.ScaleX = Math.Clamp(scaleX, 0.1f, 10f);
    }

    /// <inheritdoc />
    protected override SKTypeface Face => _face;

    /// <inheritdoc />
    public override float LineHeight => _capTop is null ? _font.Size : _height;

    /// <inheritdoc />
    public override float Baseline => _capTop ?? -_font.Metrics.Ascent;

    /// <inheritdoc />
    public override float Descent => _capTop is { } top ? _height - top : _font.Metrics.Descent;

    /// <inheritdoc />
    public override float Measure(string text)
    {
        if (!HasFallback) return MeasureWith(_font, text);
        var total = 0f;
        foreach (var (run, font) in Runs(text)) total += MeasureWith(font, run);
        return total;
    }

    /// <inheritdoc />
    public override void Draw(SKCanvas canvas, string text, float x, float baselineY, SKPaint paint)
    {
        if (!HasFallback)
        {
            canvas.DrawText(text, x, baselineY, _font, paint);
            return;
        }
        // Each run starts where the one before it ended, on the same baseline, so a word mixing faces reads as one.
        foreach (var (run, font) in Runs(text))
        {
            canvas.DrawText(run, x, baselineY, font, paint);
            x += MeasureWith(font, run);
        }
    }

    // SkiaSharp 2.88 has no string overload on SKFont.MeasureText; a paint built from the font does it.
    private static float MeasureWith(SKFont font, string text)
    {
        using var paint = new SKPaint(font);
        return paint.MeasureText(text);
    }

    /// <summary>
    /// Splits <paramref name="text"/> into runs drawn with one font each: this font for its own characters, and the same
    /// size and stretch in an installed face for characters taken from one. Measuring and drawing both use it, so ^FB
    /// wrapping and centring see the widths that are drawn.
    /// </summary>
    private IEnumerable<(string Run, SKFont Font)> Runs(string text)
    {
        var run = new StringBuilder();
        SKTypeface? current = null;
        foreach (var rune in text.EnumerateRunes())
        {
            var face = FallbackFace(rune);
            if (face != current && run.Length > 0)
            {
                yield return (run.ToString(), FontFor(current));
                run.Clear();
            }
            current = face;
            run.Append(rune.ToString());
        }
        if (run.Length > 0) yield return (run.ToString(), FontFor(current));
    }

    /// <summary>This font for <paramref name="face"/> null, otherwise the same size and stretch in that installed face.</summary>
    private SKFont FontFor(SKTypeface? face)
    {
        if (face is null) return _font;
        _fallbackFonts ??= [];
        if (!_fallbackFonts.TryGetValue(face, out var font))
        {
            font = new SKFont(face, _font.Size) { ScaleX = _font.ScaleX };
            _fallbackFonts[face] = font;
        }
        return font;
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        _font.Dispose();
        if (_fallbackFonts is not null)
            foreach (var font in _fallbackFonts.Values) font.Dispose();
    }
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
                note = Text.Get("Fonts_MagnifyClamped", id, spec.Height * 10, spec.Width * 10);
            return new CellFont(spec, magX, magY, BundledFonts.Mono);
        }
        return FontMatrices.IsScalableBuiltIn(id) ? Scalable(BundledFonts.Scalable, height, width, out note, BundledScalableWidth, BundledCapHeight) : null;
    }

    /// <summary>
    /// Noto Sans ExtraCondensed Bold, laid out with <see cref="BundledCapHeight"/>, sets text about 5% narrower than
    /// the printer's font 0; this widens it to match. Checked on capitals, lowercase, digits and stretched sizes from 20
    /// to 100 dots: line lengths land within about 2% of the printer's, so ^FB wrapping and centring follow it.
    /// </summary>
    internal const float BundledScalableWidth = 1.05f;

    /// <summary>
    /// Capitals of the printer's font 0 are about this fraction of the font height, and their tops sit on the top of
    /// the field: "1000AAA" at ^FO..,38 with height 59 prints capitals 46 dots tall from y 37 to 82.
    /// </summary>
    internal const float BundledCapHeight = 0.77f;

    /// <summary>
    /// A scalable font; when only the height or only the width is given, the other follows it (the face's own
    /// proportion). <paramref name="note"/> explains a size above <see cref="MaxScalableDots"/> that had to be limited.
    /// </summary>
    public static ScalableFont Scalable(SKTypeface face, int height, int width, out string? note, float faceWidth = 1f, float? capHeight = null)
    {
        if (height <= 0) height = width > 0 ? width : 9;   // 9 dots: Zebra's ^CF default height
        if (width <= 0) width = height;
        var (h, w) = (Math.Min(height, MaxScalableDots), Math.Min(width, MaxScalableDots));
        note = (h, w) == (height, width)
            ? null
            : Text.Get("Fonts_ScalableClamped", MaxScalableDots, height, width, h, w);
        return new ScalableFont(face, h, w, faceWidth, capHeight);
    }

    /// <summary>
    /// Bitmap magnification: the nearest whole multiple of the cell (ties round up), 1 to 10, because a bitmap font
    /// can only be enlarged by whole dots, as on a printer. A size
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
