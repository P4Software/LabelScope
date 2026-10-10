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

    private const string Damaged = "The PNG picture is damaged or incomplete, so it cannot be used. Send it again.";

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
        // Before Skia sees it: the decoder sets aside the full-size bitmap first, so a 100-byte file claiming 6000 x 6000
        // pixels would cost 144 MB before it was found to be incomplete.
        if (ImpossiblySmall(file))
            throw new GraphicDataException(Damaged);
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
            throw new GraphicDataException(Damaged);

        return MonoImage.FromRgba(bitmap.GetPixelSpan(), info.Width, info.Height, bitmap.RowBytes, premultiplied: false);
    }

    /// <summary>
    /// Deflate's best case: no stream unpacks to more than 1032 times its own size (each 258-byte match costs at least
    /// two bits). Used to spot a header that promises more pixels than its data could ever hold.
    /// </summary>
    private const int MaxDeflateRatio = 1032;

    /// <summary>
    /// True when the picture data (all IDAT chunks together) is too short to unpack to the rows the header (IHDR)
    /// promises, even at deflate's best ratio. Such a file is damaged or a "bomb" that would make the decoder set
    /// aside a full-size bitmap before it noticed; it is refused from the header alone. Reads only chunk headers, never
    /// pixel data, and answers false whenever the file is too odd to judge (the decoder then decides).
    /// </summary>
    internal static bool ImpossiblySmall(byte[] file)
    {
        long width = 0, height = 0, bitsPerPixel = 0, compressed = 0;
        var pos = 8L; // after the signature
        while (pos + 8 <= file.Length)
        {
            var length = (long)(uint)(file[pos] << 24 | file[pos + 1] << 16 | file[pos + 2] << 8 | file[pos + 3]);
            var type = System.Text.Encoding.ASCII.GetString(file, (int)pos + 4, 4);
            var body = pos + 8;
            if (type == "IHDR" && length >= 13 && body + 13 <= file.Length)
            {
                width = (uint)(file[body] << 24 | file[body + 1] << 16 | file[body + 2] << 8 | file[body + 3]);
                height = (uint)(file[body + 4] << 24 | file[body + 5] << 16 | file[body + 6] << 8 | file[body + 7]);
                // Samples per pixel by colour type (grey, RGB, palette, grey + alpha, RGBA), times the bit depth.
                long channels = file[body + 9] switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => 0 };
                bitsPerPixel = channels * file[body + 8];
            }
            else if (type == "IDAT") compressed += length;
            else if (type == "IEND") break;
            pos = body + length + 4; // data and CRC
        }
        // Sizes outside the dot limits are refused by CheckSize with their own message; only judge what could be drawn.
        if (bitsPerPixel == 0 || CheckSize((int)Math.Min(width, int.MaxValue), (int)Math.Min(height, int.MaxValue)) is not null)
            return false;
        // Every row starts with one filter byte, then its pixels rounded up to whole bytes (1-bit pictures pack 8 pixels
        // per byte, so whole bytes per pixel would wrongly refuse an honest black-and-white logo). Interlaced pictures
        // unpack to a little more than this, so the test stays on the safe side for them too.
        var unpacked = height * (1 + (width * bitsPerPixel + 7) / 8);
        return compressed * MaxDeflateRatio < unpacked;
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
