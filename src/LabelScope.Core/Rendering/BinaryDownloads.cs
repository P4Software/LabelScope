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
    public static bool TryMeasure(string text, int afterName, string name, out int dataStart, out long dataLength)
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

        var fields = new List<string>();
        var current = new StringBuilder();
        var k = afterName;
        for (; k < text.Length && k - afterName < MaxHeaderChars; k++)
        {
            var c = text[k];
            if (c is '^' or '~') return false;          // the header ended before all its parameters
            if (c is '\r' or '\n') continue;            // line breaks are not part of a parameter
            if (c != ',') { current.Append(c); continue; }
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

    /// <summary>True when <paramref name="text"/> holds at least one binary download header.</summary>
    public static bool ContainsAny(string text)
    {
        for (var i = 0; i + 3 <= text.Length; i++)
        {
            var c = text[i];
            if (c != '^' && c != '~') continue;
            var name = new string([c, char.ToUpperInvariant(text[i + 1]), char.ToUpperInvariant(text[i + 2])]);
            if (name is "^GF" or "~DY" && TryMeasure(text, i + 3, name, out _, out _)) return true;
        }
        return false;
    }
}
