using SkiaSharp;

namespace LabelScope.Core.Barcodes;

/// <summary>How a barcode field is shown: bar height and where the interpretation line goes.</summary>
/// <param name="BarHeight">Bar height in dots (the interpretation line is extra).</param>
/// <param name="ShowText">Print the human-readable line.</param>
/// <param name="TextAbove">Print the line above the bars instead of below.</param>
internal readonly record struct BarcodeLook(int BarHeight, bool ShowText, bool TextAbove);

/// <summary>Size and anchor of a drawn linear barcode, in the unrotated local coordinate system.</summary>
/// <param name="Width">Width of the bars area.</param>
/// <param name="Height">Bars plus interpretation line.</param>
/// <param name="BarsTop">y of the top of the bars.</param>
/// <param name="BaseY">y of the bottom of the bars; the <c>^FT</c> anchor.</param>
internal readonly record struct BarcodeLayout(int Width, int Height, int BarsTop, int BaseY);

/// <summary>Draws a <see cref="LinearSymbol"/> in local coordinates (top-left of the bars area is 0,0).</summary>
internal static class LinearDrawer
{
    /// <summary>Gap between bars and interpretation line, in dots.</summary>
    private const int TextGap = 4;

    /// <summary>Height of the interpretation line in dots: readable at the usual module widths.</summary>
    public static int FontDots(int narrow) => Math.Max(14, narrow * 10);

    /// <summary>How far EAN/UPC guard bars reach below the ordinary bars.</summary>
    private static int GuardDrop(int narrow) => 5 * narrow;

    private static bool HasText(LinearSymbol s, BarcodeLook look) =>
        look.ShowText && (s.Spans is { Count: > 0 } || s.Text.Length > 0);

    /// <summary>Computes the size of the drawn symbol.</summary>
    public static BarcodeLayout Measure(LinearSymbol symbol, BarcodeLook look, int narrow)
    {
        var textZone = HasText(symbol, look) ? TextGap + FontDots(narrow) : 0;
        var guardDrop = symbol.GuardBars is { Count: > 0 } ? GuardDrop(narrow) : 0;
        var barsTop = look.TextAbove ? textZone : 0;
        var below = look.TextAbove ? guardDrop : Math.Max(textZone, guardDrop);
        return new BarcodeLayout(symbol.Width, barsTop + look.BarHeight + below, barsTop, barsTop + look.BarHeight);
    }

    /// <summary>Draws bars and interpretation line with <paramref name="ink"/> (which carries the ^FR blend mode).</summary>
    /// <returns>
    /// The left and right ends of everything drawn, in local dots. The interpretation line can reach past the bars (an
    /// EAN-13 leading digit hangs to the left, a long Code 128 text is wider than its bars), and the field box must
    /// cover it; the text widths are the ones measured for drawing anyway, so this costs nothing extra.
    /// </returns>
    public static (float Left, float Right) Draw(SKCanvas canvas, LinearSymbol symbol, BarcodeLook look, int narrow, SKPaint ink, SKTypeface typeface)
    {
        var lay = Measure(symbol, look, narrow);

        // Bars are whole dots on a real printer, so no anti-aliasing: it would blur 1-dot bars.
        var wasAa = ink.IsAntialias;
        ink.IsAntialias = false;
        var x = 0;
        for (var i = 0; i < symbol.Runs.Length; i++)
        {
            var w = symbol.Runs[i];
            if (i % 2 == 0)
            {
                var drop = symbol.GuardBars?.Contains(i) == true ? GuardDrop(narrow) : 0;
                canvas.DrawRect(x, lay.BarsTop, w, look.BarHeight + drop, ink);
            }
            x += w;
        }
        ink.IsAntialias = wasAa;

        float left = 0, right = lay.Width;
        if (!HasText(symbol, look)) return (left, right);

        using var font = new SKFont(typeface, FontDots(narrow));
        var textTop = look.TextAbove ? 0 : look.BarHeight + TextGap;
        var baseline = textTop - font.Metrics.Ascent;
        var spans = symbol.Spans ?? new[] { new TextSpan(symbol.Text, 0, symbol.Width) };
        foreach (var span in spans)
        {
            var width = MeasureText(font, span.Text);
            var start = (span.X0 + span.X1) / 2f - width / 2f; // centred in its range
            canvas.DrawText(span.Text, start, baseline, font, ink);
            left = MathF.Min(left, start);
            right = MathF.Max(right, start + width);
        }
        return (left, right);
    }

    /// <summary>SkiaSharp 2.88 has no string overload on SKFont.MeasureText, so a short-lived SKPaint measures.</summary>
    private static float MeasureText(SKFont font, string text)
    {
        using var paint = new SKPaint(font);
        return paint.MeasureText(text);
    }
}
