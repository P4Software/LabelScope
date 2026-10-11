using System.Text;

namespace LabelScope.Core.Rendering;

/// <summary>Splits ZPL text into commands. It never throws: unknown text is simply skipped.</summary>
public static class ZplParser
{
    /// <summary>
    /// Parses <paramref name="zpl"/> into its commands, in order, reading it with the power-up prefixes (<c>^</c>,
    /// <c>~</c>, <c>,</c>). Line breaks (CR, LF or CRLF) are not part of any command: they are removed from names and
    /// arguments and only used to number the lines, so a command that a program wrapped over two lines still reads as
    /// one. Null gives an empty list.
    /// </summary>
    public static IReadOnlyList<ZplCommand> Parse(string? zpl) => Parse(zpl, ZplPrefixes.Default, out _, null);

    /// <summary>
    /// Parses <paramref name="zpl"/> starting with the prefixes and delimiter in <paramref name="start"/> (what an
    /// earlier job left set), following every ^CC, ^CD, ^CT, ~CC, ~CD and ~CT in the text. Command names come out with
    /// the standard prefixes ("^FO", "~DG") and parameters separated by commas whatever characters the text used, so the
    /// rest of LabelScope reads them as usual; the data of ^FD, ^FV, ^FX and the ^SN value is left exactly as sent.
    /// </summary>
    /// <param name="zpl">The text; null gives an empty list.</param>
    /// <param name="start">The characters in force when the text begins; <c>default</c> means the power-up ones.</param>
    /// <param name="end">The characters in force after the last command, which the next job starts with.</param>
    public static IReadOnlyList<ZplCommand> Parse(string? zpl, ZplPrefixes start, out ZplPrefixes end) =>
        Parse(zpl, start, out end, null);

    /// <summary>
    /// As <see cref="Parse(string?, ZplPrefixes, out ZplPrefixes)"/>, and adds to <paramref name="refused"/> each change
    /// command whose character was refused (see <see cref="ZplPrefixes.TryChange"/>), with that character as its
    /// argument. Only the parser knows which characters were in force at that point, so it is the one to report it.
    /// </summary>
    internal static IReadOnlyList<ZplCommand> Parse(string? zpl, ZplPrefixes start, out ZplPrefixes end, List<ZplCommand>? refused)
    {
        var commands = new List<ZplCommand>();
        var p = start.OrDefault();
        end = p;
        if (zpl is null) return commands;

        // Locals rather than reads of the struct in the hot loops; refreshed whenever a change command applies.
        var format = p.Format;
        var control = p.Control;

        var line = 1;
        var i = 0;

        while (i < zpl.Length)
        {
            var c = zpl[i];
            if (c != format && c != control)
            {
                if (IsLineBreak(zpl, i)) line++;
                i++;
                continue;
            }

            var startLine = line; // Line is where the command starts, whatever follows
            // Names come out with the standard prefix whatever character the text used.
            var marker = c == format ? '^' : '~';

            // ^CC, ^CD, ^CT and their ~ forms take exactly the next character, even a prefix: the rest of the text is
            // read with the new characters from there on.
            if (ZplPrefixes.TryReadChange(zpl, i, p, out var next, out var resume, out var value, out var wasRefused))
            {
                var changeName = new StringBuilder(3).Append(marker);
                for (var k = i + 1; k < resume; k++)
                {
                    if (IsLineBreak(zpl, k)) line++;
                    if (changeName.Length < 3 && zpl[k] is not ('\r' or '\n')) changeName.Append(char.ToUpperInvariant(zpl[k]));
                }
                var change = new ZplCommand(changeName.ToString(), value == '\0' ? "" : value.ToString(), startLine);
                commands.Add(change);
                if (wasRefused) refused?.Add(change);
                p = next;
                format = p.Format;
                control = p.Control;
                i = resume;
                continue;
            }

            var j = i + 1;

            // The name is at most two characters. It stops before the next prefix so a truncated name (for example
            // "^X" followed by "^FS") never swallows the next command's marker. ^A is special: its name is one letter
            // and the next character is the font id (^A0N,30,30).
            var name = new StringBuilder(marker.ToString());
            var wanted = 2;
            while (name.Length - 1 < wanted && j < zpl.Length && zpl[j] != format && zpl[j] != control)
            {
                if (zpl[j] == '\r' || zpl[j] == '\n')
                {
                    // A line break inside the name does not belong to it, but it still counts as a line.
                    if (IsLineBreak(zpl, j)) line++;
                }
                else
                {
                    name.Append(char.ToUpperInvariant(zpl[j]));
                    if (name.Length == 2 && marker == '^' && name[1] == 'A') wanted = 1;
                }
                j++;
            }

            // A marker with no name ("^^", "~^XA", a lone "^" at the end) is skipped; j is past the marker,
            // so the loop always advances.
            if (name.Length == 1)
            {
                i = j;
                continue;
            }

            var commandName = name.ToString();
            // Binary data (^GFB, ~DY format B) is skipped by its declared byte count: its bytes may contain the prefixes,
            // which would otherwise become garbage commands. Binary data is not drawn yet; it only must not break the job.
            if (BinaryDownloads.TryMeasure(zpl, j, commandName, p, out var dataStart, out var dataLength))
            {
                var header = new StringBuilder();
                for (var k = j; k < dataStart; k++)
                {
                    if (zpl[k] == '\r' || zpl[k] == '\n') { if (IsLineBreak(zpl, k)) line++; }
                    else header.Append(zpl[k]);
                }
                var headerText = header.ToString().Trim();
                if (p.Delimiter != ',') headerText = headerText.Replace(p.Delimiter, ',');
                commands.Add(new ZplCommand(commandName, headerText, startLine));
                var stop = (int)Math.Min(zpl.Length, dataStart + dataLength);
                for (var k = dataStart; k < stop; k++)
                    if (IsLineBreak(zpl, k)) line++;
                i = stop;
                continue;
            }

            var args = new StringBuilder();
            while (j < zpl.Length && zpl[j] != format && zpl[j] != control)
            {
                if (zpl[j] == '\r' || zpl[j] == '\n')
                {
                    if (IsLineBreak(zpl, j)) line++;
                }
                else
                {
                    args.Append(zpl[j]);
                }
                j++;
            }

            // Field data (^FD, and ^FV, its variable twin) may legitimately start with spaces; other commands never do.
            // ^SN too: its first parameter is the field data, and with z=N its leading spaces are counter width.
            var raw = args.ToString();
            var text = commandName is "^FD" or "^FV" or "^SN" ? raw : raw.Trim();
            if (commandName is "^FH" or "^FE")
            {
                // One parameter, a single indicator character. Cutting at the delimiter makes "^FH," mean "no indicator"
                // (the default) and lets a comma be the indicator once ^CD has made it an ordinary character.
                var cut = raw.IndexOf(p.Delimiter);
                text = (cut < 0 ? raw : raw[..cut]).Trim();
            }
            else if (p.Delimiter != ',')
            {
                text = RestoreCommas(commandName, text, p.Delimiter);
            }

            commands.Add(new ZplCommand(commandName, text, startLine));
            i = j;
        }

        end = p;
        return commands;
    }

    /// <summary>
    /// Turns a changed delimiter back into commas, so every command reader can keep splitting on ','. Free data keeps its
    /// characters: ^FD, ^FV and ^FX text, the ^SN value (its first parameter), and the payload after the header of the
    /// graphic downloads (^GF, ~DG, ~DY), whose Z64 or hex data may contain the delimiter as an ordinary character.
    /// </summary>
    private static string RestoreCommas(string name, string args, char delimiter) => name switch
    {
        "^FD" or "^FV" or "^FX" => args,
        "^SN" => ReplaceAfter(args, delimiter, 1),
        "^GF" => ReplaceFirst(args, delimiter, 4),
        "~DG" => ReplaceFirst(args, delimiter, 3),
        "~DY" => ReplaceFirst(args, delimiter, 5),
        _ => args.Replace(delimiter, ','),
    };

    /// <summary>Replaces the first <paramref name="count"/> delimiters with commas and leaves the rest as it is.</summary>
    private static string ReplaceFirst(string args, char delimiter, int count)
    {
        var chars = args.ToCharArray();
        for (var k = 0; k < chars.Length && count > 0; k++)
        {
            if (chars[k] != delimiter) continue;
            chars[k] = ',';
            count--;
        }
        return new string(chars);
    }

    /// <summary>
    /// Leaves everything up to the first delimiter as it is (the ^SN value) and replaces every delimiter from there on;
    /// <paramref name="keep"/> is the number of leading parameters kept.
    /// </summary>
    private static string ReplaceAfter(string args, char delimiter, int keep)
    {
        var k = -1;
        for (var n = 0; n < keep; n++)
        {
            k = args.IndexOf(delimiter, k + 1);
            if (k < 0) return args;
        }
        return string.Concat(args.AsSpan(0, k), args[k..].Replace(delimiter, ','));
    }

    /// <summary>
    /// True when the character at <paramref name="index"/> ends a line: a LF, or a CR that is not followed by a LF
    /// (so CRLF counts once and a lone CR counts as well).
    /// </summary>
    private static bool IsLineBreak(string text, int index) =>
        text[index] == '\n' || (text[index] == '\r' && !(index + 1 < text.Length && text[index + 1] == '\n'));
}
