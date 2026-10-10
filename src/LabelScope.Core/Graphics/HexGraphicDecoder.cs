// src/LabelScope.Core/Graphics/HexGraphicDecoder.cs
namespace LabelScope.Core.Graphics;

/// <summary>
/// Reads ZPL's ASCII hexadecimal graphic data, including Zebra's "alternative data compression scheme" used by ~DG,
/// ~DB and ^GF format A (reference part 7): repeat letters G-Y (1-19) and g-z (20-400) before a hex digit, ","
/// (rest of the row white), "!" (rest of the row black) and ":" (repeat the previous row).
/// </summary>
internal static class HexGraphicDecoder
{
    /// <summary>
    /// Decodes <paramref name="data"/> into a <paramref name="bytesPerRow"/> x <paramref name="rows"/> image. Never
    /// throws for bad data: each kind of problem adds one plain-language sentence to <paramref name="notes"/>.
    /// </summary>
    /// <param name="data">The data part of the command (everything after the size parameters).</param>
    /// <param name="bytesPerRow">Bytes per row, already checked by <see cref="GraphicLimits.TryRows"/>.</param>
    /// <param name="rows">Number of rows, already checked by <see cref="GraphicLimits.TryRows"/>.</param>
    /// <param name="notes">Receives the problems found, without the command name.</param>
    public static MonoImage Decode(ReadOnlySpan<char> data, int bytesPerRow, int rows, ICollection<string> notes)
    {
        var bits = new byte[(long)bytesPerRow * rows];
        long nibblesPerRow = bytesPerRow * 2L;
        var total = nibblesPerRow * rows;

        long pos = 0;     // next nibble to write
        long repeat = 0;  // pending repeat count (0 = none)
        var badChars = 0;
        bool clipped = false, colonOnFirstRow = false, dangling = false, extra = false;

        foreach (var c in data)
        {
            if (c is ' ' or '\t' or '\r' or '\n') continue;

            var nibble = HexValue(c);
            var letter = nibble < 0 ? RepeatValue(c) : 0;
            if (pos >= total)
            {
                // Zebra: data after the declared size is ignored. Fill characters there are harmless and common.
                if (nibble >= 0 || letter > 0) extra = true;
                else if (c is not (',' or '!' or ':')) badChars++; // junk after the end is still counted, never silent
                if (extra) break;
                continue;
            }

            if (nibble >= 0)
            {
                var count = repeat == 0 ? 1 : repeat;
                repeat = 0;
                if (count > total - pos) { count = total - pos; clipped = true; }
                Fill(bits, pos, count, nibble);
                pos += count;
                continue;
            }

            if (letter > 0)
            {
                // Letters add up (vMB = 320 + 7). The sum is capped just above what is left, so even a megabyte of
                // 'z' cannot overflow, and nothing is ever allocated from the count.
                repeat = Math.Min(repeat + letter, total + 1);
                continue;
            }

            if (c is ',' or '!' or ':')
            {
                if (repeat != 0) { dangling = true; repeat = 0; }
                var rowStart = pos / nibblesPerRow * nibblesPerRow;
                var rowEnd = rowStart + nibblesPerRow;
                if (c == '!') Fill(bits, pos, rowEnd - pos, 0xF);
                else if (c == ':')
                {
                    if (rowStart == 0) colonOnFirstRow = true; // nothing to repeat: the row stays white
                    else CopyFromRowAbove(bits, pos, rowEnd - pos, nibblesPerRow);
                }
                // ',' needs no work: the buffer starts white.
                pos = rowEnd;
                continue;
            }

            badChars++;
        }
        if (repeat != 0) dangling = true;

        if (badChars > 0)
            notes.Add($"The graphic data contains {badChars} character(s) that are neither hexadecimal digits nor Zebra compression codes; they were skipped.");
        if (clipped)
            notes.Add("A repeat count in the graphic data runs past the end of the graphic; the extra was ignored.");
        if (colonOnFirstRow)
            notes.Add("The graphic data starts a row with ':' (repeat the previous row), but there is no previous row; that row was left white.");
        if (dangling)
            notes.Add("A repeat count in the graphic data is not followed by a hexadecimal digit; it was ignored.");
        if (extra)
            notes.Add("The graphic data is longer than the size given; the extra data was ignored, as a printer does.");
        if (pos < total)
            notes.Add($"The graphic data ended after {pos / nibblesPerRow} of {rows} rows; the rest of the graphic is blank.");

        return new MonoImage(bytesPerRow, rows, bits);
    }

    /// <summary>Value of a hexadecimal digit, or -1.</summary>
    private static int HexValue(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'A' and <= 'F' => c - 'A' + 10,
        >= 'a' and <= 'f' => c - 'a' + 10,
        _ => -1,
    };

    /// <summary>Repeat value of a compression letter (G = 1 ... Y = 19, g = 20 ... z = 400), or 0.</summary>
    private static int RepeatValue(char c) => c switch
    {
        >= 'G' and <= 'Y' => c - 'F',
        >= 'g' and <= 'z' => (c - 'f') * 20,
        _ => 0,
    };

    /// <summary>Writes <paramref name="count"/> copies of <paramref name="nibble"/> from nibble position <paramref name="pos"/>.</summary>
    private static void Fill(byte[] bits, long pos, long count, int nibble)
    {
        var end = pos + count;
        // Odd start: finish the low half of the current byte first.
        if (pos < end && (pos & 1) == 1) SetNibble(bits, pos++, nibble);
        // Whole bytes in one go; this keeps a 400-dot run, or a '!' over a 1000-byte row, cheap.
        var fullBytes = (end - pos) / 2;
        if (fullBytes > 0)
        {
            Array.Fill(bits, (byte)(nibble << 4 | nibble), (int)(pos / 2), (int)fullBytes);
            pos += fullBytes * 2;
        }
        if (pos < end) SetNibble(bits, pos, nibble);
    }

    /// <summary>Copies <paramref name="count"/> nibbles from the row above, starting at <paramref name="pos"/>.</summary>
    private static void CopyFromRowAbove(byte[] bits, long pos, long count, long nibblesPerRow)
    {
        // A row is a whole number of bytes, so the nibble above sits in the same half of its byte. After an odd start
        // the rest goes byte by byte in one copy: a 5000-row graphic of ':' rows stays fast (the hostile-input sweep
        // repeats one 1000-byte row 4999 times). The source is a row above, so the two ranges never overlap.
        var end = pos + count;
        if (pos < end && (pos & 1) == 1)
        {
            SetNibble(bits, pos, GetNibble(bits, pos - nibblesPerRow));
            pos++;
        }
        var fullBytes = (end - pos) / 2;
        if (fullBytes > 0)
        {
            Array.Copy(bits, (pos - nibblesPerRow) / 2, bits, pos / 2, fullBytes);
            pos += fullBytes * 2;
        }
        if (pos < end) SetNibble(bits, pos, GetNibble(bits, pos - nibblesPerRow));
    }

    // Even positions are the high (left) half of a byte, odd positions the low half.
    private static int GetNibble(byte[] bits, long pos) => (pos & 1) == 0 ? bits[pos >> 1] >> 4 : bits[pos >> 1] & 0xF;

    private static void SetNibble(byte[] bits, long pos, int value)
    {
        ref var b = ref bits[pos >> 1];
        b = (pos & 1) == 0 ? (byte)((b & 0x0F) | value << 4) : (byte)((b & 0xF0) | value);
    }
}
