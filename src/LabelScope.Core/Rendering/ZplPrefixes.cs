namespace LabelScope.Core.Rendering;

/// <summary>
/// The three characters that shape how ZPL is read: the format prefix (<c>^</c>, changed by ^CC / ~CC), the control
/// prefix (<c>~</c>, changed by ^CT / ~CT) and the parameter delimiter (<c>,</c>, changed by ^CD / ~CD). A printer keeps
/// them from one job to the next until they are changed again or it is switched off.
/// </summary>
/// <remarks>
/// <c>default(ZplPrefixes)</c> holds three NUL characters, not the ZPL defaults; every reader treats it as
/// <see cref="Default"/> (see <see cref="OrDefault"/>), so a caller that passes <c>default</c> gets ordinary ZPL.
/// </remarks>
/// <param name="Format">Character that starts a format command (<c>^</c>).</param>
/// <param name="Control">Character that starts a control command (<c>~</c>).</param>
/// <param name="Delimiter">Character that separates parameters (<c>,</c>).</param>
public readonly record struct ZplPrefixes(char Format = '^', char Control = '~', char Delimiter = ',')
{
    /// <summary>The power-up characters: <c>^</c>, <c>~</c> and <c>,</c>.</summary>
    public static ZplPrefixes Default { get; } = new('^', '~', ',');

    /// <summary>True for the power-up characters (or an unset value), where every fast path of the readers applies.</summary>
    public bool IsDefault => OrDefault() == Default;

    /// <summary>This value, or <see cref="Default"/> when it was never set (<c>default(ZplPrefixes)</c>).</summary>
    public ZplPrefixes OrDefault() => Format == '\0' && Control == '\0' && Delimiter == '\0' ? Default : this;

    /// <summary>
    /// True when the two letters after a prefix name a change command (CC, CT or CD, either case). <paramref name="kind"/>
    /// is then the second letter in upper case: 'C' for the format prefix, 'T' for the control prefix, 'D' for the
    /// delimiter. Both prefixes may carry the change (^CC and ~CC are the same command).
    /// </summary>
    internal static bool IsChange(char first, char second, out char kind)
    {
        // Clearing bit 5 upper-cases an ASCII letter; only 'C'/'c', 'T'/'t' and 'D'/'d' land on the letters tested.
        kind = (char)(second & ~0x20);
        return (first & ~0x20) == 'C' && kind is 'C' or 'T' or 'D';
    }

    /// <summary>
    /// Applies the change command <paramref name="kind"/> ('C', 'T' or 'D', see <see cref="IsChange"/>) with the new
    /// character <paramref name="value"/>. Returns false, and leaves the characters as they were, when the character would
    /// make the rest of the job unreadable: anything but printable ASCII, a letter, a digit, a space or a comma (a comma is
    /// allowed only as the delimiter, which puts the default back), or a character already used by one of the other two.
    /// </summary>
    /// <remarks>
    /// A printer accepts any character here and then prints garbage; refusing it with a warning tells the user what went
    /// wrong instead. Only printable ASCII is accepted because the stream splitter sees bytes and the parser sees decoded
    /// characters: a UTF-8 or code-page character is one character to the parser but one or more different bytes to the
    /// splitter, so the two would disagree about where labels end.
    /// </remarks>
    internal bool TryChange(char kind, char value, out ZplPrefixes next)
    {
        var p = OrDefault();
        next = p;
        var allowed = value is > ' ' and < '\x7F' && !char.IsAsciiLetterOrDigit(value);
        switch (kind)
        {
            case 'C':
                if (!allowed || value == ',' || value == p.Control || value == p.Delimiter) return false;
                next = p with { Format = value };
                return true;
            case 'T':
                if (!allowed || value == ',' || value == p.Format || value == p.Delimiter) return false;
                next = p with { Control = value };
                return true;
            case 'D':
                if (!allowed || value == p.Format || value == p.Control) return false;
                next = p with { Delimiter = value };
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// True when reading <paramref name="text"/> from the power-up characters leaves them changed at some point, or meets a
    /// refused change. A change back to the defaults, such as the <c>~CC^~CT~~CD,</c> many label programs start with, does
    /// not count. A refused one does: its character is swallowed, and if the formatter trimmed that character (a space)
    /// away, the next prefix would become the change's character and the command after it would be lost.
    /// </summary>
    internal static bool AnyChangeIn(string text)
    {
        var p = Default;
        var i = 0;
        while (i < text.Length)
        {
            var marker = text.AsSpan(i).IndexOfAny(p.Format, p.Control);
            if (marker < 0) return false;
            i += marker;
            if (!TryReadChange(text, i, p, out var next, out var resume, out _, out var refused)) { i++; continue; }
            if (refused || next != Default) return true;
            p = next;
            i = resume;
        }
        return false;
    }

    /// <summary>
    /// Reads a change command whose prefix is at <paramref name="marker"/>, the way <see cref="ZplParser"/> reads it: the
    /// two name letters (line breaks inside the name skipped, a prefix ending it), then the next character that is not a
    /// line break. Returns false when no change command starts there. Otherwise <paramref name="next"/> holds the
    /// characters after it (unchanged when refused or when the text ends first), <paramref name="resume"/> the index where
    /// reading goes on, <paramref name="value"/> the character read (NUL at the end of the text) and
    /// <paramref name="refused"/> whether it was refused.
    /// </summary>
    internal static bool TryReadChange(string text, int marker, ZplPrefixes p, out ZplPrefixes next, out int resume,
                                       out char value, out bool refused)
    {
        next = p;
        resume = marker + 1;
        value = '\0';
        refused = false;

        Span<char> name = stackalloc char[2];
        var found = 0;
        var j = marker + 1;
        for (; j < text.Length && found < 2; j++)
        {
            var c = text[j];
            if (c == p.Format || c == p.Control) return false;
            if (c is '\r' or '\n') continue;
            name[found++] = c;
        }
        if (found < 2 || !IsChange(name[0], name[1], out var kind)) return false;

        while (j < text.Length && text[j] is '\r' or '\n') j++;
        if (j >= text.Length) { resume = j; return true; } // nothing to change to: the text ended

        value = text[j];
        if (p.TryChange(kind, value, out next)) { resume = j + 1; return true; }
        refused = true;
        // A refused prefix character is not swallowed: it starts the next command, as it would have without the change.
        resume = value == p.Format || value == p.Control ? j : j + 1;
        return true;
    }
}
