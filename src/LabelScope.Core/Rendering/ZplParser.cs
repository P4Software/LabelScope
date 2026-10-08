using System.Text;

namespace LabelScope.Core.Rendering;

/// <summary>Splits ZPL text into commands. It never throws: unknown text is simply skipped.</summary>
public static class ZplParser
{
    /// <summary>
    /// Parses <paramref name="zpl"/> into its commands, in order. Line breaks (CR, LF or CRLF) are not part of any
    /// command: they are removed from names and arguments and only used to number the lines, so a command that a
    /// program wrapped over two lines still reads as one. Null gives an empty list.
    /// </summary>
    public static IReadOnlyList<ZplCommand> Parse(string? zpl)
    {
        var commands = new List<ZplCommand>();
        if (zpl is null) return commands;

        var line = 1;
        var i = 0;

        while (i < zpl.Length)
        {
            var c = zpl[i];
            if (c != '^' && c != '~')
            {
                if (IsLineBreak(zpl, i)) line++;
                i++;
                continue;
            }

            var startLine = line; // Line is where the command starts, whatever follows
            var j = i + 1;

            // The name is at most two characters. It stops before the next '^' or '~' so a truncated name
            // (for example "^X" followed by "^FS") never swallows the next command's marker. ^A is special: its
            // name is one letter and the next character is the font id (^A0N,30,30).
            var name = new StringBuilder(c.ToString());
            var wanted = 2;
            while (name.Length - 1 < wanted && j < zpl.Length && zpl[j] != '^' && zpl[j] != '~')
            {
                if (zpl[j] == '\r' || zpl[j] == '\n')
                {
                    // A line break inside the name does not belong to it, but it still counts as a line.
                    if (IsLineBreak(zpl, j)) line++;
                }
                else
                {
                    name.Append(char.ToUpperInvariant(zpl[j]));
                    if (name.Length == 2 && c == '^' && name[1] == 'A') wanted = 1;
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

            var args = new StringBuilder();
            while (j < zpl.Length && zpl[j] != '^' && zpl[j] != '~')
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

            var commandName = name.ToString();
            // Field data may legitimately start with spaces; other commands never do.
            var text = commandName == "^FD" ? args.ToString() : args.ToString().Trim();

            commands.Add(new ZplCommand(commandName, text, startLine));
            i = j;
        }

        return commands;
    }

    /// <summary>
    /// True when the character at <paramref name="index"/> ends a line: a LF, or a CR that is not followed by a LF
    /// (so CRLF counts once and a lone CR counts as well).
    /// </summary>
    private static bool IsLineBreak(string text, int index) =>
        text[index] == '\n' || (text[index] == '\r' && !(index + 1 < text.Length && text[index + 1] == '\n'));
}
