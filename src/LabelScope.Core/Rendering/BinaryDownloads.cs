using System.Globalization;
using System.Text;
using LabelScope.Core.Listening;

namespace LabelScope.Core.Rendering;

/// <summary>
/// Recognises ^GF and ~DY commands that announce binary data (format B or C). Their bytes may contain '^', '~', CR and
/// LF, which must not be read as commands or line breaks; the header gives the exact byte count, and the guide says a
/// printer ignores command prefixes until that many bytes have arrived.
/// </summary>
internal static class BinaryDownloads
{
    // A header longer than this is not a header: protects against scanning megabytes of text for commas.
    private const int MaxHeaderChars = 256;

    /// <summary>
    /// For the command <paramref name="name"/> whose name ends at <paramref name="afterName"/>, returns true when it
    /// announces binary data, with the index where the data starts and its length in characters. The skip is by
    /// characters, which equals the byte count only when the job was read as Windows-1252 (one character per byte).
    /// Binary data almost always is, because it is not valid UTF-8; a payload that happens to be valid UTF-8 can be
    /// mis-counted until the splitter is byte-aware (planned for a later release).
    /// </summary>
    public static bool TryMeasure(string text, int afterName, string name, out int dataStart, out long dataLength) =>
        TryMeasure(text, afterName, name, ZplPrefixes.Default, out dataStart, out dataLength);

    /// <summary>
    /// As <see cref="TryMeasure(string, int, string, out int, out long)"/>, reading the header with the prefixes and
    /// delimiter in <paramref name="prefixes"/> (after ^CC, ^CT or ^CD); <paramref name="name"/> uses the standard prefixes.
    /// </summary>
    public static bool TryMeasure(string text, int afterName, string name, ZplPrefixes prefixes, out int dataStart, out long dataLength)
    {
        dataStart = 0;
        dataLength = 0;
        // ^GFa,b,c,d,data (format a, byte count b) and ~DYd:f,b,x,t,w,data (format b, total bytes t).
        var (commas, formatIndex, lengthIndex) = name switch
        {
            "^GF" => (4, 0, 1),
            "~DY" => (5, 1, 3),
            _ => (0, 0, 0),
        };
        if (commas == 0) return false;

        var (formatPrefix, controlPrefix, delimiter) = prefixes.OrDefault();
        var fields = new List<string>();
        var current = new StringBuilder();
        var k = afterName;
        for (; k < text.Length && k - afterName < MaxHeaderChars; k++)
        {
            var c = text[k];
            if (c == formatPrefix || c == controlPrefix) return false; // the header ended before all its parameters
            if (c is '\r' or '\n') continue;                            // line breaks are not part of a parameter
            if (c != delimiter) { current.Append(c); continue; }
            fields.Add(current.ToString());
            current.Clear();
            if (fields.Count == commas) break;
        }
        if (fields.Count < commas) return false;

        var format = fields[formatIndex].Trim();
        // Formats B and C both carry raw bytes (C is compressed binary); only the byte count matters here.
        if (format.Length == 0 || char.ToUpperInvariant(format[0]) is not ('B' or 'C')) return false;
        if (!long.TryParse(fields[lengthIndex].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var length)
            || length < 1 || length > ZplStreamSplitter.MaxPendingChars) return false;

        dataStart = k + 1;
        dataLength = length;
        return true;
    }

    /// <summary>
    /// True when <paramref name="text"/>, read with the power-up prefixes, holds at least one binary download header. A
    /// text that changes its prefixes or delimiter (see <see cref="ZplPrefixes.AnyChangeIn"/>) must be treated as if it
    /// held one: under other characters its headers are not found here.
    /// </summary>
    public static bool ContainsAny(string text)
    {
        for (var i = 0; i + 3 <= text.Length; i++)
        {
            var c = text[i];
            if (c != '^' && c != '~') continue;
            // Compared letter by letter: this runs over every job, and a string per prefix would cost an allocation each.
            var b = char.ToUpperInvariant(text[i + 1]);
            var d = char.ToUpperInvariant(text[i + 2]);
            var name = c == '^' && b == 'G' && d == 'F' ? "^GF" : c == '~' && b == 'D' && d == 'Y' ? "~DY" : null;
            if (name is not null && TryMeasure(text, i + 3, name, out _, out _)) return true;
        }
        return false;
    }
}
