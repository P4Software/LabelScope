// The QR matrix construction in this encoder (function patterns, module placement, masking, format and
// version information) follows the approach of the Project Nayuki QR Code generator library (MIT licence,
// https://www.nayuki.io/page/qr-code-generator-library). The licence text and the credit are in
// THIRD-PARTY-NOTICES.txt at the repository root.

namespace LabelScope.Core.Barcodes;

internal static partial class QrEncoder
{
    /// <summary>The 15 format information bits (level, mask, BCH error correction, XOR mask), as in ISO/IEC 18004 section 7.9.</summary>
    internal static int FormatBits(QrLevel level, int mask)
    {
        // The two level bits of the format information are not in L, M, Q, H order: L = 01, M = 00, Q = 11, H = 10.
        var levelBits = level switch { QrLevel.L => 1, QrLevel.M => 0, QrLevel.Q => 3, _ => 2 };
        var data = (levelBits << 3) | mask;
        var rem = data;
        // Polynomial division by the BCH(15,5) generator 10100110111; the 10-bit remainder is the check part.
        for (var i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >> 9) * 0x537);
        // The fixed XOR mask 101010000010010 keeps the format bits from ever being all zero.
        return ((data << 10) | rem) ^ 0x5412;
    }

    /// <summary>The 18 version information bits (version, BCH(18,6)); only used from version 7.</summary>
    internal static int VersionBits(int version)
    {
        var rem = version;
        // Polynomial division by the generator 1111100100101; the 12-bit remainder is the check part.
        for (var i = 0; i < 12; i++) rem = (rem << 1) ^ ((rem >> 11) * 0x1F25);
        return (version << 12) | rem;
    }

    /// <summary>Centre coordinates (same list for x and y) of the alignment patterns.</summary>
    internal static int[] AlignmentPositions(int version)
    {
        if (version == 1) return Array.Empty<int>();
        var count = version / 7 + 2;
        // The standard's table spaces the patterns evenly from the far edge back towards 6, with an even step
        // that this formula reproduces; version 32 is the one irregular row of the table.
        var step = version == 32 ? 26 : (version * 4 + count * 2 + 1) / (count * 2 - 2) * 2;
        var result = new int[count];
        result[0] = 6;
        for (int i = count - 1, pos = SizeOf(version) - 7; i >= 1; i--, pos -= step) result[i] = pos;
        return result;
    }

    /// <summary>Places the codewords, applies <paramref name="mask"/> and writes format and version information.</summary>
    /// <param name="codewords">Interleaved data and error correction codewords from <see cref="BuildCodewords"/>.</param>
    /// <param name="level">The error correction level the codewords were built for (written into the format information).</param>
    /// <param name="mask">Mask pattern 0 to 7.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="mask"/> is not 0 to 7 (callers map invalid user input first).</exception>
    public static BitMatrix BuildMatrix(QrCodewords codewords, QrLevel level, int mask)
    {
        // Not clamped: a silently changed mask would hide a caller bug. QrField turns invalid user input into 7.
        ArgumentOutOfRangeException.ThrowIfNegative(mask);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(mask, 7);

        var version = codewords.Version;
        var size = SizeOf(version);
        var dark = new bool[size, size];        // [y, x]
        var reserved = new bool[size, size];    // true for modules that are not data (patterns, format, version)

        void Set(int x, int y, bool value) { dark[y, x] = value; reserved[y, x] = true; }
        bool Bit(int value, int i) => ((value >> i) & 1) != 0;

        // Timing patterns: alternating modules in row 6 and column 6 (finders and alignments overwrite their ends).
        for (var i = 0; i < size; i++) { Set(6, i, i % 2 == 0); Set(i, 6, i % 2 == 0); }

        // Finder patterns with their light separators. The 9 x 9 area around each centre is drawn by ring
        // distance: ring 2 is the light ring inside the finder, ring 4 is the separator, the rest is dark.
        void Finder(int cx, int cy)
        {
            for (var dy = -4; dy <= 4; dy++)
                for (var dx = -4; dx <= 4; dx++)
                {
                    int x = cx + dx, y = cy + dy, d = Math.Max(Math.Abs(dx), Math.Abs(dy));
                    if (x >= 0 && x < size && y >= 0 && y < size) Set(x, y, d != 2 && d != 4);
                }
        }
        Finder(3, 3); Finder(size - 4, 3); Finder(3, size - 4);

        // Alignment patterns on every combination of the positions, except the three that would overlap a finder.
        var pos = AlignmentPositions(version);
        for (var i = 0; i < pos.Length; i++)
            for (var j = 0; j < pos.Length; j++)
            {
                if ((i == 0 && j == 0) || (i == 0 && j == pos.Length - 1) || (i == pos.Length - 1 && j == 0)) continue;
                for (var dy = -2; dy <= 2; dy++)
                    for (var dx = -2; dx <= 2; dx++)
                        Set(pos[i] + dx, pos[j] + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
            }

        // Format information, written twice (around the top-left finder, and split between the other two).
        // The first call only reserves the area so no data is placed there; the real bits follow after masking.
        void Format(int m)
        {
            var bits = FormatBits(level, m);
            for (var i = 0; i <= 5; i++) Set(8, i, Bit(bits, i));
            // Bits 6 to 8 step around the timing pattern crossing at (6, 6).
            Set(8, 7, Bit(bits, 6)); Set(8, 8, Bit(bits, 7)); Set(7, 8, Bit(bits, 8));
            for (var i = 9; i < 15; i++) Set(14 - i, 8, Bit(bits, i));
            for (var i = 0; i < 8; i++) Set(size - 1 - i, 8, Bit(bits, i));
            for (var i = 8; i < 15; i++) Set(8, size - 15 + i, Bit(bits, i));
            Set(8, size - 8, true);   // the module that is always dark
        }
        Format(0);

        // Version information (version 7 and up): a 6 x 3 block above the bottom-left finder and its mirror
        // image (3 x 6) left of the top-right finder.
        if (version >= 7)
        {
            var bits = VersionBits(version);
            for (var i = 0; i < 18; i++)
            {
                int a = size - 11 + i % 3, b = i / 3;
                Set(a, b, Bit(bits, i));
                Set(b, a, Bit(bits, i));
            }
        }

        // Codewords go in two-module-wide columns from the bottom right, zigzagging up and down. Column 6 holds
        // the vertical timing pattern, so the column pair left of it shifts by one.
        var data = codewords.Bytes;
        var index = 0;
        for (var right = size - 1; right >= 1; right -= 2)
        {
            if (right == 6) right = 5;
            // The first pair (right = size - 1) goes upward and the direction alternates; after the shift at
            // column 6 the parity of right + 1 keeps that alternation.
            var upward = ((right + 1) & 2) == 0;
            for (var vert = 0; vert < size; vert++)
                for (var j = 0; j < 2; j++)
                {
                    var x = right - j;
                    var y = upward ? size - 1 - vert : vert;
                    if (!reserved[y, x] && index < data.Length * 8)
                    {
                        dark[y, x] = Bit(data[index >> 3], 7 - (index & 7));
                        index++;
                    }
                    // Modules left over at the end are the standard's "remainder bits": light before masking.
                }
        }

        // The mask flips every data module (remainder bits included) where its condition holds, then the final
        // format bits are written, which carry the chosen mask number.
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                if (reserved[y, x]) continue;
                // ISO/IEC 18004 table 10, with i = row (y) and j = column (x).
                var invert = mask switch
                {
                    0 => (x + y) % 2 == 0,
                    1 => y % 2 == 0,
                    2 => x % 3 == 0,
                    3 => (x + y) % 3 == 0,
                    4 => (x / 3 + y / 2) % 2 == 0,
                    5 => x * y % 2 + x * y % 3 == 0,
                    6 => (x * y % 2 + x * y % 3) % 2 == 0,
                    _ => ((x + y) % 2 + x * y % 3) % 2 == 0,
                };
                dark[y, x] ^= invert;
            }
        Format(mask);

        var matrix = new BitMatrix(size, size);
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++) matrix[x, y] = dark[y, x];
        return matrix;
    }
}
