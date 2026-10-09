// PDF417 follows the public standard ISO/IEC 15438. Its three tables of 929 codeword bar patterns cannot be
// derived from a formula, so the codewords, error correction and bar patterns come from ZXing.Net (Apache-2.0).
// Everything ZPL decides (parameter ranges, symbol size, row height, truncation, errors) is LabelScope's own.
using System.Text;
using ZXing;
using ZXing.PDF417;
using ZXing.PDF417.Internal;

namespace LabelScope.Core.Barcodes;

/// <summary>PDF417 (<c>^B7</c>): ZPL parameters and sizing here, codeword tables and low-level encoding from ZXing.Net.</summary>
internal static class Pdf417Encoder
{
    /// <summary>A symbol holds at most 928 codewords, so Zebra prints nothing when columns x rows exceeds 928.</summary>
    private const int MaxCells = 928;

    /// <summary>Documented ranges of the <c>^B7</c> parameters.</summary>
    private const int MinColumns = 1, MaxColumns = 30, MinRows = 3, MaxRows = 90, MaxSecurity = 8;

    /// <summary>
    /// Encodes <paramref name="data"/> into one matrix row per symbol row (dark = true). <paramref name="columns"/>
    /// and <paramref name="rows"/> of 0 mean "choose automatically"; other values outside 1 to 30 columns and 3 to
    /// 90 rows are pulled into range, as is a security level outside 0 to 8 (<see cref="Build"/> reports that).
    /// </summary>
    /// <exception cref="BarcodeDataException">No data, an impossible size, or data that does not fit the size.</exception>
    public static BitMatrix Encode(string data, int security, int columns, int rows, bool truncate)
    {
        if (data.Length == 0) throw new BarcodeDataException("There is no data for the barcode.");
        columns = columns <= 0 ? 0 : Math.Clamp(columns, MinColumns, MaxColumns);
        rows = rows <= 0 ? 0 : Math.Clamp(rows, MinRows, MaxRows);
        security = Math.Clamp(security, 0, MaxSecurity);

        // Checked before the library runs: this is Zebra's own rule and gets its own message.
        if (columns > 0 && rows > 0 && columns * rows > MaxCells)
            throw new BarcodeDataException(TooManyCells(columns, rows));

        var bytes = ToByteString(data);

        // Both given: exactly that size (padded when the data is shorter), or no symbol.
        if (columns > 0 && rows > 0)
            return TryEncode(bytes, security, columns, rows, rows, truncate)
                ?? throw new BarcodeDataException(
                    $"The data does not fit a PDF417 symbol of {columns} columns x {rows} rows at security level {security}; the printer prints no symbol in this case. Ask for more columns or rows, lower the security level, or shorten the data.");

        // Columns only: the fewest rows (at least 3) that hold the data in those columns.
        if (columns > 0)
        {
            var fixedColumns = TryEncode(bytes, security, columns, MinRows, MaxRows, truncate)
                ?? throw new BarcodeDataException(
                    $"The data does not fit {columns} PDF417 columns (at most {MaxRows} rows) at security level {security}; the printer prints no symbol in this case. Ask for more columns, lower the security level, or shorten the data.");
            if (columns * fixedColumns.Height > MaxCells) throw new BarcodeDataException(TooManyCells(columns, fixedColumns.Height));
            return fixedColumns;
        }

        // Rows only, or nothing given: try 1, 2, 3 ... columns. The fewest rows that hold the data in c columns
        // only goes down as c grows, so the first c that meets the condition is the narrowest symbol that does.
        BitMatrix? widest = null;
        for (var c = MinColumns; c <= MaxColumns; c++)
        {
            var natural = TryEncode(bytes, security, c, MinRows, MaxRows, truncate);
            if (natural is null) continue;                      // too many rows needed in this few columns
            widest = natural;

            if (rows > 0)
            {
                // The requested number of rows is kept; the symbol is padded up to it.
                if (natural.Height <= rows && c * rows <= MaxCells)
                    return TryEncode(bytes, security, c, rows, rows, truncate)!;
            }
            else if (natural.Height <= 2 * c && c * natural.Height <= MaxCells)
            {
                // The reference derives the size from a 1:2 aspect ratio and gives one example: 72 code words
                // print as 6 columns by 12 rows. Rows at most twice the columns reproduces that example; the
                // exact rule of the printer is not documented, so other sizes may differ by a column (inferred).
                return natural;
            }
        }

        // Nothing given and even 30 columns need more than 60 rows: the widest symbol is the closest to the ratio.
        if (rows == 0 && widest is not null && MaxColumns * widest.Height <= MaxCells) return widest;

        throw new BarcodeDataException(rows > 0
            ? $"The data does not fit {rows} PDF417 rows (at most {MaxColumns} columns) at security level {security}; the printer prints no symbol in this case. Ask for more rows, lower the security level, or shorten the data."
            : $"The data is too long for a PDF417 symbol at security level {security} (about 1100 bytes fit at most, fewer at higher security levels). Lower the security level or shorten the data.");
    }

    /// <summary>The message for a size above <see cref="MaxCells"/>.</summary>
    private static string TooManyCells(int columns, int rows) =>
        $"{columns} columns x {rows} rows is more than a PDF417 symbol can hold (columns x rows must not exceed {MaxCells}); the printer prints no symbol in this case. Ask for fewer columns or rows.";

    /// <summary>
    /// Turns the text into one character per byte: Latin-1 characters stay as they are, any other character becomes
    /// its UTF-8 bytes (the same rule as Data Matrix). The library would otherwise replace it with '?'.
    /// </summary>
    private static string ToByteString(string data)
    {
        var sb = new StringBuilder(data.Length);
        foreach (var rune in data.EnumerateRunes())
        {
            if (rune.Value <= 0xFF) sb.Append((char)rune.Value);
            else foreach (var b in Encoding.UTF8.GetBytes(rune.ToString())) sb.Append((char)b);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Runs ZXing's PDF417 writer for exactly <paramref name="columns"/> data columns and <paramref name="minRows"/>
    /// to <paramref name="maxRows"/> rows. Returns null when the data does not fit (the library's WriterException);
    /// any other exception is a fault and is not hidden.
    /// </summary>
    private static BitMatrix? TryEncode(string bytes, int security, int columns, int minRows, int maxRows, bool truncate)
    {
        var options = new PDF417EncodingOptions
        {
            Dimensions = new Dimensions(minCols: columns, maxCols: columns, minRows: minRows, maxRows: maxRows),
            ErrorCorrection = (PDF417ErrorCorrectionLevel)security,   // 0 to 8 only: 9 would be "automatic"
            Compact = truncate,               // truncated: no right row indicator, the stop pattern is one bar
            AspectRatio = PDF417AspectRatio.A1, // one matrix row per symbol row (the writer repeats rows otherwise)
            Margin = 0,
            DisableECI = true,                // the data is already bytes; a printer adds no ECI either
        };

        ZXing.Common.BitMatrix zx;
        try
        {
            zx = new PDF417Writer().encode(bytes, BarcodeFormat.PDF_417, 0, 0, options.Hints);
        }
        catch (WriterException)
        {
            return null;    // "unable to fit message in columns" or "message too big"
        }

        // The writer turns a symbol with fewer modules across than rows on its side. An upright symbol has the
        // space after the 8-module start bar at x = 8 on every row; a turned one has the stop pattern's final bar
        // along its whole top edge, so that module is dark. Turned: symbol (row r, module m) = zx[r, Height-1-m].
        var turned = zx[8, 0];
        var width = turned ? zx.Height : zx.Width;
        var height = turned ? zx.Width : zx.Height;

        var matrix = new BitMatrix(width, height);
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                matrix[x, y] = turned ? zx[y, zx.Height - 1 - x] : zx[x, y];
        return matrix;
    }

    /// <summary>Factory entry for <c>^B7o,h,s,c,r,t</c>.</summary>
    /// <param name="a">o orientation, h row height in dots, s security level, c data columns, r rows, t truncate.</param>
    /// <param name="data">The field data (after <c>^FH</c>).</param>
    /// <exception cref="BarcodeDataException">The data or the requested size cannot be drawn.</exception>
    public static BarcodeField Build(BarcodeArgs a, string data)
    {
        // \& is a line break and \\ a backslash inside PDF417 data (reference). \\ is protected first so that
        // "\\&" reads as a backslash followed by '&', not as a backslash followed by a line break.
        data = data.Replace("\\\\", "\u0001").Replace("\\&", "\r\n").Replace("\u0001", "\\");

        int security = a.Int(2, 0), columns = a.Int(3, 0), rows = a.Int(4, 0);

        // Values outside the documented ranges are pulled into range; the label then differs from what was
        // written, so the user is told. 0 (or empty) for columns and rows means automatic.
        var notes = new List<string>();
        if (security is < 0 or > MaxSecurity)
            notes.Add($"Security level {security} is outside 0 to {MaxSecurity}; level {Math.Clamp(security, 0, MaxSecurity)} was used.");
        if (columns < 0 || columns > MaxColumns)
            notes.Add(columns < 0
                ? $"{columns} columns is not valid; the number of columns was chosen automatically."
                : $"{columns} columns is more than {MaxColumns}; {MaxColumns} columns were used.");
        if (rows < 0 || (rows > 0 && rows < MinRows) || rows > MaxRows)
            notes.Add(rows < 0
                ? $"{rows} rows is not valid; the number of rows was chosen automatically."
                : $"{rows} rows is outside {MinRows} to {MaxRows}; {Math.Clamp(rows, MinRows, MaxRows)} rows were used.");

        var matrix = Encode(data, security, columns, rows, a.Flag(5, false));

        // h is the row height in dots (inferred: the reference's "value times module width" wording is
        // ambiguous). Empty or 0: the ^BY height shared out over the rows, at least one dot per row.
        var h = a.Int(1, 0);
        var rowHeight = h > 0 ? Math.Clamp(h, 1, 1000) : Math.Max(1, (int)Math.Round((double)a.By.Height / matrix.Height));
        return new MatrixField(matrix, a.By.ModuleWidth, rowHeight, a.Orientation)
        {
            Note = notes.Count == 0 ? null : string.Join(" ", notes),
        };
    }
}
