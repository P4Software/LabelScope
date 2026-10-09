using System.Text;

namespace LabelScope.Core.Rendering;

/// <summary>
/// Lays ZPL out for reading: one command group per line instead of one very long line.
/// ZPL ignores CR and LF between (and inside) commands, which is why re-flowing the text is safe:
/// <see cref="ZplParser"/> removes them too, so the formatted text draws exactly the same label.
/// </summary>
public static class ZplFormatter
{
    /// <summary>What a command is for the purpose of deciding where a line may end.</summary>
    private enum Kind { Other, FieldOpen, FieldClose, FieldData, LabelStart, LabelEnd }

    /// <summary>
    /// Formats <paramref name="zpl"/> with one command group per line, separated by <c>\n</c>, no indentation, no
    /// trailing newline and one blank line between labels. A field (from <c>^FO</c> or <c>^FT</c> up to and including
    /// its <c>^FS</c>) stays on one line. Spaces inside field data are never touched. Null or empty gives "".
    /// The method never throws (on an unexpected failure the input is returned unchanged), runs in linear time and
    /// formatting its own output changes nothing.
    /// </summary>
    public static string Format(string? zpl)
    {
        if (string.IsNullOrEmpty(zpl)) return "";
        try
        {
            return FormatCore(zpl);
        }
        catch (Exception)
        {
            // A viewer must still show the text even if formatting somehow fails; the unformatted text is
            // always a correct ZPL, just harder to read.
            return zpl;
        }
    }

    private static string FormatCore(string zpl)
    {
        // Output is the input without line breaks plus roughly one new line per command group.
        var sb = new StringBuilder(zpl.Length + zpl.Length / 8 + 16);
        var hasLine = false;         // the current output line has content
        var fieldOpen = false;       // between ^FO/^FT and ^FS: nothing may be broken
        var lastLineIsField = false; // the current line was started by ^FO/^FT (a stray ^FS may join it)
        var pendingBlank = false;    // a label just ended: separate it from whatever follows
        var keepEnd = false;         // the line ends in text whose trailing spaces are significant (^FD data)

        var i = 0;
        var n = zpl.Length;

        // Text before the first command (a BOM, stray characters) is kept on a line of its own.
        var firstMarker = zpl.AsSpan().IndexOfAny('^', '~');
        var strayEnd = firstMarker < 0 ? n : firstMarker;
        if (strayEnd > 0)
        {
            var before = sb.Length;
            AppendWithoutLineBreaks(sb, zpl, 0, strayEnd);
            if (HasContent(sb, before)) { hasLine = true; keepEnd = true; }
            else sb.Length = before; // only CR/LF/spaces: nothing worth a line
            i = strayEnd;
        }

        while (i < n)
        {
            // A command runs until the next '^' or '~', exactly as ZplParser reads it.
            var rest = zpl.AsSpan(i + 1).IndexOfAny('^', '~');
            var end = rest < 0 ? n : i + 1 + rest;
            var kind = Classify(zpl, i, end);

            bool inline;
            if (fieldOpen && kind is not (Kind.LabelStart or Kind.LabelEnd))
                inline = true; // everything inside a field stays on its line
            else
                // A ^FS with no open field joins the previous line only when that line is a field.
                inline = kind == Kind.FieldClose && hasLine && lastLineIsField;

            if (!inline)
            {
                if (hasLine)
                {
                    TrimEnd(sb, keepEnd);
                    sb.Append('\n');
                    if (pendingBlank) sb.Append('\n');
                }
                pendingBlank = false;
                hasLine = true;
                lastLineIsField = kind == Kind.FieldOpen;
                // A label boundary ends any field that was never closed with ^FS.
                fieldOpen = kind == Kind.FieldOpen;
            }
            else if (kind == Kind.FieldClose)
            {
                fieldOpen = false;
            }

            AppendWithoutLineBreaks(sb, zpl, i, end);
            keepEnd = kind == Kind.FieldData;
            if (kind == Kind.LabelEnd) pendingBlank = true;
            i = end;
        }

        TrimEnd(sb, keepEnd);
        return sb.ToString();
    }

    /// <summary>
    /// Reads the two-letter name of the command starting at <paramref name="start"/> (skipping line breaks, as the
    /// parser does) and says what kind of command it is. Only '^' commands are classified; '~' commands are plain.
    /// </summary>
    private static Kind Classify(string zpl, int start, int end)
    {
        if (zpl[start] != '^') return Kind.Other;

        Span<char> name = stackalloc char[2];
        var found = 0;
        for (var k = start + 1; k < end && found < 2; k++)
        {
            var c = zpl[k];
            if (c == '\r' || c == '\n') continue;
            name[found++] = char.ToUpperInvariant(c);
        }
        if (found < 2) return Kind.Other;

        return (name[0], name[1]) switch
        {
            ('F', 'O') or ('F', 'T') => Kind.FieldOpen,
            ('F', 'S') => Kind.FieldClose,
            ('F', 'D') => Kind.FieldData,
            ('X', 'A') => Kind.LabelStart,
            ('X', 'Z') => Kind.LabelEnd,
            _ => Kind.Other,
        };
    }

    /// <summary>Appends <c>text[start..end]</c> without CR and LF, copying whole runs (fast on very large input).</summary>
    private static void AppendWithoutLineBreaks(StringBuilder sb, string text, int start, int end)
    {
        var runStart = start;
        for (var k = start; k < end; k++)
        {
            var c = text[k];
            if (c != '\r' && c != '\n') continue;
            if (k > runStart) sb.Append(text, runStart, k - runStart);
            runStart = k + 1;
        }
        if (end > runStart) sb.Append(text, runStart, end - runStart);
    }

    /// <summary>True when anything other than white space was appended since <paramref name="from"/>.</summary>
    private static bool HasContent(StringBuilder sb, int from)
    {
        for (var k = from; k < sb.Length; k++)
            if (!char.IsWhiteSpace(sb[k])) return true;
        return false;
    }

    /// <summary>
    /// Removes spaces and tabs at the end of the output. The parser trims the arguments of every command except
    /// <c>^FD</c> anyway, so this only removes what the re-flow itself would leave dangling. It is skipped when the
    /// line ends in field data, where trailing spaces are part of the data.
    /// </summary>
    private static void TrimEnd(StringBuilder sb, bool keepEnd)
    {
        if (keepEnd) return;
        var length = sb.Length;
        while (length > 0 && (sb[length - 1] == ' ' || sb[length - 1] == '\t')) length--;
        sb.Length = length;
    }
}
