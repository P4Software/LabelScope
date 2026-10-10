using SkiaSharp;

namespace LabelScope.Core.Graphics;

/// <summary>Turns a downloaded PNG (~DY type P) into a monochrome graphic, refusing oversized images before decoding.</summary>
internal static class PngImage
{
    /// <summary>
    /// Largest encoded PNG accepted (5 MB). A picture that fits the dot budget compresses far below this, so the cap
    /// only stops a hostile download from making LabelScope hold and parse a huge blob.
    /// </summary>
    public const long MaxFileBytes = 5L * 1024 * 1024;

    /// <summary>
    /// Decodes <paramref name="file"/>. The size comes from the image header and is checked first, so a small file that
    /// claims a huge picture (a "decompression bomb") is refused without allocating its pixels.
    /// </summary>
    /// <exception cref="GraphicDataException">The data is not a picture, is too large, or cannot be decoded.</exception>
    public static MonoImage Decode(byte[] file, ICollection<string> notes)
    {
        using var data = SKData.CreateCopy(file);
        // SKCodec.Create reads only the header; no pixel memory exists yet.
        using var codec = SKCodec.Create(data)
            ?? throw new GraphicDataException("The data is not a PNG (or another picture format) that LabelScope can read, so the image was not stored.");
        var info = codec.Info;
        if (CheckSize(info.Width, info.Height) is { } problem)
            throw new GraphicDataException($"The image was not stored because {problem}.");

        using var bitmap = new SKBitmap(new SKImageInfo(info.Width, info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        bitmap.Erase(SKColors.Transparent); // whatever a damaged file leaves undecoded stays white
        var result = codec.GetPixels(bitmap.Info, bitmap.GetPixels());
        if (result == SKCodecResult.IncompleteInput)
            notes.Add("The image data ended early; the missing part is white.");
        else if (result != SKCodecResult.Success)
            throw new GraphicDataException($"The image could not be decoded ({result}), so it was not stored.");

        return MonoImage.FromRgba(bitmap.GetPixelSpan(), info.Width, info.Height, bitmap.RowBytes, premultiplied: false);
    }

    /// <summary>Why an image of this size is refused (finishing "was not stored because ..."), or null when it is fine.</summary>
    internal static string? CheckSize(int width, int height) =>
        width < 1 || height < 1 ? "it has no size"
        : width > GraphicLimits.MaxDots || height > GraphicLimits.MaxDots
            ? $"it is {width} x {height} pixels and LabelScope draws images up to {GraphicLimits.MaxDots} dots per side"
        : (long)width * height > GraphicLimits.MaxGraphicDots
            ? $"it has {(long)width * height} pixels and LabelScope draws images up to {GraphicLimits.MaxGraphicDots}"
        : null;
}
