// src/LabelScope.Core/Barcodes/EanUpcEncoder.cs
namespace LabelScope.Core.Barcodes;

/// <summary>EAN-13 (<c>^BE</c>), UPC-A (<c>^BU</c>), EAN-8 (<c>^B8</c>) and UPC-E (<c>^B9</c>) encoders.</summary>
internal static class EanUpcEncoder
{
    /// <summary>Odd-parity ("L") bit patterns of the digits 0 to 9, 7 modules each ('1' = bar). From GS1 General Specifications.</summary>
    private static readonly string[] L =
        { "0001101", "0011001", "0010011", "0111101", "0100011", "0110001", "0101111", "0111011", "0110111", "0001011" };

    /// <summary>Right-hand patterns: the complement of L.</summary>
    private static string R(int d) => new(L[d].Select(c => c == '1' ? '0' : '1').ToArray());

    /// <summary>Even-parity ("G") patterns: R read backwards.</summary>
    private static string G(int d) => new(R(d).Reverse().ToArray());

    /// <summary>EAN-13: which of L or G encodes each of the six left digits, chosen by the first digit.</summary>
    private static readonly string[] Ean13Parity =
        { "LLLLLL", "LLGLGG", "LLGGLG", "LLGGGL", "LGLLGG", "LGGLLG", "LGGGLL", "LGLGLG", "LGLGGL", "LGGLGL" };

    /// <summary>UPC-E (number system 0): parity of the six digits by check digit; E = even (G), O = odd (L).</summary>
    private static readonly string[] UpcEParity =
        { "EEEOOO", "EEOEOO", "EEOOEO", "EEOOOE", "EOEEOO", "EOOEEO", "EOOOEE", "EOEOEO", "EOEOOE", "EOOEOE" };

    private const string Guard = "101", Centre = "01010", UpcEEnd = "010101";

    // ---- shared helpers --------------------------------------------------------------------------

    /// <summary>Digits only, left-padded with zeros to <paramref name="count"/> or cut to the first <paramref name="count"/>.</summary>
    private static string Normalize(string data, int count)
    {
        if (data.Length == 0) throw new BarcodeDataException("There is no data for the barcode.");
        foreach (var c in data)
            if (!char.IsAsciiDigit(c))
                throw new BarcodeDataException($"This barcode accepts digits only, but the data contains '{c}'.");
        return data.Length < count ? data.PadLeft(count, '0') : data[..count];
    }

    private static string Digit(int d, char kind) => kind switch { 'L' or 'O' => L[d], 'G' or 'E' => G(d), _ => R(d) };

    /// <summary>
    /// Turns modules into a symbol. <paramref name="guardRanges"/> are module ranges whose bars reach into the
    /// text zone; they are converted to the indexes of the runs those bars belong to.
    /// </summary>
    private static LinearSymbol Make(string bits, int narrow, string text, IReadOnlyList<TextSpan> spans, params (int Start, int End)[] guardRanges)
    {
        var runs = new RunList().AddBits(bits, narrow).ToArray();

        // Walk the bits, counting runs, so a module position can be mapped to its run.
        var guards = new HashSet<int>();
        var run = 0;
        for (var i = 0; i < bits.Length; i++)
        {
            if (i > 0 && bits[i] != bits[i - 1]) run++;
            if (bits[i] == '1' && guardRanges.Any(g => i >= g.Start && i < g.End)) guards.Add(run);
        }
        return new LinearSymbol(runs, text, spans, guards);
    }

    // ---- EAN-13 and UPC-A -----------------------------------------------------------------------

    /// <summary>The 95 modules of an EAN-13 symbol for 13 digits (UPC-A is the same with a leading 0).</summary>
    private static string Ean13Bits(string d13)
    {
        var parity = Ean13Parity[d13[0] - '0'];
        var bits = Guard;
        for (var i = 0; i < 6; i++) bits += Digit(d13[1 + i] - '0', parity[i]);
        bits += Centre;
        for (var i = 0; i < 6; i++) bits += Digit(d13[7 + i] - '0', 'R');
        return bits + Guard;
    }

    /// <summary>Encodes EAN-13 from 12 digits (padded or cut); the 13th digit is the Mod 10 check.</summary>
    public static LinearSymbol EncodeEan13(string data, BarDefaults by)
    {
        var d = Normalize(data, 12);
        d += (char)('0' + CheckDigits.Mod10(d));
        var n = by.Narrow;
        var spans = new[]
        {
            new TextSpan(d[..1], -9 * n, -1 * n),
            new TextSpan(d[1..7], 3 * n, 45 * n),
            new TextSpan(d[7..], 50 * n, 92 * n),
        };
        return Make(Ean13Bits(d), n, d, spans, (0, 3), (45, 50), (92, 95));
    }

    /// <summary>Encodes UPC-A from 11 digits (padded or cut) plus the Mod 10 check.</summary>
    public static LinearSymbol EncodeUpcA(string data, bool showCheck, BarDefaults by)
    {
        var d = Normalize(data, 11);
        d += (char)('0' + CheckDigits.Mod10(d));
        var n = by.Narrow;
        var spans = new List<TextSpan>
        {
            new(d[..1], -9 * n, -1 * n),
            new(d[1..6], 10 * n, 45 * n),
            new(d[6..11], 50 * n, 85 * n),
        };
        if (showCheck) spans.Add(new TextSpan(d[11..], 96 * n, 104 * n));
        // UPC-A is an EAN-13 with a leading 0; its first and last digit bars are as tall as the guards.
        return Make(Ean13Bits("0" + d), n, d, spans, (0, 3), (3, 10), (45, 50), (85, 92), (92, 95));
    }

    // ---- EAN-8 ------------------------------------------------------------------------------------

    /// <summary>Encodes EAN-8 from 7 digits (padded or cut) plus the Mod 10 check.</summary>
    public static LinearSymbol EncodeEan8(string data, BarDefaults by)
    {
        var d = Normalize(data, 7);
        d += (char)('0' + CheckDigits.Mod10(d));
        var bits = Guard;
        for (var i = 0; i < 4; i++) bits += L[d[i] - '0'];
        bits += Centre;
        for (var i = 4; i < 8; i++) bits += R(d[i] - '0');
        bits += Guard;

        var n = by.Narrow;
        var spans = new[] { new TextSpan(d[..4], 3 * n, 31 * n), new TextSpan(d[4..], 36 * n, 64 * n) };
        return Make(bits, n, d, spans, (0, 3), (31, 36), (64, 67));
    }

    // ---- UPC-E --------------------------------------------------------------------------------------

    /// <summary>
    /// Shortens a 10 digit manufacturer (5) + product (5) number to the 6 digit UPC-E form (number system 0),
    /// using the zero-suppression rules of the reference.
    /// </summary>
    /// <exception cref="BarcodeDataException">The number cannot be shortened.</exception>
    public static string CompressUpcE(string ten)
    {
        var m = ten[..5];
        var p = ten[5..];
        if (m[2] <= '2' && m[3] == '0' && m[4] == '0' && p[..2] == "00") return $"{m[0]}{m[1]}{p[2..]}{m[2]}";   // 000, 100, 200
        if (m[2] >= '3' && m[3] == '0' && m[4] == '0' && p[..3] == "000") return $"{m[0]}{m[1]}{m[2]}{p[3..]}3";  // 300 to 900
        if (m[3] != '0' && m[4] == '0' && p[..4] == "0000") return $"{m[..4]}{p[4]}4";                          // 10 to 90
        if (m[4] != '0' && p[..4] == "0000" && p[4] >= '5') return $"{m}{p[4]}";                                // product 5 to 9
        throw new BarcodeDataException("This UPC-E number cannot be written in the short form: the manufacturer and product numbers do not follow the zero-suppression rules.");
    }

    /// <summary>The inverse of <see cref="CompressUpcE"/>: manufacturer and product number of a 6 digit UPC-E.</summary>
    public static (string Manufacturer, string Product) ExpandUpcE(string six)
    {
        var last = six[5];
        return last switch
        {
            '0' or '1' or '2' => (six[..2] + last + "00", "00" + six[2..5]),
            '3' => (six[..3] + "00", "000" + six[3..5]),
            '4' => (six[..4] + "0", "0000" + six[4]),
            _ => (six[..5], "0000" + last),
        };
    }

    /// <summary>Encodes UPC-E from 10 digits (shortened here) or from the 6 digit short form.</summary>
    public static LinearSymbol EncodeUpcE(string data, bool showCheck, BarDefaults by)
    {
        if (data.Length == 0) throw new BarcodeDataException("There is no data for the barcode.");
        var digits = Normalize(data, data.Length);
        string six;
        if (digits.Length == 6) six = digits;
        else if (digits.Length == 10) six = CompressUpcE(digits);
        else throw new BarcodeDataException("UPC-E needs 10 digits (5 manufacturer + 5 product) or the 6 digit short form.");

        var (m, p) = ExpandUpcE(six);
        var check = CheckDigits.Mod10("0" + m + p);
        var parity = UpcEParity[check];

        var bits = Guard;
        for (var i = 0; i < 6; i++) bits += Digit(six[i] - '0', parity[i]);
        bits += UpcEEnd;

        var n = by.Narrow;
        var spans = new List<TextSpan> { new("0", -9 * n, -1 * n), new(six, 3 * n, 45 * n) };
        if (showCheck) spans.Add(new TextSpan(check.ToString(), 52 * n, 60 * n));
        return Make(bits, n, "0" + six + check, spans, (0, 3), (45, 51));
    }

    // ---- factory entries -----------------------------------------------------------------------------

    private static BarcodeLook Look(BarcodeArgs a) => new(a.Height(1), a.Flag(2, true), a.Flag(3, false));

    /// <summary>Factory entry for <c>^BEo,h,f,g</c>.</summary>
    public static BarcodeField BuildEan13(BarcodeArgs a, string data) =>
        new LinearField(EncodeEan13(data, a.By), Look(a), a.By.Narrow, a.Orientation);

    /// <summary>Factory entry for <c>^BUo,h,f,g,e</c>.</summary>
    public static BarcodeField BuildUpcA(BarcodeArgs a, string data) =>
        new LinearField(EncodeUpcA(data, a.Flag(4, true), a.By), Look(a), a.By.Narrow, a.Orientation);

    /// <summary>Factory entry for <c>^B8o,h,f,g</c>.</summary>
    public static BarcodeField BuildEan8(BarcodeArgs a, string data) =>
        new LinearField(EncodeEan8(data, a.By), Look(a), a.By.Narrow, a.Orientation);

    /// <summary>Factory entry for <c>^B9o,h,f,g,e</c>.</summary>
    public static BarcodeField BuildUpcE(BarcodeArgs a, string data) =>
        new LinearField(EncodeUpcE(data, a.Flag(4, true), a.By), Look(a), a.By.Narrow, a.Orientation);
}
