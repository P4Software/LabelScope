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
    /// escapes that is not valid UTF-8 keeps the Latin-1 reading. Other code pages are not mapped yet.
    /// </summary>
    public static string Decode(string raw, char? hexIndicator, bool utf8 = false)
    {
        if (hexIndicator is null || raw.IndexOf(hexIndicator.Value) < 0) return raw;

        var sb = new StringBuilder(raw.Length);
        // Bytes of consecutive escapes, decoded together under UTF-8 because one character can take several escapes.
        var run = new List<byte>();
        for (var i = 0; i < raw.Length; i++)
        {
            if (raw[i] == hexIndicator && i + 2 < raw.Length && IsHex(raw[i + 1]) && IsHex(raw[i + 2]))
            {
                run.Add((byte)Convert.ToInt32(raw.Substring(i + 1, 2), 16));
                i += 2;
            }
            else
            {
                Flush(sb, run, utf8);
                sb.Append(raw[i]);
            }
        }
        Flush(sb, run, utf8);
        return sb.ToString();
    }

    private static void Flush(StringBuilder sb, List<byte> run, bool utf8)
    {
        if (run.Count == 0) return;
        string? text = null;
        if (utf8)
        {
            try { text = StrictUtf8.GetString(run.ToArray()); }
            catch (DecoderFallbackException) { /* not UTF-8: keep one character per byte, as without ^CI28 */ }
        }
        if (text is not null) sb.Append(text);
        else foreach (var b in run) sb.Append((char)b);
        run.Clear();
    }

    private static bool IsHex(char c) => c is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F');
}
