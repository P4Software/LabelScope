using System.Buffers;
using System.Text;

namespace LabelScope.Core.Barcodes;

/// <summary>Turns the raw text after <c>^FD</c> into the characters the printer would use.</summary>
internal static class FieldData
{
    // Strict, so a run of escapes that is not valid UTF-8 is noticed and kept byte for byte instead of becoming U+FFFD.
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Applies <c>^FH</c>: the <paramref name="hexIndicator"/> character followed by two hex digits stands for one
    /// byte. The parser cuts fields at <c>^</c> and <c>~</c>, so this is the only way to put them in data.
    /// An incomplete escape ("_1" at the end, "_ZZ") is kept as written. Bytes map to Latin-1 characters, except under
    /// ^CI28 (<paramref name="utf8"/>): there a printer reads the bytes as UTF-8, so "_C3_A9" is one "é". A run of
    /// escapes that is not valid UTF-8 keeps the Latin-1 reading. This is the reading for barcodes, which encode the
    /// bytes themselves, and for text when no ^CI was ever sent; text under a ^CI goes through
    /// <c>CharacterDecoder.DecodeField</c>.
    /// </summary>
    public static string Decode(string raw, char? hexIndicator, bool utf8 = false) =>
        Decode(raw, hexIndicator, utf8 ? Utf8OrLatin1 : Latin1);

    /// <summary>
    /// Applies <c>^FH</c> with <paramref name="decodeRun"/> turning each run of consecutive escape bytes into text; the
    /// text between escapes is kept as it is. A run is decoded as a whole because one character can take several
    /// escapes (two under UTF-16, up to four under UTF-8).
    /// </summary>
    public static string Decode(string raw, char? hexIndicator, Func<byte[], string> decodeRun)
    {
        if (hexIndicator is not { } indicator || raw.IndexOf(indicator) < 0) return raw;

        var sb = new StringBuilder(raw.Length);
        var run = new List<byte>();
        for (var i = 0; i < raw.Length; i++)
        {
            if (IsEscapeAt(raw, i, indicator))
            {
                run.Add(HexByte(raw, i));
                i += 2;
            }
            else
            {
                Flush(sb, run, decodeRun);
                sb.Append(raw[i]);
            }
        }
        Flush(sb, run, decodeRun);
        return sb.ToString();
    }

    /// <summary>
    /// The bytes a printer received for a field: the text between ^FH escapes encoded with <paramref name="textEncoding"/>
    /// (the encoding the job was read with, so these are the sender's bytes again), and each escape as its one byte, in
    /// order. <paramref name="textHasNonAscii"/> says whether the text itself (not the escapes) went beyond ASCII.
    /// </summary>
    public static byte[] ToBytes(string raw, char? hexIndicator, Encoding textEncoding, out bool textHasNonAscii)
    {
        if (hexIndicator is not { } indicator || raw.IndexOf(indicator) < 0)
        {
            textHasNonAscii = HasNonAscii(raw);
            return textEncoding.GetBytes(raw);
        }

        textHasNonAscii = false;
        var bytes = new ArrayBufferWriter<byte>(raw.Length + 16);
        var textStart = 0;
        for (var i = 0; i < raw.Length; i++)
        {
            if (!IsEscapeAt(raw, i, indicator)) continue;
            textHasNonAscii |= AppendText(bytes, raw.AsSpan(textStart, i - textStart), textEncoding);
            bytes.GetSpan(1)[0] = HexByte(raw, i);
            bytes.Advance(1);
            i += 2;
            textStart = i + 1;
        }
        textHasNonAscii |= AppendText(bytes, raw.AsSpan(textStart), textEncoding);
        return bytes.WrittenSpan.ToArray();
    }

    /// <summary>Encodes a stretch of field text onto <paramref name="bytes"/>; true when it had non-ASCII characters.</summary>
    private static bool AppendText(ArrayBufferWriter<byte> bytes, ReadOnlySpan<char> text, Encoding encoding)
    {
        if (text.IsEmpty) return false;
        var written = encoding.GetBytes(text, bytes.GetSpan(encoding.GetMaxByteCount(text.Length)));
        bytes.Advance(written);
        return text.IndexOfAnyExceptInRange('\0', '\u007F') >= 0;
    }

    private static bool HasNonAscii(string text) => text.AsSpan().IndexOfAnyExceptInRange('\0', '\u007F') >= 0;

    private static bool IsEscapeAt(string raw, int i, char indicator) =>
        raw[i] == indicator && i + 2 < raw.Length && IsHex(raw[i + 1]) && IsHex(raw[i + 2]);

    private static byte HexByte(string raw, int i) => (byte)(HexValue(raw[i + 1]) << 4 | HexValue(raw[i + 2]));

    private static int HexValue(char c) => c <= '9' ? c - '0' : (c | 0x20) - 'a' + 10;

    private static void Flush(StringBuilder sb, List<byte> run, Func<byte[], string> decodeRun)
    {
        if (run.Count == 0) return;
        sb.Append(decodeRun(run.ToArray()));
        run.Clear();
    }

    /// <summary>One character per byte, the reading without a ^CI.</summary>
    private static string Latin1(byte[] bytes) => Encoding.Latin1.GetString(bytes);

    /// <summary>UTF-8 when the bytes are valid UTF-8; otherwise one character per byte, as without ^CI28.</summary>
    internal static string Utf8OrLatin1(byte[] bytes)
    {
        try { return StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException) { return Encoding.Latin1.GetString(bytes); }
    }

    private static bool IsHex(char c) => c is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F');
}
