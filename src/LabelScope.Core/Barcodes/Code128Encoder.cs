namespace LabelScope.Core.Barcodes;

/// <summary>Code 128 encoder following the ZPL rules for <c>^BC</c> (subsets A, B and C, invocation codes starting with '>').</summary>
internal static class Code128Encoder
{
    private const int StartA = 103, StartB = 104, StartC = 105, Stop = 106, Fnc1 = 102, ShiftValue = 98;

    /// <summary>
    /// Bar and space widths of all 107 symbol characters in module units, bar first (values 0 to 105 have six
    /// elements, the stop character 106 has seven). From ISO/IEC 15417; the test checks that every entry adds up to 11 modules.
    /// </summary>
    private static readonly string[] Patterns =
    {
        "212222", "222122", "222221", "121223", "121322", "131222", "122213", "122312", "132212", "221213",
        "221312", "231212", "112232", "122132", "122231", "113222", "123122", "123221", "223211", "221132",
        "221231", "213212", "223112", "312131", "311222", "321122", "321221", "312212", "322112", "322211",
        "212123", "212321", "232121", "111323", "131123", "131321", "112313", "132113", "132311", "211313",
        "231113", "231311", "112133", "112331", "132131", "113123", "113321", "133121", "313121", "211331",
        "231131", "213113", "213311", "213131", "311123", "311321", "331121", "312113", "312311", "332111",
        "314111", "221411", "431111", "111224", "111422", "121124", "121421", "141122", "141221", "112214",
        "112412", "122114", "122411", "142112", "142211", "241211", "221114", "413111", "241112", "134111",
        "111242", "121142", "121241", "114212", "124112", "124211", "411212", "421112", "421211", "212141",
        "214121", "412121", "111143", "111341", "131141", "114113", "114311", "411113", "411311", "113141",
        "114131", "311141", "411131", "211412", "211214", "211232", "2331112",
    };

    /// <summary>Exposes the table to the consistency test only.</summary>
    internal static string[] PatternsForTests => Patterns;

    /// <summary>Start code letters: <c>&gt;9</c> A, <c>&gt;:</c> B, <c>&gt;;</c> C.</summary>
    private static readonly Dictionary<char, char> StartCodes = new() { ['9'] = 'A', [':'] = 'B', [';'] = 'C' };

    /// <summary>Mid-data subset switches: <c>&gt;5</c> to C, <c>&gt;6</c> to B, <c>&gt;7</c> to A.</summary>
    private static readonly Dictionary<char, char> SwitchCodes = new() { ['5'] = 'C', ['6'] = 'B', ['7'] = 'A' };

    /// <summary>
    /// Further codes from Zebra's invocation table (docs.zebra.com, ^BC page) with their Code 128 symbol value:
    /// <c>&gt;0</c> a literal '>', <c>&gt;=</c> '~', <c>&gt;1</c> DEL, <c>&gt;2</c> FNC3, <c>&gt;3</c> FNC2.
    /// <c>&gt;4</c> (SHIFT) needs special handling and is done in <see cref="Emit"/>. Values below 96 are
    /// ordinary characters and only exist in subsets A and B.
    /// </summary>
    private static readonly Dictionary<char, int> ExtraInvocations = new()
    {
        ['0'] = 30, ['='] = 94, ['1'] = 95, ['2'] = 96, ['3'] = 97,
    };

    /// <summary>One piece of data after invocation codes were read: a character, FNC1, a subset switch, a shift or a raw symbol value.</summary>
    internal readonly record struct Item(char Kind, char Value)
    {
        public static Item Char(char c) => new('c', c);
        public static Item Function1() => new('f', '\0');
        public static Item Switch(char subset) => new('s', subset);
        public static Item Raw(int symbolValue) => new('v', (char)symbolValue);
        public static Item Shift() => new('h', '\0');
    }

    /// <summary>Splits the data into items and finds the start subset (B unless the data begins with a start code).</summary>
    internal static (char Start, List<Item> Items) ParseInvocations(string data)
    {
        var items = new List<Item>();
        var start = 'B';
        var i = 0;
        if (data.Length >= 2 && data[0] == '>' && StartCodes.TryGetValue(data[1], out var s))
        {
            start = s;
            i = 2;
        }

        while (i < data.Length)
        {
            var c = data[i];
            if (c != '>')
            {
                items.Add(Item.Char(c));
                i++;
                continue;
            }
            if (i + 1 >= data.Length)
                throw new BarcodeDataException("The data ends with '>', which starts a Code 128 special code. Add the code character after it, or remove the '>'.");

            var code = data[i + 1];
            i += 2;
            if (StartCodes.TryGetValue(code, out var startSubset)) items.Add(Item.Switch(startSubset)); // start codes inside the data switch subset
            else if (SwitchCodes.TryGetValue(code, out var subset)) items.Add(Item.Switch(subset));
            else if (code == '8') items.Add(Item.Function1());
            else if (code == '4') items.Add(Item.Shift());
            else if (ExtraInvocations.TryGetValue(code, out var symbol)) items.Add(Item.Raw(symbol));
            else throw new BarcodeDataException($"The Code 128 special code '>{code}' is not a Code 128 special code. Remove it, or write >0 for a literal '>'.");
        }
        return (start, items);
    }

    /// <summary>Turns items into symbol character values: the start character first, no check or stop yet.</summary>
    internal static List<int> Emit(char start, IEnumerable<Item> items)
    {
        var values = new List<int> { start switch { 'A' => StartA, 'C' => StartC, _ => StartB } };
        var subset = start;
        var pending = -1; // first digit of a subset C pair
        var shifted = false; // SHIFT: the next single character comes from the other of A and B

        foreach (var item in items)
        {
            // A half pair must be completed by the very next digit.
            if (pending >= 0 && !(item.Kind == 'c' && subset == 'C' && char.IsAsciiDigit(item.Value)))
                throw new BarcodeDataException("In Code 128 subset C digits come in pairs, but a digit has no partner. Add a digit or switch subset before it.");

            // SHIFT applies to the one character right after it; anything else there is a mistake in the data.
            if (shifted && item.Kind != 'c')
                throw new BarcodeDataException("The Code 128 shift code >4 must be followed directly by a character. Put the character right after >4.");

            switch (item.Kind)
            {
                case 's':
                    if (item.Value != subset)
                    {
                        values.Add(item.Value switch { 'A' => 101, 'B' => 100, _ => 99 });
                        subset = item.Value;
                    }
                    shifted = false;
                    break;
                case 'f': values.Add(Fnc1); break;
                case 'h':
                    if (subset == 'C')
                        throw new BarcodeDataException("The Code 128 shift code >4 only works in subset A or B. Switch to one of them first.");
                    values.Add(ShiftValue);
                    shifted = true;
                    break;
                case 'v':
                    // Values 30 to 97 here are characters (>, ~, DEL) or FNC2/FNC3. Subset C codes 96 and 97 are digit
                    // pairs, so none of them may be written there or they would silently turn into digits.
                    if (subset == 'C')
                        throw new BarcodeDataException("This Code 128 special code cannot be used in subset C, which holds digit pairs only. Use >6 to switch to subset B first.");
                    // In subset A the values 94 and 95 are the control characters RS and US, not '~' and DEL.
                    if (subset == 'A' && item.Value is (char)94 or (char)95)
                        throw new BarcodeDataException("'~' and DEL exist only in subset B; start the data with >: or use >6 to switch to subset B before this code.");
                    values.Add(item.Value);
                    break;
                default:
                    EmitCharacter(values, shifted ? (subset == 'A' ? 'B' : 'A') : subset, item.Value, ref pending);
                    shifted = false;
                    break;
            }
        }
        if (pending >= 0)
            throw new BarcodeDataException("In Code 128 subset C digits come in pairs, but the last digit has no partner.");
        if (shifted)
            throw new BarcodeDataException("The Code 128 shift code >4 must be followed directly by a character. Put the character right after >4.");
        return values;
    }

    private static void EmitCharacter(List<int> values, char subset, char c, ref int pending)
    {
        switch (subset)
        {
            case 'C':
                if (!char.IsAsciiDigit(c))
                    throw new BarcodeDataException($"Code 128 subset C holds digits only, but the data contains {Describe(c)}. Use >6 to switch to subset B first.");
                if (pending < 0) pending = c - '0';
                else { values.Add(pending * 10 + (c - '0')); pending = -1; }
                break;
            case 'A':
                if (c is >= ' ' and <= '_') values.Add(c - 32);
                else if (c < ' ') values.Add(c + 64);
                else throw new BarcodeDataException($"{Describe(c)} cannot be written in Code 128 subset A. Use >6 to switch to subset B before it.");
                break;
            default:
                if (c is >= ' ' and <= (char)127) values.Add(c - 32);
                else throw new BarcodeDataException($"{Describe(c)} cannot be written in Code 128 subset B. Use >7 to switch to subset A before it.");
                break;
        }
    }

    private static string Describe(char c) => c < ' ' ? $"the control character 0x{(int)c:X2}" : $"the character '{c}'";

    /// <summary>Appends the Mod 103 check character and the stop character.</summary>
    internal static int[] Finish(List<int> values)
    {
        var sum = values[0];
        // Position weights start at 1 after the start character. The sum is reduced modulo 103 at every step (the result
        // is mathematically identical) so very long hostile data can never overflow the int.
        for (var i = 1; i < values.Count; i++) sum = (sum + values[i] * (i % 103)) % 103;
        values.Add(sum % 103);
        values.Add(Stop);
        return values.ToArray();
    }

    /// <summary>All symbol character values (start, data, check, stop) for <paramref name="data"/>.</summary>
    internal static int[] Values(string data, char mode, bool uccCheck) => Prepare(data, mode, uccCheck).Values;

    // ---- automatic packing (mode A, and the back end of mode D) ------------------------------

    /// <summary>
    /// Chooses subsets for plain text: C for runs of four or more digits, A for control characters, B otherwise.
    /// Returns the start subset and the items with the needed switches inserted.
    /// </summary>
    internal static (char Start, List<Item> Items) Optimize(IReadOnlyList<Item> source)
    {
        var n = source.Count;

        // Number of digit characters in a row starting at 'from' (FNC1 and other items end a run).
        int DigitRun(int from)
        {
            var k = 0;
            while (from + k < n && source[from + k].Kind == 'c' && char.IsAsciiDigit(source[from + k].Value)) k++;
            return k;
        }

        // The start subset looks past leading FNC1 symbols (GS1-128 begins with one).
        var first = 0;
        while (first < n && source[first].Kind == 'f') first++;
        var run0 = DigitRun(first);
        char subset;
        if (run0 >= 4 || (run0 >= 2 && run0 == n - first)) subset = 'C';
        else if (first < n && source[first].Kind == 'c' && source[first].Value < ' ') subset = 'A';
        else subset = 'B';
        var start = subset;

        var items = new List<Item>();
        var i = 0;
        while (i < n)
        {
            var it = source[i];
            if (it.Kind != 'c') { items.Add(it); i++; continue; } // FNC1 and raw symbols pass through

            var run = DigitRun(i);
            if (subset == 'C')
            {
                if (run >= 2)
                {
                    for (var k = 0; k < run / 2 * 2; k++) items.Add(source[i + k]);
                    i += run / 2 * 2;
                }
                else
                {
                    // A lone digit or a non-digit: leave subset C.
                    var target = it.Value < ' ' ? 'A' : 'B';
                    items.Add(Item.Switch(target));
                    subset = target;
                }
                continue;
            }

            if (run >= 4)
            {
                // An odd run keeps its first digit here so the rest forms whole pairs for subset C.
                if (run % 2 == 1) { items.Add(it); i++; }
                items.Add(Item.Switch('C'));
                subset = 'C';
                continue;
            }

            // Control characters only exist in A; lowercase, '~' and DEL only in B. Anything else fits both.
            var need = it.Value < ' ' ? 'A' : it.Value > '_' ? 'B' : subset;
            if (need != subset) { items.Add(Item.Switch(need)); subset = need; }
            items.Add(it);
            i++;
        }
        return (start, items);
    }

    // ---- UCC Case mode (U) ---------------------------------------------------------------------

    /// <summary>Mode U: 19 digits (cut or zero padded) plus a Mod 10 digit, written as Start C, FNC1 and ten pairs.</summary>
    private static (char Start, List<Item> Items, string Text) UccCase(string data)
    {
        var digits = new string(data.Where(c => c != ' ').ToArray());
        if (digits.Any(c => !char.IsAsciiDigit(c)))
            throw new BarcodeDataException("UCC case mode (m = U) takes digits only. Remove the letters and special codes from the data.");

        digits = digits.Length > 19 ? digits[..19] : digits.PadRight(19, '0');
        digits += (char)('0' + CheckDigits.Mod10(digits));

        var items = new List<Item> { Item.Function1() };
        items.AddRange(digits.Select(Item.Char));
        return ('C', items, digits);
    }

    // ---- UCC/EAN mode (D) ----------------------------------------------------------------------

    /// <summary>Application identifiers (first two digits) whose value has a fixed length, so no FNC1 follows them.</summary>
    private static readonly Dictionary<string, int> FixedLengthAi = BuildFixedLengthAi();

    private static Dictionary<string, int> BuildFixedLengthAi()
    {
        var d = new Dictionary<string, int> { ["00"] = 18, ["01"] = 14, ["02"] = 14, ["20"] = 2, ["41"] = 13 };
        for (var ai = 11; ai <= 19; ai++) d[ai.ToString()] = 6;       // dates
        for (var ai = 31; ai <= 36; ai++) d[ai.ToString()] = 6;       // measures
        return d;
    }

    /// <summary>Turns <c>(AI)value(AI)value</c> data (or plain digits) into items with FNC1 in the places GS1-128 needs it.</summary>
    private static List<Item> Gs1Items(string data)
    {
        var items = new List<Item> { Item.Function1() };

        // Plain digits (no parentheses) are taken as already formatted.
        if (!data.Contains('('))
        {
            items.AddRange(data.Where(c => c != ' ').Select(Item.Char));
            return items;
        }

        var groups = new List<(string Ai, string Value)>();
        var i = 0;
        while (i < data.Length)
        {
            if (data[i] == ' ') { i++; continue; }
            var close = data[i] == '(' ? data.IndexOf(')', i) : -1;
            if (close < 0)
                throw new BarcodeDataException("UCC/EAN mode (m = D) needs data like (01)12345678901231(10)LOT7: each application identifier in parentheses, then its value.");
            var ai = data[(i + 1)..close];
            if (ai.Length is < 2 or > 4 || ai.Any(c => !char.IsAsciiDigit(c)))
                throw new BarcodeDataException($"'({ai})' is not an application identifier: it must be two to four digits.");
            var next = data.IndexOf('(', close + 1);
            // Spaces are only layout in the data; they are not part of a GS1-128 value.
            var value = new string((next < 0 ? data[(close + 1)..] : data[(close + 1)..next]).Where(c => c != ' ').ToArray());
            if (value.Length == 0)
                throw new BarcodeDataException($"The application identifier ({ai}) has no value.");
            groups.Add((ai, value));
            i = next < 0 ? data.Length : next;
        }

        for (var g = 0; g < groups.Count; g++)
        {
            var (ai, value) = groups[g];
            // The printer computes the missing check digit of the identification keys.
            if ((ai == "00" && value.Length == 17) || (ai is "01" or "02" && value.Length == 13))
                value += (char)('0' + CheckDigits.Mod10(value));

            items.AddRange(ai.Select(Item.Char));
            items.AddRange(value.Select(Item.Char));

            // The first two digits decide (310x to 369x measures, 410 to 417 locations, 11 to 20 dates and so on).
            var fixedLength = FixedLengthAi.TryGetValue(ai[..2], out var len) && value.Length == len;
            if (g < groups.Count - 1 && !fixedLength) items.Add(Item.Function1());
        }
        return items;
    }

    // ---- shared preparation --------------------------------------------------------------------

    private static (int[] Values, string Text) Prepare(string data, char mode, bool uccCheck)
    {
        char start;
        List<Item> items;
        string? text = null;

        switch (mode)
        {
            case 'U':
                (start, items, text) = UccCase(data);
                break;

            case 'D':
                (start, items) = Optimize(Gs1Items(data));
                text = data;
                break;

            case 'A':
            {
                // Automatic mode picks subsets itself, so subset and shift codes are dropped and FNC1 is kept.
                // >0 and >= stand for the literal '>' and '~'; they become plain characters so no data is lost.
                var parsed = ParseInvocations(data).Items
                    .Where(i => i.Kind is 'c' or 'f' || (i.Kind == 'v' && i.Value is (char)30 or (char)94))
                    .Select(i => i.Kind != 'v' ? i : Item.Char(i.Value == 30 ? '>' : '~'))
                    .ToList();
                AppendUccCheck(parsed, uccCheck);
                (start, items) = Optimize(parsed);
                break;
            }

            default: // 'N' and any unknown letter
                (start, items) = ParseInvocations(data);
                AppendUccCheck(items, uccCheck);
                break;
        }

        // The human-readable line shows literal '>' (>0) and '~' (>=) too; DEL, FNC and SHIFT have nothing to print.
        text ??= string.Concat(items.Select(i => i.Kind switch
        {
            'c' => i.Value.ToString(),
            'v' when i.Value == 30 => ">",
            'v' when i.Value == 94 => "~",
            _ => string.Empty,
        }));
        return (Finish(Emit(start, items)), text);
    }

    /// <summary>e = Y: a Mod 10 check digit is added to the data before the Mod 103 check is computed.</summary>
    private static void AppendUccCheck(List<Item> items, bool uccCheck)
    {
        if (!uccCheck) return;
        var digits = string.Concat(items.Where(i => i.Kind == 'c').Select(i => i.Value));
        items.Add(Item.Char((char)('0' + CheckDigits.Mod10(digits))));
    }

    /// <summary>Encodes <paramref name="data"/> as a Code 128 symbol with the bar widths of <paramref name="by"/>.</summary>
    /// <exception cref="BarcodeDataException">The data cannot be written in Code 128.</exception>
    public static LinearSymbol Encode(string data, char mode, bool uccCheck, BarDefaults by)
    {
        var (values, text) = Prepare(data, mode, uccCheck);
        var runs = new RunList();
        foreach (var v in values) runs.AddUnits(Patterns[v], by.Narrow);
        return new LinearSymbol(runs.ToArray(), text);
    }

    /// <summary>Factory entry for <c>^BCo,h,f,g,e,m</c>.</summary>
    public static BarcodeField Build(BarcodeArgs a, string data) =>
        new LinearField(
            Encode(data, a.Letter(5, 'N'), a.Flag(4, false), a.By),
            new BarcodeLook(a.Height(1), a.Flag(2, true), a.Flag(3, false)),
            a.By.Narrow,
            a.Orientation);
}
