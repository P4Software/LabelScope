// Data Matrix ECC 200 follows the public standard ISO/IEC 16022: its symbol size table, ASCII encodation,
// the 253-state pad randomising algorithm, Reed-Solomon over GF(256) with polynomial 0x12D and round-robin
// block interleaving, and its reference codeword placement algorithm (Annex F, the "Utah" placement with
// its four corner cases and edge wrapping rules), which Place below follows step for step.
using System.Text;

namespace LabelScope.Core.Barcodes;

/// <summary>One Data Matrix ECC 200 symbol size (ISO/IEC 16022 table 7).</summary>
/// <param name="Rows">Symbol height in modules, finder and alignment patterns included.</param>
/// <param name="Cols">Symbol width in modules.</param>
/// <param name="DataCodewords">Capacity in data codewords.</param>
/// <param name="EcCodewords">Total error correction codewords (all blocks).</param>
/// <param name="Blocks">Number of interleaved Reed-Solomon blocks.</param>
/// <param name="RegionsDown">Data regions stacked vertically.</param>
/// <param name="RegionsAcross">Data regions side by side.</param>
internal sealed record DmSize(int Rows, int Cols, int DataCodewords, int EcCodewords, int Blocks, int RegionsDown, int RegionsAcross)
{
    /// <summary>True for the six rectangular sizes.</summary>
    public bool IsRectangular => Rows != Cols;
}

/// <summary>Data Matrix ECC 200 encoder with ASCII encodation, as used by <c>^BX</c> quality 200.</summary>
internal static class DataMatrixEncoder
{
    /// <summary>Marks FNC1 inside the token list (bytes are 0 to 255, so any larger value is free).</summary>
    internal const int Fnc1Token = 1232;

    private static readonly GaloisField Field = new(0x12D);

    /// <summary>The quality values the ZPL reference documents for <c>^BX</c>.</summary>
    private static readonly int[] DocumentedQualities = { 0, 50, 80, 100, 140, 200 };

    /// <summary>All sizes, squares first (ascending), then the rectangles (ascending).</summary>
    internal static readonly IReadOnlyList<DmSize> Sizes = new DmSize[]
    {
        new(10, 10, 3, 5, 1, 1, 1),       new(12, 12, 5, 7, 1, 1, 1),       new(14, 14, 8, 10, 1, 1, 1),
        new(16, 16, 12, 12, 1, 1, 1),     new(18, 18, 18, 14, 1, 1, 1),     new(20, 20, 22, 18, 1, 1, 1),
        new(22, 22, 30, 20, 1, 1, 1),     new(24, 24, 36, 24, 1, 1, 1),     new(26, 26, 44, 28, 1, 1, 1),
        new(32, 32, 62, 36, 1, 2, 2),     new(36, 36, 86, 42, 1, 2, 2),     new(40, 40, 114, 48, 1, 2, 2),
        new(44, 44, 144, 56, 1, 2, 2),    new(48, 48, 174, 68, 1, 2, 2),    new(52, 52, 204, 84, 2, 2, 2),
        new(64, 64, 280, 112, 2, 4, 4),   new(72, 72, 368, 144, 4, 4, 4),   new(80, 80, 456, 192, 4, 4, 4),
        new(88, 88, 576, 224, 4, 4, 4),   new(96, 96, 696, 272, 4, 4, 4),   new(104, 104, 816, 336, 6, 4, 4),
        new(120, 120, 1050, 408, 6, 6, 6), new(132, 132, 1304, 496, 8, 6, 6), new(144, 144, 1558, 620, 10, 6, 6),
        new(8, 18, 5, 7, 1, 1, 1),        new(8, 32, 10, 11, 1, 1, 2),      new(12, 26, 16, 14, 1, 1, 1),
        new(12, 36, 22, 18, 1, 1, 2),     new(16, 36, 32, 24, 1, 1, 2),     new(16, 48, 49, 28, 1, 1, 2),
    };

    // ---- data ---------------------------------------------------------------------------------------

    /// <summary>
    /// Turns field data into tokens: byte values (0 to 255; characters above 255 become their UTF-8 bytes) and
    /// <see cref="Fnc1Token"/>. Escapes: the escape character twice is itself, 1 is FNC1, dNNN is byte NNN, and
    /// @, A to Z, [, \, ], ^, _ give the control character with that letter.
    /// </summary>
    /// <exception cref="BarcodeDataException">An escape is broken or not supported yet.</exception>
    internal static List<int> ParseEscapes(string data, char escape)
    {
        var tokens = new List<int>();
        var runes = data.EnumerateRunes().ToList();
        for (var i = 0; i < runes.Count; i++)
        {
            var r = runes[i];
            if (r.Value != escape)
            {
                AddRune(tokens, r);
                continue;
            }

            // A lone escape character at the very end has no meaning; guessing "literal" could print data the
            // label did not intend, so it is reported instead.
            if (i == runes.Count - 1)
                throw new BarcodeDataException(Text.Get("Barcode_DataMatrix_EndsWithEscape", escape));

            // Compare whole rune values: casting a rune above U+FFFF to char would truncate it to an unrelated letter.
            var next = runes[++i].Value;
            if (next == escape) { AddRune(tokens, new Rune(escape)); continue; }
            if (next == '1') { tokens.Add(Fnc1Token); continue; }
            if (next is >= '@' and <= '_') { tokens.Add(next - '@'); continue; }          // ~@ = NUL ... ~G = BEL
            if (next == 'd')
            {
                var digits = runes.Skip(i + 1).Take(3).Select(x => x.Value).ToList();
                if (digits.Count != 3 || !digits.All(v => v is >= '0' and <= '9'))
                    throw new BarcodeDataException(Text.Get("Barcode_DataMatrix_EscapeDigits", escape));
                var value = digits.Aggregate(0, (acc, v) => acc * 10 + (v - '0'));
                if (value > 255)
                    throw new BarcodeDataException(Text.Get("Barcode_DataMatrix_EscapeDigits", escape));
                tokens.Add(value);
                i += 3;
                continue;
            }
            throw new BarcodeDataException(Text.Get("Barcode_DataMatrix_EscapeUnsupported", escape, char.ConvertFromUtf32(next)));
        }
        return tokens;
    }

    private static void AddRune(List<int> tokens, Rune r)
    {
        if (r.Value <= 0xFF) tokens.Add(r.Value);
        else foreach (var b in Encoding.UTF8.GetBytes(r.ToString())) tokens.Add(b);
    }

    /// <summary>ASCII encodation: digit pairs in one codeword (130 + value), other bytes as value + 1, bytes above 127 behind the upper-shift codeword 235.</summary>
    internal static List<int> EncodeAscii(IReadOnlyList<int> tokens)
    {
        var cw = new List<int>();
        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t == Fnc1Token) { cw.Add(232); continue; }
            if (t is >= '0' and <= '9' && i + 1 < tokens.Count && tokens[i + 1] is >= '0' and <= '9')
            {
                cw.Add(130 + (t - '0') * 10 + (tokens[i + 1] - '0'));
                i++;
            }
            else if (t < 128) cw.Add(t + 1);
            else { cw.Add(235); cw.Add(t - 128 + 1); }
        }
        return cw;
    }

    /// <summary>
    /// The symbol for <paramref name="codewords"/> data codewords. Without a requested size (both minimums 0) it is
    /// the smallest symbol that holds the data. With one, it is the smallest symbol at least minRows x minCols
    /// (the requested size itself when it exists), and the data must fit in it.
    /// </summary>
    /// <exception cref="BarcodeDataException">
    /// The requested size is larger than any symbol, the data does not fit the requested size, or no symbol is
    /// large enough for the data.
    /// </exception>
    internal static DmSize SelectSize(int codewords, bool rectangular, int minRows, int minCols)
    {
        // A requested size beyond the largest symbol of the shape is its own mistake; calling it "too much data"
        // would send the user looking in the wrong place. Only the dimension that is out of range is named.
        var shape = rectangular ? Text.Get("Barcode_DataMatrix_Rectangular") : Text.Get("Barcode_DataMatrix_Square");
        var largest = Sizes.Where(s => s.IsRectangular == rectangular).MaxBy(s => s.Rows * s.Cols)!;
        var tooLarge = new List<string>();
        if (minRows > largest.Rows) tooLarge.Add(Text.Get("Barcode_DataMatrix_Rows", minRows));
        if (minCols > largest.Cols) tooLarge.Add(Text.Get("Barcode_DataMatrix_Columns", minCols));
        if (tooLarge.Count > 0)
            throw new BarcodeDataException(
                Text.Get("Barcode_DataMatrix_SizeTooLarge", string.Join(Text.Get("Barcode_DataMatrix_And"), tooLarge), shape, largest.Rows, largest.Cols));

        if (minRows > 0 || minCols > 0)
        {
            // A forced size: Zebra prints no symbol at all when the data does not fit it, so neither does LabelScope
            // (drawing a bigger symbol would show something the printer never prints).
            var forced = Sizes.First(s => s.IsRectangular == rectangular && s.Rows >= minRows && s.Cols >= minCols);
            if (forced.DataCodewords < codewords)
                throw new BarcodeDataException(
                    Text.Get("Barcode_DataMatrix_DoesNotFitForced", forced.Rows, forced.Cols, forced.DataCodewords, codewords));
            return forced;
        }

        var size = Sizes.FirstOrDefault(s => s.IsRectangular == rectangular && s.DataCodewords >= codewords);
        return size ?? throw new BarcodeDataException(rectangular
            ? Text.Get("Barcode_DataMatrix_TooLongRectangular")
            : Text.Get("Barcode_DataMatrix_TooLong"));
    }

    // ---- codewords -----------------------------------------------------------------------------------

    /// <summary>Pads <paramref name="data"/> to the symbol's capacity and appends the interleaved Reed-Solomon codewords.</summary>
    internal static byte[] Codewords(List<int> data, DmSize size)
    {
        var padded = new List<int>(data);
        if (padded.Count < size.DataCodewords) padded.Add(129);                       // first pad codeword
        while (padded.Count < size.DataCodewords)
        {
            // Later pads are scrambled by the 253-state randomising algorithm of ISO/IEC 16022 using their 1-based
            // position, so runs of pads do not create long identical module patterns.
            var v = 129 + (149 * (padded.Count + 1)) % 253 + 1;
            padded.Add(v > 254 ? v - 254 : v);
        }

        var result = new byte[size.DataCodewords + size.EcCodewords];
        for (var i = 0; i < size.DataCodewords; i++) result[i] = (byte)padded[i];

        // Data codeword i belongs to block i mod blocks; each block gets its own error correction codewords,
        // which are interleaved the same way. (For 144 x 144 this gives the standard's 8 blocks of 156 and 2 of 155.)
        var ecPerBlock = size.EcCodewords / size.Blocks;
        for (var b = 0; b < size.Blocks; b++)
        {
            var block = new List<byte>();
            for (var i = b; i < size.DataCodewords; i += size.Blocks) block.Add(result[i]);
            var ec = Field.Remainder(block.ToArray(), ecPerBlock, 1);
            for (var i = 0; i < ecPerBlock; i++) result[size.DataCodewords + i * size.Blocks + b] = ec[i];
        }
        return result;
    }

    // ---- module placement ---------------------------------------------------------------------------

    /// <summary>
    /// Places codeword bits into the logical data area (all regions joined, borders removed) with the "Utah"
    /// algorithm of ISO/IEC 16022 Annex F: the eight bits of a codeword form an L shaped group that is moved
    /// diagonally across the area, with four special corner arrangements for the places where the shape would
    /// leave the area. Returns [row, column], true = dark.
    /// </summary>
    internal static bool[,] Place(byte[] cw, int nrow, int ncol)
    {
        var value = new bool[nrow, ncol];
        var done = new bool[nrow, ncol];

        void Module(int r, int c, int p, int bit)
        {
            // A module that falls off the top or left edge wraps to the opposite side, as the standard prescribes.
            if (r < 0) { r += nrow; c += 4 - ((nrow + 4) % 8); }
            if (c < 0) { c += ncol; r += 4 - ((ncol + 4) % 8); }
            value[r, c] = p < cw.Length && ((cw[p] >> (8 - bit)) & 1) == 1;
            done[r, c] = true;
        }
        void Utah(int r, int c, int p)
        {
            Module(r - 2, c - 2, p, 1); Module(r - 2, c - 1, p, 2);
            Module(r - 1, c - 2, p, 3); Module(r - 1, c - 1, p, 4); Module(r - 1, c, p, 5);
            Module(r, c - 2, p, 6); Module(r, c - 1, p, 7); Module(r, c, p, 8);
        }
        void Corner1(int p)
        {
            Module(nrow - 1, 0, p, 1); Module(nrow - 1, 1, p, 2); Module(nrow - 1, 2, p, 3); Module(0, ncol - 2, p, 4);
            Module(0, ncol - 1, p, 5); Module(1, ncol - 1, p, 6); Module(2, ncol - 1, p, 7); Module(3, ncol - 1, p, 8);
        }
        void Corner2(int p)
        {
            Module(nrow - 3, 0, p, 1); Module(nrow - 2, 0, p, 2); Module(nrow - 1, 0, p, 3); Module(0, ncol - 4, p, 4);
            Module(0, ncol - 3, p, 5); Module(0, ncol - 2, p, 6); Module(0, ncol - 1, p, 7); Module(1, ncol - 1, p, 8);
        }
        void Corner3(int p)
        {
            Module(nrow - 3, 0, p, 1); Module(nrow - 2, 0, p, 2); Module(nrow - 1, 0, p, 3); Module(0, ncol - 2, p, 4);
            Module(0, ncol - 1, p, 5); Module(1, ncol - 1, p, 6); Module(2, ncol - 1, p, 7); Module(3, ncol - 1, p, 8);
        }
        void Corner4(int p)
        {
            Module(nrow - 1, 0, p, 1); Module(nrow - 1, ncol - 1, p, 2); Module(0, ncol - 3, p, 3); Module(0, ncol - 2, p, 4);
            Module(0, ncol - 1, p, 5); Module(1, ncol - 3, p, 6); Module(1, ncol - 2, p, 7); Module(1, ncol - 1, p, 8);
        }

        int pos = 0, row = 4, col = 0;
        do
        {
            if (row == nrow && col == 0) Corner1(pos++);
            if (row == nrow - 2 && col == 0 && ncol % 4 != 0) Corner2(pos++);
            if (row == nrow - 2 && col == 0 && ncol % 8 == 4) Corner3(pos++);
            if (row == nrow + 4 && col == 2 && ncol % 8 == 0) Corner4(pos++);

            // Sweep up and to the right...
            do
            {
                if (row < nrow && col >= 0 && !done[row, col]) Utah(row, col, pos++);
                row -= 2; col += 2;
            } while (row >= 0 && col < ncol);
            row++; col += 3;

            // ...then down and to the left.
            do
            {
                if (row >= 0 && col < ncol && !done[row, col]) Utah(row, col, pos++);
                row += 2; col -= 2;
            } while (row < nrow && col >= 0);
            row += 3; col++;
        } while (row < nrow || col < ncol);

        // Sizes with spare modules end with a fixed pattern in the bottom right corner.
        if (!done[nrow - 1, ncol - 1])
        {
            value[nrow - 1, ncol - 1] = true;
            value[nrow - 2, ncol - 2] = true;
        }
        return value;
    }

    /// <summary>Builds the symbol: places the data, then draws each region's solid L and alternating edges around it.</summary>
    internal static BitMatrix BuildSymbol(DmSize size, byte[] codewords)
    {
        var regionRows = (size.Rows - 2 * size.RegionsDown) / size.RegionsDown;
        var regionCols = (size.Cols - 2 * size.RegionsAcross) / size.RegionsAcross;
        var logical = Place(codewords, regionRows * size.RegionsDown, regionCols * size.RegionsAcross);

        var m = new BitMatrix(size.Cols, size.Rows);
        for (var ry = 0; ry < size.RegionsDown; ry++)
            for (var rx = 0; rx < size.RegionsAcross; rx++)
            {
                int x0 = rx * (regionCols + 2), y0 = ry * (regionRows + 2);

                // Border of this region: left column and bottom row solid, top row and right column alternate
                // (dark at the top left of the top row and at the bottom of the right column).
                for (var i = 0; i < regionRows + 2; i++)
                {
                    m[x0, y0 + i] = true;
                    m[x0 + regionCols + 1, y0 + i] = (regionRows + 1 - i) % 2 == 0;
                }
                for (var i = 0; i < regionCols + 2; i++)
                {
                    m[x0 + i, y0 + regionRows + 1] = true;
                    m[x0 + i, y0] = i % 2 == 0;
                }

                // Data modules of this region.
                for (var r = 0; r < regionRows; r++)
                    for (var c = 0; c < regionCols; c++)
                        m[x0 + 1 + c, y0 + 1 + r] = logical[ry * regionRows + r, rx * regionCols + c];
            }
        return m;
    }

    // ---- ZPL field -------------------------------------------------------------------------------------

    /// <summary>Factory entry for <c>^BXo,h,s,c,r,f,g,a</c>.</summary>
    /// <param name="a">o orientation, h module size, s quality, c columns, r rows, f format id (ignored), g escape character, a aspect.</param>
    /// <param name="data">The field data (after <c>^FH</c>).</param>
    /// <exception cref="BarcodeDataException">The data, an escape or the requested size cannot be drawn.</exception>
    public static BarcodeField Build(BarcodeArgs a, string data)
    {
        var escape = a.Text(6) is { Length: > 0 } g ? g[0] : '~';
        var tokens = ParseEscapes(data, escape);
        if (tokens.Count == 0) throw new BarcodeDataException(Text.Get("Barcode_NoData"));
        var dataCodewords = EncodeAscii(tokens);

        // Quality: only 200 is drawn as written. Omitted (Zebra's default 0) and 0 to 140 are the older
        // convolutional codes, which are deferred: they are drawn as ECC 200 with a warning.
        var notes = new List<string>();
        var qualityText = a.Text(2);
        var minRows = Math.Max(0, a.Int(4, 0));
        var minCols = Math.Max(0, a.Int(3, 0));
        if (qualityText.Length == 0)
        {
            // Zebra's documented default is 0 (ECC 000), which LabelScope does not draw; say so every time, since
            // the printed label would carry a different, older symbol.
            notes.Add(Text.Get("Barcode_DataMatrix_NoQuality"));
        }
        else
        {
            var quality = a.Int(2, -1);
            if (quality != 200)
            {
                notes.Add(DocumentedQualities.Contains(quality)
                    ? Text.Get("Barcode_DataMatrix_OldQuality", quality)
                    : Text.Get("Barcode_DataMatrix_UnknownQuality", qualityText));

                // Below 200 Zebra only accepts odd sizes (an even column count prints nothing), and those sizes do
                // not exist in ECC 200, so the requested size is dropped for the automatic one.
                if (minRows > 0 || minCols > 0)
                {
                    notes.Add(Text.Get("Barcode_DataMatrix_SizeIgnored"));
                    minRows = minCols = 0;
                }
            }
        }

        var size = SelectSize(dataCodewords.Count, a.Int(7, 1) == 2, minRows, minCols);

        // A requested size that is not one of the standard sizes (25 x 25, an odd column count) is rounded up to
        // the next one; the label then prints a different size than written, so the user is told.
        if ((minRows > 0 && minRows != size.Rows) || (minCols > 0 && minCols != size.Cols))
            notes.Add(Text.Get("Barcode_DataMatrix_SizeRounded",
                Dim(minRows, "Barcode_DataMatrix_Rows", "Barcode_DataMatrix_AutoRows"),
                Dim(minCols, "Barcode_DataMatrix_Columns", "Barcode_DataMatrix_AutoColumns"), size.Rows, size.Cols));
        var matrix = BuildSymbol(size, Codewords(dataCodewords, size));

        // h = module size; empty or 0 means "fit the ^BY height into the rows", at least one dot.
        var h = a.Int(1, 0);
        var module = h > 0
            ? Math.Clamp(h, 1, 1000)
            : Math.Max(1, (int)Math.Round((double)a.By.Height / size.Rows));

        return new MatrixField(matrix, module, module, a.Orientation)
        {
            Note = notes.Count == 0 ? null : string.Join(" ", notes),
        };
    }

    /// <summary>
    /// "24 rows" for a requested dimension (<paramref name="countKey"/>), "automatic rows" for one left empty
    /// (<paramref name="autoKey"/>), in the current language.
    /// </summary>
    private static string Dim(int value, string countKey, string autoKey) =>
        value > 0 ? Text.Get(countKey, value) : Text.Get(autoKey);
}
