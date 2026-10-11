using System.Buffers;
using System.Globalization;
using System.Text;
using LabelScope.Core.Barcodes;
using LabelScope.Core.Memory;

namespace LabelScope.Core.Rendering;

/// <summary>
/// The character sets of ^CI and how a printer turns the bytes of a text field into characters through them.
/// </summary>
/// <remarks>
/// <para>
/// LabelScope reads every job as UTF-8 (Windows-1252 when the bytes are not valid UTF-8) before any ZPL is seen, so a
/// field's text is already characters when ^CI applies. The bytes a printer received are recovered by encoding the text
/// again with the encoding it was read with (<see cref="RenderOptions.TextReadAsWindows1252"/>); those bytes then go
/// through the set, as on a printer. ^FH escapes are bytes already and join the same byte stream (see
/// <see cref="CharacterDecoder.DecodeField"/>).
/// </para>
/// <para>
/// Only text fields are decoded this way. A barcode encodes the bytes it receives, and the ^CI table only chooses the
/// glyphs a font prints: Labelary draws the same Code 128 symbol for "[AB]" under ^CI0 and ^CI6. So barcode data keeps
/// the bytes, as before.
/// </para>
/// <para>
/// The tables are facts from the Zebra "ZPL II Programming Guide" (P1134473-11EN), checked cell by cell against its
/// charts (the page is given with each table). Where the guide's chart equals a Microsoft or IBM code page, the table
/// is built from .NET's copy of that code page rather than typed in again.
/// </para>
/// </remarks>
internal static class CharacterSets
{
    /// <summary>^CI13: Zebra code page 850 with a backslash at 0x5C.</summary>
    public const int CodePage850 = 13;

    /// <summary>^CI17: UCS-2 big-endian, replaced by ^CI28-30.</summary>
    public const int Ucs2 = 17;

    /// <summary>^CI28: field data is UTF-8.</summary>
    public const int Utf8 = 28;

    /// <summary>^CI29: field data is UTF-16 big-endian.</summary>
    public const int Utf16BigEndian = 29;

    /// <summary>^CI30: field data is UTF-16 little-endian.</summary>
    public const int Utf16LittleEndian = 30;

    /// <summary>Most remapping pairs one ^CI may carry (the guide: up to 256).</summary>
    public const int MaxRemapPairs = 256;

    /// <summary>
    /// The twelve positions sets 0-12 may change (guide p.159 chart). 0x30 is charted too, but only as a slashed or
    /// plain zero, which is a glyph style of the same digit, so it is left out. 0x24 ($) is not charted and never changes.
    /// </summary>
    private const string NationalPositions = "#@[\\]^`{|}~";

    /// <summary>
    /// What sets 0-13 print at <see cref="NationalPositions"/>, one string per set, read from the "International
    /// Character Sets" chart (guide p.159). A position equal to plain ASCII repeats the ASCII
    /// character. Set 13 is code page 850 itself; it prints a backslash at 0x5C, where sets 0-2 print a cent sign
    /// (Labelary agrees on both).
    /// </summary>
    private static readonly string[] National =
    [
        "#@[¢]^`{|}~",                                    //  0 USA 1: ¢
        "#@⅓¢⅔^`¼½¾~",           //  1 USA 2: ⅓ ¢ ⅔ ¼ ½ ¾
        "£@[¢]^`{|}~",                               //  2 UK: £ ¢
        "ƒ§[Ĳ]^`{ĳ}~",                     //  3 Holland: ƒ § Ĳ ĳ
        "#@ÆØÅ^`æøå~",           //  4 Denmark/Norway: Æ Ø Å æ ø å
        "ÜÉÄÖÅÜéäöåü", //  5 Sweden/Finland: Ü É Ä Ö Å Ü é ä ö å ü
        "#§ÄÖÜ^`äöüß", //  6 Germany: § Ä Ö Ü ä ö ü ß
        "£à[ç]^`é|ùè",           //  7 France 1: £ à ç é ù è
        "#àâçêîôéùèû", //  8 France 2: à â ç ê î ô é ù è û
        "£§[çé^ùàòèì", //  9 Italy: £ § ç é ù à ò è ì
        "#§¡Ñ¿^`{ñç~",           // 10 Spain: § ¡ Ñ ¿ ñ ç
        "£ÉÄÖÜ^äëïöü", // 11 Miscellaneous: £ É Ä Ö Ü ä ë ï ö ü
        "#@[¥]^`{|}~",                                    // 12 Japan: ¥
        "#@[\\]^`{|}~",                                        // 13 Zebra code page 850: \
    ];

    // One table per single-byte set, built on first use and then shared read-only (a char[] is never changed after it
    // is built, so concurrent labels can read it).
    private static readonly char[]?[] Tables = new char[37][];
    private static readonly object TablesGate = new();

    // Decoders for a set without remapping, shared by every label that uses the set.
    private static readonly CharacterDecoder?[] PlainDecoders = new CharacterDecoder[37];

    /// <summary>Reads the set number from the first ^CI argument; false when it is not a whole number.</summary>
    public static bool TryParse(string first, out int set)
    {
        set = 0;
        var text = first.Trim();
        // An empty ^CI keeps the power-up default, set 0.
        return text.Length == 0 || int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out set);
    }

    /// <summary>True for ^CI17 and ^CI28-30, whose text is Unicode: only ^FH escape bytes need decoding.</summary>
    public static bool IsUnicode(int set) => set is Ucs2 or (>= Utf8 and <= Utf16LittleEndian);

    /// <summary>True for the sets that read one byte per character: 0-13, 27, 31 and 33-36.</summary>
    public static bool IsSingleByte(int set) => set is (>= 0 and <= 13) or 27 or 31 or (>= 33 and <= 36);

    /// <summary>
    /// True for ^CI14, 15, 16, 24 and 26: a printer converts their bytes through a .DAT table loaded with ^SE, which
    /// LabelScope does not have; the closest Windows encoding stands in (see <see cref="AsianEncoding"/>).
    /// </summary>
    public static bool IsTableDriven(int set) => set is 14 or 15 or 16 or 24 or 26;

    /// <summary>True when <paramref name="set"/> is a ^CI number a Zebra printer knows.</summary>
    public static bool IsKnown(int set) => IsSingleByte(set) || IsUnicode(set) || IsTableDriven(set);

    /// <summary>True for the sets remapping pairs work with: 0-13 (guide p.156-158; its own example uses set 0).</summary>
    public static bool CanRemap(int set) => set is >= 0 and <= 13;

    /// <summary>
    /// The set's name in the current language, as warnings quote it ("code page 850", "Shift-JIS"), or null for a
    /// number that is no character set (18-23, 25, 32, above 36).
    /// </summary>
    public static string? Describe(int set) => set switch
    {
        >= 0 and <= 12 => Text.Get("CharSet_SingleByte"),
        CodePage850 => Text.Get("CharSet_CodePage", 850),
        14 or 15 or 16 or 24 or 26 => AsianName(set),
        Ucs2 => Text.Get("CharSet_Ucs2"),
        27 => Text.Get("CharSet_CodePage", 1252),
        Utf8 => "UTF-8",
        Utf16BigEndian => "UTF-16 BE",
        Utf16LittleEndian => "UTF-16 LE",
        31 => Text.Get("CharSet_CodePage", 1250),
        33 => Text.Get("CharSet_CodePage", 1251),
        34 => Text.Get("CharSet_CodePage", 1253),
        35 => Text.Get("CharSet_CodePage", 1254),
        36 => Text.Get("CharSet_CodePage", 1255),
        _ => null,
    };

    /// <summary>
    /// The encoding a sender must use for <paramref name="set"/>, as the "sent as UTF-8" warning names it: code page
    /// 850 for sets 0-13 (their upper half is code page 850), otherwise <see cref="Describe"/>.
    /// </summary>
    public static string EncodingName(int set) =>
        set is >= 0 and <= 13 ? Text.Get("CharSet_CodePage", 850) : Describe(set) ?? "";

    /// <summary>The Windows encoding that stands in for a table-driven set, by its usual name.</summary>
    private static string AsianName(int set) => set switch
    {
        14 => "GBK (" + Text.Get("CharSet_CodePage", 936) + ")",
        15 => "Shift-JIS (" + Text.Get("CharSet_CodePage", 932) + ")",
        16 => "EUC-JP",
        24 => "Windows-874",
        _ => "GB18030",
    };

    /// <summary>
    /// Reads a ^CI command (^CIa,s1,d1,s2,d2,...) and returns the character setting it makes, or null, with a warning,
    /// when the set number is no character set (the setting in effect then stays). Remapping pairs are kept for sets
    /// 0-13; elsewhere, or when some pairs are not usable, one warning says what was ignored.
    /// </summary>
    /// <remarks>
    /// The arguments are scanned in place and at most 1 + 2 x <see cref="MaxRemapPairs"/> of them are looked at, so a
    /// hostile ^CI with millions of commas costs one pass over the text and never millions of strings.
    /// </remarks>
    public static CharacterSetting? ReadCommand(ZplCommand cmd, List<RenderWarning> warnings)
    {
        var args = cmd.Args;
        var comma = args.IndexOf(',');
        var first = comma < 0 ? args : args[..comma];
        if (!TryParse(first, out var set) || !IsKnown(set))
        {
            // Quoted back to the user, so a hostile megabyte of text is cut to a readable length.
            var shown = first.Trim();
            warnings.Add(new(cmd.Line, Text.Get("Painter_CharSetUnknown", shown.Length > 10 ? shown[..10] + "..." : shown)));
            return null;
        }
        // Only commas and spaces after the set (some programs pad the command) are no remapping at all.
        if (comma < 0 || args.AsSpan(comma + 1).IndexOfAnyExcept(", \t\r\n") < 0)
            return CharacterSetting.Plain(set);

        if (!CanRemap(set))
        {
            warnings.Add(new(cmd.Line, Text.Get("Painter_CharSetRemapNotHere", set)));
            return CharacterSetting.Plain(set);
        }

        var remap = new Dictionary<char, char>();
        var ignored = false;
        var pairs = 0;
        var rest = args.AsSpan(comma + 1);
        var done = false;
        while (!done)
        {
            var source = TakeField(ref rest, ref done);
            var hasDestination = !done;
            var destination = hasDestination ? TakeField(ref rest, ref done) : default;
            // ",," is no pair at all (programs pad the list); it is skipped without a word.
            if (source.Trim().IsEmpty && destination.Trim().IsEmpty) continue;
            if (pairs++ >= MaxRemapPairs)
            {
                // Past 256 pairs nothing more is read: the rest of the list is ignored without parsing it.
                ignored = true;
                break;
            }
            // The guide: each value is 0-255, and the space can never be remapped.
            if (!hasDestination || !TryByte(source, out var s) || !TryByte(destination, out var d) || d == ' ')
            {
                ignored = true;
                continue;
            }
            remap[(char)d] = (char)s;
        }
        if (ignored)
            warnings.Add(new(cmd.Line, Text.Get("Painter_CharSetRemapIgnored", set, MaxRemapPairs)));
        return remap.Count == 0 ? CharacterSetting.Plain(set) : new CharacterSetting(set, remap);
    }

    /// <summary>
    /// Cuts the next comma-separated field off <paramref name="rest"/>; sets <paramref name="done"/> when it was the last.
    /// Works on the text in place, so a long list allocates nothing.
    /// </summary>
    private static ReadOnlySpan<char> TakeField(ref ReadOnlySpan<char> rest, ref bool done)
    {
        var end = rest.IndexOf(',');
        if (end < 0)
        {
            var last = rest;
            rest = default;
            done = true;
            return last;
        }
        var field = rest[..end];
        rest = rest[(end + 1)..];
        return field;
    }

    private static bool TryByte(ReadOnlySpan<char> text, out int value) =>
        int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out value) && value is >= 0 and <= 255;

    /// <summary>
    /// Decodes <paramref name="fieldText"/> (^FD text, already characters) as a printer under <paramref name="set"/>
    /// would: encoded again with the encoding the job was read with, then decoded through the set. Under ^CI17 and
    /// 28-30 the text is returned as it is (see <see cref="CharacterDecoder"/>).
    /// </summary>
    /// <param name="fieldText">The field's text.</param>
    /// <param name="set">The ^CI set.</param>
    /// <param name="readAsWindows1252">True when the job's bytes were read as Windows-1252.</param>
    /// <param name="sentAsUtf8Mojibake">
    /// True when the text came as UTF-8 with characters outside ASCII while the set does not read UTF-8: a printer then
    /// prints two or more wrong characters for each such letter.
    /// </param>
    public static string Decode(string fieldText, int set, bool readAsWindows1252, out bool sentAsUtf8Mojibake) =>
        DecoderFor(CharacterSetting.Plain(set)).DecodeField(fieldText, null, readAsWindows1252, keepLineBreaks: false,
            downloadedFont: false, out sentAsUtf8Mojibake, out _);

    /// <summary>Decodes bytes (^FH escapes) through <paramref name="set"/>.</summary>
    public static string DecodeBytes(byte[] bytes, int set) => DecoderFor(CharacterSetting.Plain(set)).DecodeBytes(bytes);

    /// <summary>The decoder for <paramref name="setting"/>; a set without remapping shares one decoder per set.</summary>
    public static CharacterDecoder DecoderFor(CharacterSetting setting)
    {
        if (setting.Remap.Count > 0 || setting.Set is < 0 or > 36) return new CharacterDecoder(setting);
        return PlainDecoders[setting.Set] ??= new CharacterDecoder(setting);
    }

    /// <summary>
    /// The 256 characters a single-byte set prints for each byte, before remapping; null for other sets.
    /// </summary>
    internal static char[]? TableFor(int set)
    {
        if (!IsSingleByte(set)) return null;
        if (Tables[set] is { } built) return built;
        lock (TablesGate)
        {
            return Tables[set] ??= Build(set);
        }
    }

    private static char[] Build(int set)
    {
        if (set <= CodePage850)
        {
            // Sets 0-13: Zebra code page 850 (guide p.1566-1567) with the national positions of
            // the set on top (guide p.159).
            var table = CodePage850Table();
            var national = National[set];
            for (var i = 0; i < NationalPositions.Length; i++) table[NationalPositions[i]] = national[i];
            return table;
        }
        // The Windows code pages. The guide's charts match Microsoft's tables cell by cell:
        // 1252 p.1570-1571, 1250 p.1568-1569, 1253 p.1572-1573, 1254 p.1574-1575, 1255 p.1576-1577. The guide has no
        // 1251 chart, so Microsoft's 1251 is used as is. For 1255 the chart draws 0xCA blank, while Microsoft's (and
        // .NET's) table puts U+05BA (Hebrew point holam haser for vav) there; Microsoft's table is kept, since a blank
        // chart cell may only mean the font has no glyph to show for a combining point.
        var codePage = set switch { 27 => 1252, 31 => 1250, 33 => 1251, 34 => 1253, 35 => 1254, _ => 1255 };
        return FromCodePage(codePage);
    }

    /// <summary>
    /// Zebra code page 850: IBM code page 850 for 0x80-0xFF (the guide's chart equals it cell by cell, with 0xD5 the
    /// dotless i, so not 858), ASCII for 0x20-0x7E, and two cells where the resident font differs from what .NET
    /// returns: 0x7F is the house sign, and 0x15 holds the euro sign (the guide's remap example ^CI0,21,36 prints € for
    /// $). Other bytes below 0x20 are not charted and stay control characters, which print as spaces.
    /// </summary>
    private static char[] CodePage850Table()
    {
        var table = FromCodePage(850);
        for (var b = 0x20; b < 0x7F; b++) table[b] = (char)b;
        table[0x7F] = '⌂';
        table[0x15] = '€';
        return table;
    }

    private static char[] FromCodePage(int codePage)
    {
        var encoding = CodePage(codePage);
        var bytes = new byte[256];
        for (var i = 0; i < 256; i++) bytes[i] = (byte)i;
        var chars = encoding.GetChars(bytes);
        // Every byte of these code pages decodes to exactly one UTF-16 unit, so the result lines up byte for byte.
        return chars.Length == 256 ? chars : throw new InvalidOperationException($"Code page {codePage} is not single-byte.");
    }

    /// <summary>
    /// A Windows or IBM code page, straight from the code-pages provider. Asking the provider itself, rather than
    /// Encoding.GetEncoding, works whether or not anything registered it in this process. Bytes that do not decode
    /// become "?" instead of throwing, so no field data can make decoding fail.
    /// </summary>
    internal static Encoding CodePage(int codePage) =>
        CodePagesEncodingProvider.Instance.GetEncoding(codePage, EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback)
        ?? throw new InvalidOperationException($"Code page {codePage} is not available.");

    /// <summary>
    /// The Windows encoding that stands in for a table-driven set. The guide names no code page for these; it says the
    /// printer converts through the .DAT table loaded with ^SE (guide p.156-158). Chosen: ^CI15 Shift-JIS (932, the
    /// guide's own description); ^CI16 EUC-JP (51932; the guide says "EUC-JP and EUC-CN", JIS.DAT being the table its
    /// samples load); ^CI26 GB18030 (the guide's example table for ASCII-transparent multibyte); ^CI14 GBK (936), the
    /// most common double-byte table, since the guide leaves the table open; ^CI24 Windows-874 (Thai), the only
    /// single-byte Asian Windows code page.
    /// </summary>
    internal static Encoding AsianEncoding(int set) => CodePage(set switch
    {
        14 => 936,
        15 => 932,
        16 => 51932,
        24 => 874,
        _ => 54936,
    });

    /// <summary>True when <paramref name="text"/> has a character outside 7-bit ASCII.</summary>
    public static bool HasNonAscii(string text) => text.AsSpan().IndexOfAnyExceptInRange('\0', '\u007F') >= 0;
}

/// <summary>
/// Turns a text field's data into the characters a printer prints under one ^CI setting. Built once per setting and
/// then only read, so a decoder for a set without remapping is shared by every label.
/// </summary>
internal sealed class CharacterDecoder
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly Lazy<Encoding> Windows1252 = new(() => CharacterSets.CodePage(1252));

    // Printable ASCII, the characters plain field text is made of.
    private static readonly SearchValues<char> PrintableAscii = SearchValues.Create(
        string.Concat(Enumerable.Range(0x20, 0x5F).Select(c => (char)c)));

    // The table of a single-byte set, with any remapping already applied; null for other sets.
    private readonly char[]? _table;
    // The same for a field drawn in a downloaded font (^A@ or ^CW), where sets 0-2 print a backslash at 0x5C; null when
    // that changes nothing (every set but 0-2).
    private readonly char[]? _downloadedTable;
    // The printable ASCII characters each table prints unchanged. Text made only of them comes out as it went in, so it
    // is returned without encoding or decoding (ZebraDesigner sends a ^CI with every job, mostly with plain text).
    private readonly SearchValues<char> _unchanged, _downloadedUnchanged;
    // The stand-in encoding of a table-driven (Asian) set; null for other sets.
    private readonly Encoding? _asian;

    /// <summary>Builds the decoder for <paramref name="setting"/>; use <see cref="CharacterSets.DecoderFor"/>.</summary>
    internal CharacterDecoder(CharacterSetting setting)
    {
        Set = setting.Set;
        _unchanged = _downloadedUnchanged = PrintableAscii;
        if (CharacterSets.TableFor(Set) is { } table)
        {
            _table = WithRemap(table, setting.Remap);
            _unchanged = Unchanged(_table);
            _downloadedUnchanged = _unchanged;
            if (table[0x5C] == '\u00A2')
            {
                // The guide's code page 850 chart: at 0x5C resident fonts print a cent sign, downloaded fonts a backslash.
                var downloaded = (char[])table.Clone();
                downloaded[0x5C] = '\\';
                _downloadedTable = WithRemap(downloaded, setting.Remap);
                _downloadedUnchanged = Unchanged(_downloadedTable);
            }
        }
        else if (CharacterSets.IsTableDriven(Set))
        {
            // Every stand-in encoding reads bytes below 0x80 as ASCII (^CI26 is ASCII-transparent by the guide).
            _asian = CharacterSets.AsianEncoding(Set);
        }
    }

    /// <summary>
    /// Remapping as a table of its own: byte d prints the glyph the set has at s. Built once from the unchanged table,
    /// so the pairs never chain.
    /// </summary>
    private static char[] WithRemap(char[] table, IReadOnlyDictionary<char, char> remap)
    {
        if (remap.Count == 0) return table;
        var remapped = (char[])table.Clone();
        foreach (var (destination, source) in remap)
        {
            // ReadCommand only makes byte values; a setting built elsewhere is held to them as well.
            if (destination < 256 && source < 256) remapped[destination] = table[source];
        }
        return remapped;
    }

    /// <summary>The printable ASCII characters <paramref name="table"/> prints as themselves.</summary>
    private static SearchValues<char> Unchanged(char[] table) =>
        SearchValues.Create(string.Concat(Enumerable.Range(0x20, 0x5F).Where(c => table[c] == c).Select(c => (char)c)));

    /// <summary>The ^CI set.</summary>
    public int Set { get; }

    /// <summary>True for a single-byte set, which decodes through a table of 256 characters.</summary>
    public bool HasTable => _table is not null;

    /// <summary>True when this set's table (either font variant) prints <paramref name="c"/> for some byte.</summary>
    public bool Prints(char c) =>
        _table is not null && (Array.IndexOf(_table, c) >= 0 || (_downloadedTable is not null && Array.IndexOf(_downloadedTable, c) >= 0));

    /// <summary>
    /// Decodes a text field's raw data, ^FH escapes included, as a printer under this set does.
    /// </summary>
    /// <remarks>
    /// The order: ^FH escapes are found first, on the text as received, so the indicator (often a backslash, which sets
    /// 0-12 print as another letter) is recognised before any table applies. Then, for a byte-based set, the text
    /// between escapes is encoded again with the job's read encoding and joined with the escape bytes into the one byte
    /// stream a printer received, which goes through the set's table once: a Shift-JIS pair split between text and an
    /// escape still decodes. Under ^CI17 and 28-30 the text stays as received (it was already read as Unicode, and
    /// commands that were readable as ASCII mean the stream was not UTF-16) and only escape runs are decoded.
    /// Serial counting and the clock come after this, on characters (see <c>LabelPainter.EndField</c>).
    /// </remarks>
    /// <param name="raw">The field data, escapes still in it.</param>
    /// <param name="hexIndicator">The ^FH indicator, or null without ^FH.</param>
    /// <param name="readAsWindows1252">True when the job's bytes were read as Windows-1252.</param>
    /// <param name="keepLineBreaks">
    /// True for a ^FB field: its "\&amp;" line breaks are kept as written. A printer finds them in the bytes, before the
    /// glyph table; Labelary breaks the line under ^CI0 as well, where 0x5C alone prints a cent sign.
    /// </param>
    /// <param name="downloadedFont">
    /// True when the field is drawn in a downloaded font (^A@ with a file, or a letter ^CW points at a file): under sets
    /// 0-2 it prints a backslash at 0x5C, where the resident fonts print a cent sign.
    /// </param>
    /// <param name="sentAsUtf8">True when the text was UTF-8 with non-ASCII characters while this set does not read UTF-8.</param>
    /// <param name="highBytes">True when bytes above 0x7F went through the set (the only bytes a table-driven set's table decides).</param>
    public string DecodeField(string raw, char? hexIndicator, bool readAsWindows1252, bool keepLineBreaks, bool downloadedFont,
                              out bool sentAsUtf8, out bool highBytes)
    {
        sentAsUtf8 = false;
        highBytes = false;
        if (CharacterSets.IsUnicode(Set)) return FieldData.Decode(raw, hexIndicator, DecodeBytes);

        // Plain text the set prints unchanged needs no work at all: the usual label under ZebraDesigner's ^CI.
        var table = downloadedFont && _downloadedTable is not null ? _downloadedTable : _table;
        var unchanged = downloadedFont ? _downloadedUnchanged : _unchanged;
        if ((hexIndicator is not { } indicator || raw.IndexOf(indicator) < 0) && raw.AsSpan().IndexOfAnyExcept(unchanged) < 0)
            return raw;

        var encoding = readAsWindows1252 ? Windows1252.Value : Utf8;
        var bytes = FieldData.ToBytes(raw, hexIndicator, encoding, out var textHasNonAscii);
        // Text read as Windows-1252 is the sender's own bytes, one per character; only UTF-8 makes several bytes of one letter.
        sentAsUtf8 = textHasNonAscii && !readAsWindows1252;
        highBytes = bytes.AsSpan().IndexOfAnyInRange((byte)0x80, (byte)0xFF) >= 0;
        return table is not null ? FromTable(table, bytes, keepLineBreaks) : DecodeBytes(bytes);
    }

    /// <summary>Decodes bytes through this set: ^FH escape runs under the Unicode sets, or a whole field otherwise.</summary>
    public string DecodeBytes(byte[] bytes)
    {
        if (_table is not null) return FromTable(_table, bytes, keepLineBreaks: false);
        if (_asian is not null) return _asian.GetString(bytes);
        return Set switch
        {
            // ^CI28 reads escapes as before: bytes that are not valid UTF-8 keep one character each.
            CharacterSets.Utf8 => FieldData.Utf8OrLatin1(bytes),
            CharacterSets.Utf16LittleEndian => Utf16(bytes, bigEndian: false),
            // ^CI17 (UCS-2 big-endian) and ^CI29: the guide's ^FH example _00_A1 under ^CI29 prints "¡".
            _ => Utf16(bytes, bigEndian: true),
        };
    }

    private static string FromTable(char[] table, byte[] bytes, bool keepLineBreaks)
    {
        return string.Create(bytes.Length, (bytes, table, keepLineBreaks), static (span, state) =>
        {
            var (b, t, keep) = state;
            for (var i = 0; i < b.Length; i++)
            {
                if (keep && b[i] == 0x5C && i + 1 < b.Length && b[i + 1] == 0x26)
                {
                    span[i] = '\\';
                    span[++i] = '&';
                    continue;
                }
                span[i] = t[b[i]];
            }
        });
    }

    /// <summary>UTF-16 in pairs of bytes; an odd last byte keeps one character, as an incomplete escape run is kept.</summary>
    private static string Utf16(byte[] bytes, bool bigEndian)
    {
        var even = bytes.Length & ~1;
        var encoding = bigEndian ? Encoding.BigEndianUnicode : Encoding.Unicode;
        var text = encoding.GetString(bytes, 0, even);
        return even == bytes.Length ? text : text + (char)bytes[^1];
    }
}
