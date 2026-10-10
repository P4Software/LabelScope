using System.Text;

namespace LabelScope.Core.Barcodes;

/// <summary>Interleaved 2 of 5 (<c>^B2</c>), Codabar (<c>^BK</c>), Code 11 (<c>^B1</c>) and MSI (<c>^BM</c>) encoders.</summary>
internal static class OtherLinearEncoders
{
    // ---- Interleaved 2 of 5 -----------------------------------------------------------------------

    /// <summary>The five elements of each digit ('w' = wide); every digit has two wide elements.</summary>
    private static readonly string[] Itf =
        { "nnwwn", "wnnnw", "nwnnw", "wwnnn", "nnwnw", "wnwnn", "nwwnn", "nnnww", "wnnwn", "nwnwn" };

    internal static IReadOnlyList<string> ItfPatternsForTests => Itf;

    private static string DigitsOnly(string data)
    {
        if (data.Length == 0) throw new BarcodeDataException(Text.Get("Barcode_NoData"));
        foreach (var c in data)
            if (!char.IsAsciiDigit(c))
                throw new BarcodeDataException(Text.Get("Barcode_DigitsOnly", c));
        return data;
    }

    /// <summary>Interleaved 2 of 5: digit pairs where the first digit is carried by the bars and the second by the spaces.</summary>
    public static LinearSymbol EncodeInterleaved2of5(string data, bool mod10, BarDefaults by)
    {
        var d = DigitsOnly(data);

        // The symbol needs an even number of digits. With a check digit the total counts, so the padding
        // decision is made before the check digit is computed (and the check covers the padding zero).
        if ((d.Length + (mod10 ? 1 : 0)) % 2 == 1) d = "0" + d;
        if (mod10) d += (char)('0' + CheckDigits.Mod10(d));

        var runs = new RunList().AddPattern("nnnn", by.Narrow, by.Wide);      // start
        var pair = new StringBuilder(10);
        for (var i = 0; i < d.Length; i += 2)
        {
            pair.Clear();
            var bars = Itf[d[i] - '0'];
            var spaces = Itf[d[i + 1] - '0'];
            for (var k = 0; k < 5; k++) pair.Append(bars[k]).Append(spaces[k]);   // bar, space, bar, space ...
            runs.AddPattern(pair.ToString(), by.Narrow, by.Wide);
        }
        runs.AddPattern("wnn", by.Narrow, by.Wide);                           // stop: wide bar, narrow space, narrow bar
        return new LinearSymbol(runs.ToArray(), d);
    }

    /// <summary>Factory entry for <c>^B2o,h,f,g,e,j</c>.</summary>
    public static BarcodeField BuildInterleaved2of5(BarcodeArgs a, string data) =>
        new LinearField(
            EncodeInterleaved2of5(data, a.Flag(4, false), a.By),
            new BarcodeLook(a.Height(1), a.Flag(2, true), a.Flag(3, false)),
            a.By.Narrow, a.Orientation);

    // ---- Codabar ------------------------------------------------------------------------------------

    private const string CodabarChars = "0123456789-$:/.+";

    private static readonly string[] CodabarPatterns =
    {
        "nnnnnww", "nnnnwwn", "nnnwnnw", "wwnnnnn", "nnwnnwn", "wnnnnwn", "nwnnnnw", "nwnnwnn", "nwwnnnn", "wnnwnnn",
        "nnnwwnn", "nnwwnnn", "wnnnwnw", "wnwnnnw", "wnwnwnn", "nnwnwnw",
    };

    private static readonly Dictionary<char, string> CodabarStartStop = new()
    {
        ['A'] = "nnwwnwn", ['B'] = "nwnwnnw", ['C'] = "nnnwnww", ['D'] = "nnnwwwn",
    };

    internal static IReadOnlyList<string> CodabarPatternsForTests => CodabarPatterns.Concat(CodabarStartStop.Values).ToList();

    /// <summary>Codabar with the given start and stop letters (A to D).</summary>
    public static LinearSymbol EncodeCodabar(string data, char start, char stop, BarDefaults by)
    {
        if (data.Length == 0) throw new BarcodeDataException(Text.Get("Barcode_NoData"));
        if (!CodabarStartStop.TryGetValue(start, out var startPattern) || !CodabarStartStop.TryGetValue(stop, out var stopPattern))
            throw new BarcodeDataException(Text.Get("Barcode_Codabar_StartStop"));
        foreach (var c in data)
            if (CodabarChars.IndexOf(c) < 0)
                throw new BarcodeDataException(Text.Get("Barcode_Codabar_BadChar", c));

        var runs = new RunList().AddPattern(startPattern, by.Narrow, by.Wide);
        foreach (var c in data)
        {
            runs.Add(by.Narrow); // gap between characters
            runs.AddPattern(CodabarPatterns[CodabarChars.IndexOf(c)], by.Narrow, by.Wide);
        }
        runs.Add(by.Narrow);
        runs.AddPattern(stopPattern, by.Narrow, by.Wide);
        return new LinearSymbol(runs.ToArray(), data);
    }

    /// <summary>Factory entry for <c>^BKo,e,h,f,g,k,l</c>.</summary>
    public static BarcodeField BuildCodabar(BarcodeArgs a, string data) =>
        new LinearField(
            EncodeCodabar(data, a.Letter(5, 'A'), a.Letter(6, 'A'), a.By),
            new BarcodeLook(a.Height(2), a.Flag(3, true), a.Flag(4, false)),
            a.By.Narrow, a.Orientation);

    // ---- Code 11 --------------------------------------------------------------------------------------

    private const string Code11Chars = "0123456789-";

    /// <summary>Five elements (bar, space, bar, space, bar) of each Code 11 character, then the start/stop pattern last.</summary>
    private static readonly string[] Code11Patterns =
        { "nnnnw", "wnnnw", "nwnnw", "wwnnn", "nnwnw", "wnwnn", "nwwnn", "nnnww", "wnnwn", "wnnnn", "nnwnn", "nnwwn" };

    internal static IReadOnlyList<string> Code11PatternsForTests => Code11Patterns;

    /// <summary>Code 11 with one (C) or two (C and K) check digits.</summary>
    public static LinearSymbol EncodeCode11(string data, bool oneCheck, BarDefaults by)
    {
        if (data.Length == 0) throw new BarcodeDataException(Text.Get("Barcode_NoData"));
        var (c, k) = CheckDigits.Code11(data);   // throws for characters outside 0-9 and '-'
        var text = data + Code11Chars[c] + (oneCheck ? "" : Code11Chars[k].ToString());

        var runs = new RunList().AddPattern(Code11Patterns[11], by.Narrow, by.Wide);
        foreach (var ch in text)
        {
            runs.Add(by.Narrow);
            runs.AddPattern(Code11Patterns[Code11Chars.IndexOf(ch)], by.Narrow, by.Wide);
        }
        runs.Add(by.Narrow);
        runs.AddPattern(Code11Patterns[11], by.Narrow, by.Wide);
        return new LinearSymbol(runs.ToArray(), text);
    }

    /// <summary>Factory entry for <c>^B1o,e,h,f,g</c> (e = Y: one check digit, N: two).</summary>
    public static BarcodeField BuildCode11(BarcodeArgs a, string data) =>
        new LinearField(
            EncodeCode11(data, a.Flag(1, false), a.By),
            new BarcodeLook(a.Height(2), a.Flag(3, true), a.Flag(4, false)),
            a.By.Narrow, a.Orientation);

    // ---- MSI ---------------------------------------------------------------------------------------------

    /// <summary>
    /// MSI writes every digit as four bits, most significant first. A 1 bit is a wide bar and a narrow space,
    /// a 0 bit a narrow bar and a wide space. The symbol starts with a 1 bit pair and stops with 'nwn'.
    /// </summary>
    public static LinearSymbol EncodeMsi(string data, char checkMode, bool printChecks, BarDefaults by)
    {
        var d = DigitsOnly(data);
        var withChecks = checkMode switch
        {
            'A' => d,
            'C' => Mod10Twice(d),
            'D' => Mod11ThenMod10(d),
            'B' => d + CheckDigits.Mod10Ibm(d),
            // An unknown letter must not quietly become another check scheme: the printed code would not match what the label asked for.
            _ => throw new BarcodeDataException(Text.Get("Barcode_Msi_BadCheck", checkMode)),
        };

        var runs = new RunList().AddPattern("wn", by.Narrow, by.Wide);
        foreach (var ch in withChecks)
        {
            var digit = ch - '0';
            for (var bit = 3; bit >= 0; bit--)
                runs.AddPattern(((digit >> bit) & 1) == 1 ? "wn" : "nw", by.Narrow, by.Wide);
        }
        runs.AddPattern("nwn", by.Narrow, by.Wide);
        return new LinearSymbol(runs.ToArray(), printChecks ? withChecks : d);
    }

    private static string Mod10Twice(string d)
    {
        var once = d + CheckDigits.Mod10Ibm(d);
        return once + CheckDigits.Mod10Ibm(once);
    }

    private static string Mod11ThenMod10(string d)
    {
        var withMod11 = d + CheckDigits.Mod11Ibm(d); // a value of 10 is written as the two digits "10"
        return withMod11 + CheckDigits.Mod10Ibm(withMod11);
    }

    /// <summary>Factory entry for <c>^BMo,e,h,f,g,e2</c>.</summary>
    public static BarcodeField BuildMsi(BarcodeArgs a, string data) =>
        new LinearField(
            EncodeMsi(data, a.Letter(1, 'B'), a.Flag(5, false), a.By),
            new BarcodeLook(a.Height(2), a.Flag(3, true), a.Flag(4, false)),
            a.By.Narrow, a.Orientation);
}
