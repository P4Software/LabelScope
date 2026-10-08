namespace LabelScope.Core.Rendering;

/// <summary>Splits ZPL text into commands. It never throws: unknown text is simply skipped.</summary>
public static class ZplParser
{
    /// <summary>Parses <paramref name="zpl"/> into its commands, in order.</summary>
    public static IReadOnlyList<ZplCommand> Parse(string zpl)
    {
        var commands = new List<ZplCommand>();
        var line = 1;
        var i = 0;

        while (i < zpl.Length)
        {
            var c = zpl[i];
            if (c != '^' && c != '~')
            {
                if (c == '\n') line++;
                i++;
                continue;
            }

            var nameStart = i + 1;
            if (nameStart >= zpl.Length) break;

            // ^A is special: the "name" is one letter and the next character is the font id (^A0N,30,30).
            var nameLength = c == '^' && char.ToUpperInvariant(zpl[nameStart]) == 'A'
                ? 1
                : Math.Min(2, zpl.Length - nameStart);

            var name = c + zpl.Substring(nameStart, nameLength).ToUpperInvariant();
            var argStart = nameStart + nameLength;
            var startLine = line;

            var j = argStart;
            while (j < zpl.Length && zpl[j] != '^' && zpl[j] != '~')
            {
                if (zpl[j] == '\n') line++;
                j++;
            }

            var args = zpl[argStart..j];
            // Field data may legitimately start with spaces; other commands never do.
            args = name == "^FD" ? args.TrimEnd('\r', '\n') : args.Trim();

            commands.Add(new ZplCommand(name, args, startLine));
            i = j;
        }

        return commands;
    }
}
