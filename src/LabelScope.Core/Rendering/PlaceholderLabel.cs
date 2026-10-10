using LabelScope.Core.Fonts;
using SkiaSharp;

namespace LabelScope.Core.Rendering;

/// <summary>Makes the small picture shown when received data did not produce a label.</summary>
public static class PlaceholderLabel
{
    /// <summary>The text drawn on the picture when the data contains no label at all.</summary>
    public const string Text = "No label (^XA ... ^XZ) in this data";

    /// <summary>The text drawn when the data has a label start (^XA) but no picture could be drawn from it.</summary>
    public const string NotDrawnText = "This label could not be drawn";

    /// <summary>The history title for data without any label.</summary>
    public const string NotFoundTitle = "No label found";

    /// <summary>The history title for a label that exists but could not be drawn.</summary>
    public const string NotDrawnTitle = "Label not drawn";

    /// <summary>The text drawn when a job only stored graphics or fonts (for example a ~DG download) and has no label.</summary>
    public const string StoredText = "Stored in LabelScope's printer memory";

    /// <summary>The history title for a job that only stored or deleted objects in printer memory.</summary>
    public const string StoredTitle = "Stored in memory";

    /// <summary>Draws <paramref name="text"/> (default <see cref="Text"/>) in grey on white and returns it as a PNG, so it fits the normal history entry.</summary>
    public static RenderedLabel Create(string text = Text)
    {
        const int width = 420, height = 120;
        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            using var border = new SKPaint { Color = new SKColor(0xB0, 0xB0, 0xB0), Style = SKPaintStyle.Stroke, StrokeWidth = 2 };
            canvas.DrawRect(1, 1, width - 2, height - 2, border);

            // The bundled scalable font, so the picture never depends on Arial being installed. The typeface is
            // shared for the life of the process and must not be disposed here; the SKFont is ours.
            using var font = new SKFont(BundledFonts.Scalable, 20);
            using var ink = new SKPaint { Color = new SKColor(0x60, 0x60, 0x60), IsAntialias = true };
            using var measure = new SKPaint(font);
            var textWidth = measure.MeasureText(text);
            canvas.DrawText(text, (width - textWidth) / 2, height / 2f + 7, font, ink);
        }

        using var pixmap = bitmap.PeekPixels();
        using var data = pixmap.Encode(SKPngEncoderOptions.Default)
            ?? throw new InvalidOperationException("PNG encoding failed.");
        return new RenderedLabel(data.ToArray(), width, height, 1);
    }
}
