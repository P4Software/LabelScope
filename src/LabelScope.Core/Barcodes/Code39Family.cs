namespace LabelScope.Core.Barcodes;

/// <summary>Code 39 (<c>^B3</c>), LOGMARS (<c>^BL</c>) and Code 93 (<c>^BA</c>) encoders.</summary>
internal static class Code39Family
{
    /// <summary>
    /// Element widths of the 43 Code 39 characters in the order of <see cref="CheckDigits.Code39Set"/>: bar, space,
    /// bar ... ('n' narrow, 'w' wide). From ANSI MH10.8M; the test checks the 2 + 1 (or 0 + 3) wide-element rule.
    /// </summary>
    private static readonly string[] Code39Patterns =
    {
        "nnnwwnwnn", "wnnwnnnnw", "nnwwnnnnw", "wnwwnnnnn", "nnnwwnnnw", "wnnwwnnnn", "nnwwwnnnn", "nnnwnnwnw", "wnnwnnwnn", "nnwwnnwnn",
        "wnnnnwnnw", "nnwnnwnnw", "wnwnnwnnn", "nnnnwwnnw", "wnnnwwnnn", "nnwnwwnnn", "nnnnnwwnw", "wnnnnwwnn", "nnwnnwwnn", "nnnnwwwnn",
        "wnnnnnnww", "nnwnnnnww", "wnwnnnnwn", "nnnnwnnww", "wnnnwnnwn", "nnwnwnnwn", "nnnnnnwww", "wnnnnnwwn", "nnwnnnwwn", "nnnnwnwwn",
        "wwnnnnnnw", "nwwnnnnnw", "wwwnnnnnn", "nwnnwnnnw", "wwnnwnnnn", "nwwnwnnnn",
        "nwnnnnwnw", "wwnnnnwnn", "nwwnnnwnn", "nwnwnwnnn", "nwnwnnnwn", "nwnnnwnwn", "nnnwnwnwn",
    };

    /// <summary>The start/stop character '*'.</summary>
    private const string StarPattern = "nwnnwnwnn";

    /// <summary>
    /// Widths in modules of the 47 Code 93 characters: the 43 of <see cref="CheckDigits.Code39Set"/> in the same order,
    /// then the shift characters ($), (%), (/), (+). From ISO/IEC 15416 / AIM USS-93; the test checks nine modules each.
    /// </summary>
    private static readonly string[] Code93Patterns =
    {
        "131112", "111213", "111312", "111411", "121113", "121212", "121311", "111114", "131211", "141111",
        "211113", "211212", "211311", "221112", "221211", "231111", "112113", "112212", "112311", "122112",
        "132111", "111123", "111222", "111321", "121122", "131121", "212112", "212211", "211122", "211221",
        "221121", "222111", "112122", "112221", "122121", "123111",
        "121131", "311112", "311211", "321111", "112131", "113121", "211131",
        "121221", "312111", "311121", "122211",
    };

    /// <summary>Code 93 start and stop character.</summary>
    private const string Code93StartStop = "111141";

    internal static string[] Code39PatternsForTests => Code39Patterns;
    internal static string StarPatternForTests => StarPattern;
    internal static string[] Code93PatternsForTests => Code93Patterns;

    // ---- Code 39 ---------------------------------------------------------------------------------

    /// <summary>Encodes Code 39; <paramref name="mod43"/> adds the check character, <paramref name="upperCase"/> converts lowercase letters (LOGMARS).</summary>
    /// <exception cref="BarcodeDataException">The data contains a character Code 39 cannot hold.</exception>
    public static LinearSymbol EncodeCode39(string data, bool mod43, bool upperCase, BarDefaults by)
    {
        if (upperCase) data = data.ToUpperInvariant();
        if (data.Length == 0) throw new BarcodeDataException(Text.Get("Barcode_NoData"));

        foreach (var c in data)
        {
            if (c == '*')
                throw new BarcodeDataException(Text.Get("Barcode_Code39_StarInData"));
            if (CheckDigits.Code39Set.IndexOf(c) < 0)
                throw new BarcodeDataException(Text.Get("Barcode_Code39_BadChar", c));
        }

        var full = mod43 ? data + CheckDigits.Mod43(data) : data;
        var runs = new RunList();
        runs.AddPattern(StarPattern, by.Narrow, by.Wide);
        foreach (var c in full)
        {
            runs.Add(by.Narrow); // the gap between characters is one narrow space
            runs.AddPattern(Code39Patterns[CheckDigits.Code39Set.IndexOf(c)], by.Narrow, by.Wide);
        }
        runs.Add(by.Narrow);
        runs.AddPattern(StarPattern, by.Narrow, by.Wide);
        return new LinearSymbol(runs.ToArray(), "*" + full + "*");
    }

    /// <summary>Factory entry for <c>^B3o,e,h,f,g</c>.</summary>
    public static BarcodeField BuildCode39(BarcodeArgs a, string data) =>
        new LinearField(
            EncodeCode39(data, a.Flag(1, false), upperCase: false, a.By),
            new BarcodeLook(a.Height(2), a.Flag(3, true), a.Flag(4, false)),
            a.By.Narrow,
            a.Orientation);

    /// <summary>Factory entry for <c>^BLo,h,g</c> (LOGMARS).</summary>
    public static BarcodeField BuildLogmars(BarcodeArgs a, string data) =>
        new LinearField(
            EncodeCode39(data, mod43: true, upperCase: true, a.By),
            new BarcodeLook(a.Height(1), ShowText: true, TextAbove: a.Flag(2, false)),
            a.By.Narrow,
            a.Orientation);

    // ---- Code 93 ---------------------------------------------------------------------------------

    /// <summary>Value 0 to 46 of a data character; the shift characters are typed as &amp; ' ( ) in ZPL.</summary>
    private static int Code93Value(char c)
    {
        var v = CheckDigits.Code39Set.IndexOf(c);
        if (v >= 0) return v;
        // A typographic quote is deliberately not accepted as the % shift: it is far more likely a pasted
        // quotation mark than a request for a shift, and guessing would print the wrong data.
        return c switch
        {
            '&' => 43,
            '\'' => 44,
            '(' => 45,
            ')' => 46,
            _ => throw new BarcodeDataException(Text.Get("Barcode_Code93_BadChar", c)),
        };
    }

    private static char Code93Char(int v) => v < 43 ? CheckDigits.Code39Set[v] : "&'()"[v - 43];

    /// <summary>Encodes Code 93 with its two check characters; <paramref name="printCheck"/> puts them in the interpretation line.</summary>
    /// <exception cref="BarcodeDataException">The data contains a character Code 93 cannot hold.</exception>
    public static LinearSymbol EncodeCode93(string data, bool printCheck, BarDefaults by)
    {
        if (data.Length == 0) throw new BarcodeDataException(Text.Get("Barcode_NoData"));
        var values = data.Select(Code93Value).ToArray();
        var (c, k) = CheckDigits.Code93(values);

        var runs = new RunList().AddUnits(Code93StartStop, by.Narrow);
        foreach (var v in values) runs.AddUnits(Code93Patterns[v], by.Narrow);
        runs.AddUnits(Code93Patterns[c], by.Narrow).AddUnits(Code93Patterns[k], by.Narrow);
        runs.AddUnits(Code93StartStop, by.Narrow);
        runs.Add(by.Narrow); // termination bar

        return new LinearSymbol(runs.ToArray(), data + (printCheck ? $"{Code93Char(c)}{Code93Char(k)}" : ""));
    }

    /// <summary>Factory entry for <c>^BAo,h,f,g,e</c>.</summary>
    public static BarcodeField BuildCode93(BarcodeArgs a, string data) =>
        new LinearField(
            EncodeCode93(data, a.Flag(4, false), a.By),
            new BarcodeLook(a.Height(1), a.Flag(2, true), a.Flag(3, false)),
            a.By.Narrow,
            a.Orientation);
}
