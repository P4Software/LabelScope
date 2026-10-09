using System.Text;

namespace LabelScope.Core.Barcodes;

/// <summary>Turns the raw text after <c>^FD</c> into the characters the printer would use.</summary>
internal static class FieldData
{
    /// <summary>
    /// Applies <c>^FH</c>: the <paramref name="hexIndicator"/> character followed by two hex digits stands for one
    /// byte. The parser cuts fields at <c>^</c> and <c>~</c>, so this is the only way to put them in data.
    /// An incomplete escape ("_1" at the end, "_ZZ") is kept as written. Bytes map to Latin-1 characters; code
    /// pages are handled by <c>^CI</c> support in plan 4.
    /// </summary>
    public static string Decode(string raw, char? hexIndicator)
    {
        if (hexIndicator is null || raw.IndexOf(hexIndicator.Value) < 0) return raw;

        var sb = new StringBuilder(raw.Length);
        for (var i = 0; i < raw.Length; i++)
        {
            if (raw[i] == hexIndicator && i + 2 < raw.Length && IsHex(raw[i + 1]) && IsHex(raw[i + 2]))
            {
                sb.Append((char)Convert.ToInt32(raw.Substring(i + 1, 2), 16));
                i += 2;
            }
            else sb.Append(raw[i]);
        }
        return sb.ToString();
    }

    private static bool IsHex(char c) => c is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F');
}
