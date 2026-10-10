using System.Numerics;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace LabelScope.Core.Graphics;

/// <summary>
/// A monochrome bitmap as ZPL sends it: rows of whole bytes, 1 bit per dot, the most significant bit is the leftmost
/// dot and 1 means black. Kept packed (an 812 x 1218 background is 124 KB) and turned into a drawing mask on demand.
/// </summary>
internal sealed class MonoImage
{
    /// <summary>Creates an image over <paramref name="bits"/>, which must hold exactly <paramref name="bytesPerRow"/> x <paramref name="rows"/> bytes.</summary>
    public MonoImage(int bytesPerRow, int rows, byte[] bits)
    {
        if (bytesPerRow < 1 || rows < 1 || bits.LongLength != (long)bytesPerRow * rows)
            throw new ArgumentException("The bit buffer does not match the declared graphic size.", nameof(bits));
        BytesPerRow = bytesPerRow;
        Height = rows;
        Bits = bits;
    }

    /// <summary>Bytes in one row.</summary>
    public int BytesPerRow { get; }

    /// <summary>Width in dots; ZPL rounds every row up to whole bytes, so this is always a multiple of 8.</summary>
    public int Width => BytesPerRow * 8;

    /// <summary>Height in dots (rows).</summary>
    public int Height { get; }

    /// <summary>The packed rows.</summary>
    public byte[] Bits { get; }

    /// <summary>Memory the image occupies, used by the printer-memory budget.</summary>
    public long SizeInBytes => Bits.LongLength;

    /// <summary>True when the dot at (<paramref name="x"/>, <paramref name="y"/>) is black.</summary>
    public bool IsBlack(int x, int y) => (Bits[y * BytesPerRow + (x >> 3)] & (0x80 >> (x & 7))) != 0;

    /// <summary>
    /// Smallest rectangle holding every black dot, or null for an all-white image. Used for the "does not fit"
    /// warning: the white padding that rounds a row up to whole bytes must not count as overhang.
    /// </summary>
    public SKRectI? InkBounds()
    {
        int left = int.MaxValue, top = int.MaxValue, right = -1, bottom = -1;
        for (var y = 0; y < Height; y++)
        {
            var row = y * BytesPerRow;
            for (var b = 0; b < BytesPerRow; b++)
            {
                var v = Bits[row + b];
                if (v == 0) continue;
                // Leading zero bits give the first black dot of this byte, trailing zero bits the last one.
                var first = b * 8 + (BitOperations.LeadingZeroCount((uint)v) - 24);
                var last = b * 8 + 7 - BitOperations.TrailingZeroCount(v);
                left = Math.Min(left, first);
                right = Math.Max(right, last);
                top = Math.Min(top, y);
                bottom = y;
            }
        }
        return right < 0 ? null : new SKRectI(left, top, right + 1, bottom + 1);
    }

    /// <summary>
    /// An Alpha8 bitmap: 255 where the dot is black, 0 elsewhere. Skia draws an Alpha8 bitmap as a mask in the
    /// paint's colour, so white dots leave the label untouched and ^FR (white with Difference) still inverts.
    /// The caller disposes the result.
    /// </summary>
    public SKBitmap ToMask()
    {
        var bitmap = new SKBitmap(new SKImageInfo(Width, Height, SKColorType.Alpha8, SKAlphaType.Premul));
        var row = new byte[Width];
        var dst = bitmap.GetPixels();
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++) row[x] = IsBlack(x, y) ? (byte)255 : (byte)0;
            // RowBytes may be padded beyond Width, so each row is copied to its own start.
            Marshal.Copy(row, 0, dst + y * bitmap.RowBytes, Width);
        }
        return bitmap;
    }

    /// <summary>
    /// Turns RGBA pixels (a decoded PNG or a finished label for ^IS) into black and white. Each pixel is laid over
    /// the white label first, so transparent parts stay white, and is black when its luminance is below the middle.
    /// </summary>
    /// <param name="pixels">RGBA bytes, row after row.</param>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    /// <param name="rowBytes">Bytes from one row to the next (at least width x 4).</param>
    /// <param name="premultiplied">True when the colour channels are already multiplied by alpha.</param>
    public static MonoImage FromRgba(ReadOnlySpan<byte> pixels, int width, int height, int rowBytes, bool premultiplied)
    {
        var bytesPerRow = (width + 7) / 8;
        var bits = new byte[(long)bytesPerRow * height];
        for (var y = 0; y < height; y++)
        {
            var src = pixels.Slice(y * rowBytes, width * 4);
            for (var x = 0; x < width; x++)
            {
                int r = src[x * 4], g = src[x * 4 + 1], b = src[x * 4 + 2], a = src[x * 4 + 3];
                // Integer luminance (ITU-R BT.601 weights, scaled by 1000), laid over the white label.
                var lum = (299 * r + 587 * g + 114 * b) / 1000;
                var over = premultiplied ? lum + (255 - a) : (lum * a + 255 * (255 - a)) / 255;
                if (over < 128) bits[y * bytesPerRow + (x >> 3)] |= (byte)(0x80 >> (x & 7));
            }
        }
        return new MonoImage(bytesPerRow, height, bits);
    }
}
