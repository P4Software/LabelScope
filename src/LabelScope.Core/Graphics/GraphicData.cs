namespace LabelScope.Core.Graphics;

/// <summary>One place that knows which encoding a graphic or file download uses.</summary>
internal static class GraphicData
{
    /// <summary>
    /// Largest file accepted from ~DY (PNG, TrueType). The listener already refuses a job above 16 MB
    /// (<c>ZplStreamSplitter.MaxPendingChars</c>), so this only matters for ZB64 data that unpacks to more.
    /// </summary>
    public const long MaxFileBytes = 16L * 1024 * 1024;

    /// <summary>Decodes bitmap data (^GF format A, ~DG, ~DY .GRF): ZB64, or ASCII hex with Zebra compression letters.</summary>
    /// <exception cref="GraphicDataException">The size is outside what LabelScope draws, or the ZB64 data is damaged.</exception>
    public static MonoImage DecodeBitmap(string data, int bytesPerRow, int rows, ICollection<string> notes)
    {
        // Callers normally validated the size through GraphicLimits already; this repeats the byte ceiling in long
        // arithmetic so no caller can make either decoder allocate more than the ceiling.
        if (bytesPerRow < 1 || rows < 1)
            throw new GraphicDataException(Text.Get("Graphics_NoSize"));
        var total = (long)bytesPerRow * rows;
        if (total > GraphicLimits.MaxGraphicBytes)
            throw new GraphicDataException(Text.Get("Graphics_TooManyBytes", total, GraphicLimits.MaxGraphicBytes));

        if (!Zb64.IsZb64(data)) return HexGraphicDecoder.Decode(data, bytesPerRow, rows, notes);
        var bytes = Zb64.Decode(data, total, exact: true, notes);
        return new MonoImage(bytesPerRow, rows, bytes);
    }

    /// <summary>
    /// Decodes a whole file (~DY PNG or TrueType): ZB64, or plain ASCII hex with two digits per byte (the compression
    /// letters belong to bitmaps only). <paramref name="declaredBytes"/> is the size the command gives; 0 or less
    /// means unknown.
    /// </summary>
    /// <exception cref="GraphicDataException">The data is not hexadecimal or not valid ZB64.</exception>
    public static byte[] DecodeFile(string data, long declaredBytes, ICollection<string> notes)
    {
        var cap = declaredBytes is > 0 and <= MaxFileBytes ? declaredBytes : MaxFileBytes;
        var bytes = Zb64.IsZb64(data) ? Zb64.Decode(data, cap, exact: false, notes) : PlainHex(data, cap, notes);
        if (declaredBytes > 0 && bytes.LongLength != declaredBytes)
            notes.Add(Text.Get("Graphics_FileSizeMismatch", bytes.Length, declaredBytes));
        return bytes;
    }

    /// <summary>Two hexadecimal digits per byte; white space between them is ignored.</summary>
    private static byte[] PlainHex(string data, long cap, ICollection<string> notes)
    {
        // Sized up front for the bytes the text can hold (never more than the cap), so a large file is not copied
        // over and over while the list grows.
        var bytes = new List<byte>((int)Math.Min(cap, data.Length / 2));
        var high = -1;
        foreach (var c in data)
        {
            if (c is ' ' or '\t' or '\r' or '\n') continue;
            var v = HexGraphicDecoder.HexValue(c);
            if (v < 0)
                throw new GraphicDataException(Text.Get("Graphics_FileNotHex", (int)c));
            if (high < 0) { high = v; continue; }
            // The cap bounds the list, so a huge or endless text cannot grow it past the declared file size.
            if (bytes.Count >= cap)
            {
                notes.Add(Text.Get("Graphics_FileTooLong", cap));
                return bytes.ToArray();
            }
            bytes.Add((byte)(high << 4 | v));
            high = -1;
        }
        if (high >= 0)
            throw new GraphicDataException(Text.Get("Graphics_FileOddDigits"));
        return bytes.ToArray();
    }
}
