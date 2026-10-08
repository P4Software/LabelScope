using SkiaSharp;

namespace LabelScope.Core.Rendering;

/// <summary>Makes the small picture shown when received data did not produce a label.</summary>
public static class PlaceholderLabel
{
    /// <summary>The text drawn on the picture.</summary>
    public const string Text = "No label (^XA ... ^XZ) in this data";

    /// <summary>Draws <see cref="Text"/> in grey on white and returns it as a PNG, so it fits the normal history entry.</summary>
    public static RenderedLabel Create()
    {
        const int width = 420, height = 120;
        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            using var border = new SKPaint { Color = new SKColor(0xB0, 0xB0, 0xB0), Style = SKPaintStyle.Stroke, StrokeWidth = 2 };
            canvas.DrawRect(1, 1, width - 2, height - 2, border);

            // Arial when installed, otherwise Skia substitutes the Windows default font.
            using var typeface = SKTypeface.FromFamilyName("Arial");
            using var font = new SKFont(typeface, 20);
            using var ink = new SKPaint { Color = new SKColor(0x60, 0x60, 0x60), IsAntialias = true };
            using var measure = new SKPaint(font);
            var textWidth = measure.MeasureText(Text);
            canvas.DrawText(Text, (width - textWidth) / 2, height / 2f + 7, font, ink);
        }

        using var pixmap = bitmap.PeekPixels();
        using var data = pixmap.Encode(SKPngEncoderOptions.Default)
            ?? throw new InvalidOperationException("PNG encoding failed.");
        return new RenderedLabel(data.ToArray(), width, height, 1);
    }
}
