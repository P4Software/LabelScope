using System.Buffers;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace LabelScope.Core.Rendering;

/// <summary>
/// Turns a finished label bitmap into PNG bytes quickly. A label is black ink on white paper (with grey edges where
/// text is smoothed), so it is stored as an 8-bit grey PNG: a quarter of the pixel data of RGBA, compressed with the
/// "Up" filter and a fast zlib level. Skia's default PNG settings (RGBA, every filter tried, zlib level 6) took about
/// 12 ms for an empty 812 x 406 label, which made a send of 5000 labels take over a minute; this takes well under 1 ms.
/// </summary>
internal static class LabelPng
{
    // "Up" stores each row as its difference from the row above. Label rows repeat a lot (white margins, the bars of a
    // barcode), so most rows become zeros and compress almost for free; trying every filter per row is what made the
    // default slow. Level 3 was as fast as level 1 on real labels and gave slightly smaller files.
    private static readonly SKPngEncoderOptions FastOptions = new(SKPngEncoderFilterFlags.Up, 3);

    /// <summary>
    /// Encodes <paramref name="bitmap"/> (RGBA, as the painters draw it) as PNG. The picture is never changed: when
    /// every pixel is an opaque grey (red = green = blue), which is always the case for what the painters draw, the
    /// PNG is grey; should a pixel ever carry colour or transparency, the PNG keeps all four channels instead.
    /// </summary>
    /// <exception cref="InvalidOperationException">Skia could not encode the picture (out of memory).</exception>
    public static byte[] Encode(SKBitmap bitmap)
    {
        int width = bitmap.Width, height = bitmap.Height;
        if (bitmap.ColorType == SKColorType.Rgba8888 && width > 0 && height > 0)
        {
            var size = width * height;
            // Rented, not allocated: at 330 KB for a 4 x 2 inch label every buffer would land on the large-object heap,
            // and a stream of thousands of labels would keep the garbage collector busy with full collections.
            var gray = ArrayPool<byte>.Shared.Rent(size);
            try
            {
                if (TryToGray(bitmap.GetPixelSpan(), bitmap.RowBytes, width, height, gray))
                    return EncodeGray(gray, width, height);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(gray);
            }
        }

        using var pixmap = bitmap.PeekPixels();
        using var data = pixmap.Encode(FastOptions) ?? throw new InvalidOperationException("PNG encoding failed.");
        return data.ToArray();
    }

    /// <summary>
    /// Copies the red channel into <paramref name="gray"/>, checking on the way that each pixel is opaque grey. Stops
    /// with false at the first pixel that is not, so the caller can keep the colour.
    /// </summary>
    internal static bool TryToGray(ReadOnlySpan<byte> rgba, int rowBytes, int width, int height, byte[] gray)
    {
        for (var y = 0; y < height; y++)
        {
            // Rgba8888 in memory is R, G, B, A; read as a little-endian uint that is R | G << 8 | B << 16 | A << 24.
            var row = MemoryMarshal.Cast<byte, uint>(rgba.Slice(y * rowBytes, width * 4));
            var target = gray.AsSpan(y * width, width);
            for (var x = 0; x < row.Length; x++)
            {
                var p = row[x];
                var r = p & 0xFF;
                // Opaque grey is exactly 0xFF000000 | r * 0x010101 (premultiplied and plain are the same when opaque).
                if (p != (0xFF000000u | (r * 0x010101u))) return false;
                target[x] = (byte)r;
            }
        }
        return true;
    }

    private static byte[] EncodeGray(byte[] gray, int width, int height)
    {
        var handle = GCHandle.Alloc(gray, GCHandleType.Pinned);
        try
        {
            var info = new SKImageInfo(width, height, SKColorType.Gray8, SKAlphaType.Opaque);
            using var pixmap = new SKPixmap(info, handle.AddrOfPinnedObject(), width);
            using var data = pixmap.Encode(FastOptions) ?? throw new InvalidOperationException("PNG encoding failed.");
            return data.ToArray();
        }
        finally
        {
            handle.Free();
        }
    }
}
