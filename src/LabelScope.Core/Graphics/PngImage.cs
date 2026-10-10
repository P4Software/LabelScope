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
        // Untrusted bytes must only ever reach the PNG decoder: SKCodec would otherwise sniff and open JPEG, GIF, BMP,
        // WebP and more, a much larger attack surface than a PNG download needs.
        if (!HasPngSignature(file))
            throw new GraphicDataException("The data is not a PNG picture.");
        using var data = SKData.CreateCopy(file);
        // SKCodec.Create reads only the header; no pixel memory exists yet.
        using var codec = SKCodec.Create(data)
            ?? throw new GraphicDataException("The data is not a PNG picture.");
        var info = codec.Info;
        if (CheckSize(info.Width, info.Height) is { } problem)
            throw new GraphicDataException($"The image was not stored because {problem}.");

        using var bitmap = new SKBitmap(new SKImageInfo(info.Width, info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        bitmap.Erase(SKColors.Transparent);
        var result = codec.GetPixels(bitmap.Info, bitmap.GetPixels());
        // A printer would not print a corrupt PNG, and a half logo must never be stored as if it were the logo, so a
        // cut-off or damaged file is refused outright instead of being kept with a white remainder.
        if (result != SKCodecResult.Success)
            throw new GraphicDataException("The PNG picture is damaged or incomplete, so it cannot be used. Send it again.");

        return MonoImage.FromRgba(bitmap.GetPixelSpan(), info.Width, info.Height, bitmap.RowBytes, premultiplied: false);
    }

    /// <summary>True when <paramref name="file"/> starts with the 8-byte PNG signature.</summary>
    private static bool HasPngSignature(byte[] file) =>
        file.AsSpan().StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

    /// <summary>Why an image of this size is refused (finishing "was not stored because ..."), or null when it is fine.</summary>
    internal static string? CheckSize(int width, int height) =>
        width < 1 || height < 1 ? "it has no size"
        : width > GraphicLimits.MaxDots || height > GraphicLimits.MaxDots
            ? $"it is {width} x {height} pixels and LabelScope draws images up to {GraphicLimits.MaxDots} dots per side"
        : (long)width * height > GraphicLimits.MaxGraphicDots
            ? $"it has {(long)width * height} pixels and LabelScope draws images up to {GraphicLimits.MaxGraphicDots}"
        : null;
}
