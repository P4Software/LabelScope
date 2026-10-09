using System.Text.RegularExpressions;

namespace LabelScope.Core.Barcodes;

/// <summary>The ZPL <c>^BQ</c> command: parameters and the structured field data.</summary>
internal static class QrField
{
    // Error correction letter, input letter (A = automatic, M = manual) and a comma. Upper case only, as documented.
    private static readonly Regex Header = new(@"^(?<ecc>[HQML])(?<input>[AM]),", RegexOptions.Compiled);

    // D + code number (2 digits) + number of divisions (2 digits) + parity (2 hex digits) + comma.
    private static readonly Regex MixedMode = new(@"^D\d{4}[0-9A-Fa-f]{2},", RegexOptions.Compiled);

    private static QrLevel Level(char letter) => letter switch
    {
        'H' => QrLevel.H, 'Q' => QrLevel.Q, 'L' => QrLevel.L, _ => QrLevel.M,
    };

    /// <summary>Factory entry for <c>^BQa,b,c,d,e</c>.</summary>
    /// <param name="a">a orientation (ignored: QR is always normal), b model, c module size, d level, e mask.</param>
    /// <param name="data">The field data, header included.</param>
    /// <exception cref="BarcodeDataException">The header is not supported or broken, or the data does not fit a QR code.</exception>
    public static BarcodeField Build(BarcodeArgs a, string data)
    {
        if (MixedMode.IsMatch(data))
            throw new BarcodeDataException("The mixed-mode (structured append) QR header is not supported yet. Remove the D header to preview the data as one symbol.");

        // d: empty means Q, an invalid letter means M (reference). Used only when the data has no header.
        var level = Level(a.Letter(3, 'Q'));
        var mode = '\0';

        var header = Header.Match(data);
        if (header.Success)
        {
            level = Level(header.Groups["ecc"].Value[0]);
            data = data[header.Length..];
            if (header.Groups["input"].Value == "M") (mode, data) = ManualMode(data);
        }

        // e: 0 to 7; anything else falls back to the documented default 7. Every mask scans, so no penalty
        // search is done: the symbol is drawn with the mask the label asks for.
        var mask = a.Int(4, 7);
        if (mask is < 0 or > 7) mask = 7;

        var codewords = QrEncoder.BuildCodewords(data, level, mode);
        var matrix = QrEncoder.BuildMatrix(codewords, level, mask);

        // c: module size in dots, 1 to 100 (documented default 1; zero or junk falls back to it).
        var size = Math.Clamp(a.Int(2, 1), 1, 100);

        // ^FW and the a parameter have no effect on QR, so the field is always normal.
        return new MatrixField(matrix, size, size, 'N')
        {
            Note = a.Int(1, 2) == 1
                ? "QR model 1 is drawn as model 2. Both scan the same, but the pattern differs from a model 1 symbol."
                : null,
        };
    }

    /// <summary>Reads the manual input mode: N or A followed by data, or B and a four digit count followed by data.</summary>
    private static (char Mode, string Data) ManualMode(string rest)
    {
        if (rest.Length == 0)
            throw new BarcodeDataException("The QR data says the input mode is manual but no mode letter (N, A or B) follows. Add the mode letter, or use automatic input (A).");

        switch (char.ToUpperInvariant(rest[0]))
        {
            case 'N': return ('N', rest[1..]);
            case 'A': return ('A', rest[1..]);
            case 'B':
                if (rest.Length < 5 || !rest[1..5].All(char.IsAsciiDigit))
                    throw new BarcodeDataException("QR byte mode needs a four digit character count after B, for example B0005hello.");
                var count = int.Parse(rest[1..5]);
                var payload = rest[5..];
                // A count that does not match would either cut the data or leave characters unexplained; both
                // would print a symbol that reads differently from what the label meant, so neither is drawn.
                if (payload.Length != count)
                    throw new BarcodeDataException($"The QR byte mode count says {count} characters but {payload.Length} follow. Make the four digit count after B match the data.");
                return ('B', payload);
            case 'K':
                throw new BarcodeDataException("QR Kanji mode is not supported yet. Use automatic input (A) or byte mode (B) to preview the data.");
            default:
                throw new BarcodeDataException("The QR input mode letter must be N, A, B or K. Fix the letter after the comma, or use automatic input (A).");
        }
    }
}
