using LabelScope.Core.Rendering;
using SkiaSharp;
using Xunit;

namespace LabelScope.Core.Tests;

public sealed class PlaceholderLabelTests
{
    [Fact]
    public void Create_ReturnsADecodablePngWithTheStatedSize_AndSomeInk()
    {
        var label = PlaceholderLabel.Create();

        using var bitmap = SKBitmap.Decode(label.PngBytes);
        Assert.NotNull(bitmap);
        Assert.Equal(label.WidthDots, bitmap.Width);
        Assert.Equal(label.HeightDots, bitmap.Height);
        Assert.Equal(1, label.Copies);

        // The text must really be drawn: some pixel in the middle band is not white.
        var inked = false;
        for (var x = 0; x < bitmap.Width && !inked; x++)
            for (var y = bitmap.Height / 2 - 10; y < bitmap.Height / 2 + 10 && !inked; y++)
                inked = bitmap.GetPixel(x, y) != SKColors.White;
        Assert.True(inked);
    }

    [Fact]
    public void Create_WithOtherText_DrawsADifferentPicture()
    {
        // A label that could not be drawn must not carry the "no label in this data" message.
        var none = PlaceholderLabel.Create();
        var notDrawn = PlaceholderLabel.Create(PlaceholderLabel.NotDrawnText);

        Assert.NotEqual(none.PngBytes, notDrawn.PngBytes);
        Assert.NotEqual(PlaceholderLabel.Text, PlaceholderLabel.NotDrawnText);
    }
}
