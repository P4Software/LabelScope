using LabelScope.Core.Fonts;
using SkiaSharp;

namespace LabelScope.Core.Rendering;

/// <summary>Makes the small picture shown when received data did not produce a label.</summary>
public static class PlaceholderLabel
{
    // These texts are read on every use (not constants) so they follow the current language. The class member named
    // Text hides the Localization.Text class here, hence the qualified name.

    /// <summary>The text drawn on the picture when the data contains no label at all.</summary>
    public static string Text => Localization.Text.Get("Placeholder_NoLabel");

    /// <summary>The text drawn when the data has a label start (^XA) but no picture could be drawn from it.</summary>
    public static string NotDrawnText => Localization.Text.Get("Placeholder_NotDrawn");

    /// <summary>The history title for data without any label.</summary>
    public static string NotFoundTitle => Localization.Text.Get("Placeholder_NotFoundTitle");

    /// <summary>The history title for a label that exists but could not be drawn.</summary>
    public static string NotDrawnTitle => Localization.Text.Get("Placeholder_NotDrawnTitle");

    /// <summary>The text drawn when a job only stored graphics or fonts (for example a ~DG download) and has no label.</summary>
    public static string StoredText => Localization.Text.Get("Placeholder_Stored");

    /// <summary>The history title for a job that only stored or deleted objects in printer memory.</summary>
    public static string StoredTitle => Localization.Text.Get("Placeholder_StoredTitle");

    /// <summary>Draws <paramref name="text"/> (default <see cref="Text"/>) in grey on white and returns it as a PNG, so it fits the normal history entry.</summary>
    public static RenderedLabel Create(string? text = null)
    {
        text ??= Text;
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

        return new RenderedLabel(LabelPng.Encode(bitmap), width, height, 1);
    }
}
