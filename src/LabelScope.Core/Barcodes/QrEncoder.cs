// This encoder (codeword building and the matrix half in QrEncoder.Matrix.cs) follows the approach of the
// Project Nayuki QR Code generator library (MIT licence, https://www.nayuki.io/page/qr-code-generator-library).
// The licence text and the credit are in THIRD-PARTY-NOTICES.txt at the repository root.

using System.Text;

namespace LabelScope.Core.Barcodes;

/// <summary>QR error correction level.</summary>
internal enum QrLevel { L = 0, M = 1, Q = 2, H = 3 }

/// <summary>The data and error correction codewords of a QR symbol, interleaved and ready to be placed.</summary>
/// <param name="Version">Symbol version 1 to 40 (21 + 4 x (version - 1) modules per side).</param>
/// <param name="Bytes">Data codewords followed by error correction codewords, block-interleaved as the standard requires.</param>
internal sealed record QrCodewords(int Version, byte[] Bytes);

/// <summary>QR Code model 2 encoder (ISO/IEC 18004): numeric, alphanumeric and byte modes.</summary>
/// <remarks>Partial: the matrix half (function patterns, placement, masks) lives in a second file.</remarks>
internal static partial class QrEncoder
{
    private const string AlphanumericSet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ $%*+-./:";

    /// <summary>Error correction codewords per block, by level (L, M, Q, H) and version 1 to 40. From ISO/IEC 18004 table 9.</summary>
    private static readonly int[][] EcPerBlock =
    {
        new[] { 7, 10, 15, 20, 26, 18, 20, 24, 30, 18, 20, 24, 26, 30, 22, 24, 28, 30, 28, 28, 28, 28, 30, 30, 26, 28, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30 },
        new[] { 10, 16, 26, 18, 24, 16, 18, 22, 22, 26, 30, 22, 22, 24, 24, 28, 28, 26, 26, 26, 26, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28 },
        new[] { 13, 22, 18, 26, 18, 24, 18, 22, 20, 24, 28, 26, 24, 20, 30, 24, 28, 28, 26, 30, 28, 30, 30, 30, 30, 28, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30 },
        new[] { 17, 28, 22, 16, 22, 28, 26, 26, 24, 28, 24, 28, 22, 24, 24, 30, 28, 28, 26, 28, 30, 24, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30 },
    };

    /// <summary>Number of error correction blocks, by level and version 1 to 40. From ISO/IEC 18004 table 9.</summary>
    private static readonly int[][] BlockCount =
    {
        new[] { 1, 1, 1, 1, 1, 2, 2, 2, 2, 4, 4, 4, 4, 4, 6, 6, 6, 6, 7, 8, 8, 9, 9, 10, 12, 12, 12, 13, 14, 15, 16, 17, 18, 19, 19, 20, 21, 22, 24, 25 },
        new[] { 1, 1, 1, 2, 2, 4, 4, 4, 5, 5, 5, 8, 9, 9, 10, 10, 11, 13, 14, 16, 17, 17, 18, 20, 21, 23, 25, 26, 28, 29, 31, 33, 35, 37, 38, 40, 43, 45, 47, 49 },
        new[] { 1, 1, 2, 2, 4, 4, 6, 6, 8, 8, 8, 10, 12, 16, 12, 17, 16, 18, 21, 20, 23, 23, 25, 27, 29, 34, 34, 35, 38, 40, 43, 45, 48, 51, 53, 56, 59, 62, 65, 68 },
        new[] { 1, 1, 2, 4, 4, 4, 5, 6, 8, 8, 11, 11, 16, 16, 18, 16, 19, 21, 25, 25, 25, 34, 30, 32, 35, 37, 40, 42, 45, 48, 51, 54, 57, 60, 63, 66, 70, 74, 77, 81 },
    };

    private static readonly GaloisField Field = new(0x11D);

    /// <summary>Modules per side of a symbol of <paramref name="version"/>.</summary>
    public static int SizeOf(int version) => 17 + 4 * version;

    /// <summary>Modules that can carry codewords (everything except finder, timing, alignment and format/version areas).</summary>
    public static int RawDataModules(int version)
    {
        // All modules minus the three finders with separators, the timing patterns and the format information,
        // folded into one polynomial; the leftover remainder bits (0 to 7) fall away in the division by 8 later.
        var result = (16 * version + 128) * version + 64;
        if (version >= 2)
        {
            var align = version / 7 + 2;                 // alignment pattern positions per axis
            // align^2 - 3 alignment patterns of 25 modules each; the 2 x (align - 2) that sit on a timing pattern
            // share 5 modules with it, which were already taken away above.
            result -= (25 * align - 10) * align - 55;
            if (version >= 7) result -= 36;              // two version information blocks of 18 modules
        }
        return result;
    }

    /// <summary>Number of data codewords (error correction excluded) of a version and level.</summary>
    public static int DataCodewords(int version, QrLevel level) =>
        RawDataModules(version) / 8 - EcPerBlock[(int)level][version - 1] * BlockCount[(int)level][version - 1];

    // ---- segments --------------------------------------------------------------------------------

    /// <summary>Width of the character count field, which grows at versions 10 and 27.</summary>
    private static int CountBits(char mode, int version)
    {
        var range = version <= 9 ? 0 : version <= 26 ? 1 : 2;
        return mode switch
        {
            'N' => new[] { 10, 12, 14 }[range],
            'A' => new[] { 9, 11, 13 }[range],
            _ => new[] { 8, 16, 16 }[range],
        };
    }

    private static bool IsNumeric(string data) => data.All(char.IsAsciiDigit);

    private static bool IsAlphanumeric(string data) => data.All(c => AlphanumericSet.Contains(c));

    /// <summary>The most compact single mode that holds every character: numeric, else alphanumeric, else byte.</summary>
    private static char DetectMode(string data) => IsNumeric(data) ? 'N' : IsAlphanumeric(data) ? 'A' : 'B';

    /// <summary>Bytes of byte mode: Latin-1 when possible (what scanners expect for 8-bit data), otherwise UTF-8.</summary>
    private static byte[] ByteData(string data) =>
        data.All(c => c <= 0xFF) ? Encoding.Latin1.GetBytes(data) : Encoding.UTF8.GetBytes(data);

    /// <summary>A growable, most-significant-bit-first bit string.</summary>
    private sealed class BitBuffer
    {
        private readonly List<bool> _bits = new();

        /// <summary>Number of bits written so far.</summary>
        public int Length => _bits.Count;

        /// <summary>Appends the low <paramref name="count"/> bits of <paramref name="value"/>, highest first.</summary>
        public void Append(int value, int count)
        {
            for (var i = count - 1; i >= 0; i--) _bits.Add(((value >> i) & 1) == 1);
        }

        /// <summary>Packs the bits into bytes; a partial last byte is padded with zero bits.</summary>
        public byte[] ToBytes()
        {
            var bytes = new byte[(_bits.Count + 7) / 8];
            for (var i = 0; i < _bits.Count; i++)
                if (_bits[i]) bytes[i >> 3] |= (byte)(0x80 >> (i & 7));
            return bytes;
        }
    }

    /// <summary>
    /// Writes mode indicator, character count and data for one segment, or returns null when the count does not fit
    /// the count field of this version (the data is then far too long for the version anyway).
    /// </summary>
    private static BitBuffer? Segment(char mode, string data, int version)
    {
        var bits = new BitBuffer();
        var indicator = mode switch { 'N' => 1, 'A' => 2, _ => 4 };
        var payload = mode == 'B' ? ByteData(data) : null;
        var count = payload?.Length ?? data.Length;

        // A count wider than its field would be silently truncated and give a symbol that decodes to the wrong length.
        var countBits = CountBits(mode, version);
        if (count >= 1 << countBits) return null;

        bits.Append(indicator, 4);
        bits.Append(count, countBits);

        if (mode == 'N')
        {
            // Groups of three digits in 10 bits; a last group of two takes 7 bits, of one 4 bits.
            for (var i = 0; i < data.Length; i += 3)
            {
                var group = data.Substring(i, Math.Min(3, data.Length - i));
                bits.Append(int.Parse(group), group.Length switch { 3 => 10, 2 => 7, _ => 4 });
            }
        }
        else if (mode == 'A')
        {
            // Pairs as 45 x first + second in 11 bits; an odd last character in 6 bits.
            for (var i = 0; i + 1 < data.Length; i += 2)
                bits.Append(AlphanumericSet.IndexOf(data[i]) * 45 + AlphanumericSet.IndexOf(data[i + 1]), 11);
            if (data.Length % 2 == 1) bits.Append(AlphanumericSet.IndexOf(data[^1]), 6);
        }
        else
        {
            foreach (var b in payload!) bits.Append(b, 8);
        }
        return bits;
    }

    // ---- codewords -------------------------------------------------------------------------------

    /// <summary>
    /// Chooses the smallest version that holds <paramref name="data"/> at <paramref name="level"/>, then builds
    /// data and error correction codewords. <paramref name="mode"/> is '\0' for automatic or N, A, B to force one.
    /// </summary>
    /// <exception cref="BarcodeDataException">The data is empty, does not fit the forced mode, or exceeds version 40.</exception>
    public static QrCodewords BuildCodewords(string data, QrLevel level, char mode)
    {
        if (data.Length == 0) throw new BarcodeDataException("There is no data for the QR code. Add the text to encode after ^FD.");

        mode = mode == '\0' ? DetectMode(data) : char.ToUpperInvariant(mode);
        if (mode is not ('N' or 'A' or 'B'))
            throw new BarcodeDataException($"QR code input mode '{mode}' is not supported. Use N (numeric), A (alphanumeric) or B (byte), or leave the mode out to choose automatically.");
        if (mode == 'N' && !IsNumeric(data))
            throw new BarcodeDataException("The QR code was set to numeric input, but the data contains characters that are not digits. Remove them or use automatic input.");
        if (mode == 'A' && !IsAlphanumeric(data))
            throw new BarcodeDataException("The QR code was set to alphanumeric input (digits, capital letters and space $ % * + - . / :), but the data contains other characters. Remove them or use automatic input.");

        for (var version = 1; version <= 40; version++)
        {
            var capacityBits = DataCodewords(version, level) * 8;
            var bits = Segment(mode, data, version);
            if (bits is null || bits.Length > capacityBits) continue;

            // Terminator (up to 4 zero bits), then zero bits up to a byte boundary, then alternating pad bytes 0xEC 0x11.
            bits.Append(0, Math.Min(4, capacityBits - bits.Length));
            bits.Append(0, (8 - bits.Length % 8) % 8);
            var bytes = bits.ToBytes().ToList();
            for (var pad = 0xEC; bytes.Count < capacityBits / 8; pad ^= 0xEC ^ 0x11) bytes.Add((byte)pad);

            return new QrCodewords(version, AddErrorCorrection(bytes.ToArray(), version, level));
        }
        throw new BarcodeDataException("The data is too long for a QR code (the largest symbol holds about 7000 digits, 4000 letters or 2900 bytes at the lowest error correction). Shorten the data or choose a lower error correction level.");
    }

    /// <summary>Splits the data into blocks, adds Reed-Solomon codewords to each and interleaves them as the standard requires.</summary>
    private static byte[] AddErrorCorrection(byte[] data, int version, QrLevel level)
    {
        var blocks = BlockCount[(int)level][version - 1];
        var ecLen = EcPerBlock[(int)level][version - 1];
        var rawCodewords = RawDataModules(version) / 8;
        var shortBlocks = blocks - rawCodewords % blocks;            // the first blocks are one data byte shorter
        var shortTotal = rawCodewords / blocks;                       // codewords per short block incl. error correction

        var dataBlocks = new List<byte[]>();
        var ecBlocks = new List<byte[]>();
        var offset = 0;
        for (var i = 0; i < blocks; i++)
        {
            var dataLen = shortTotal - ecLen + (i < shortBlocks ? 0 : 1);
            var block = data[offset..(offset + dataLen)];
            offset += dataLen;
            dataBlocks.Add(block);
            ecBlocks.Add(Field.Remainder(block, ecLen, 0));
        }

        // Column-wise: first codeword of every block, then the second, ...; short blocks simply run out one column early.
        var result = new List<byte>(rawCodewords);
        var maxData = dataBlocks.Max(b => b.Length);
        for (var i = 0; i < maxData; i++)
            foreach (var b in dataBlocks)
                if (i < b.Length) result.Add(b[i]);
        for (var i = 0; i < ecLen; i++)
            foreach (var b in ecBlocks) result.Add(b[i]);
        return result.ToArray();
    }
}
