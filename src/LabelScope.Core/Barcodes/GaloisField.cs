namespace LabelScope.Core.Barcodes;

/// <summary>
/// Arithmetic in GF(256) and the Reed-Solomon error correction used by QR Code (polynomial 0x11D, roots from
/// alpha^0) and Data Matrix ECC 200 (polynomial 0x12D, roots from alpha^1).
/// </summary>
internal sealed class GaloisField
{
    private readonly int[] _exp = new int[512]; // doubled so that products need no modulo
    private readonly int[] _log = new int[256];

    /// <summary>Builds the log and antilog tables for the given primitive polynomial.</summary>
    public GaloisField(int primitivePolynomial)
    {
        // alpha = 2: each power is the previous one shifted left, reduced by the polynomial when it overflows 8 bits.
        var x = 1;
        for (var i = 0; i < 255; i++)
        {
            _exp[i] = x;
            _log[x] = i;
            x <<= 1;
            if (x >= 256) x ^= primitivePolynomial;
        }
        for (var i = 255; i < 512; i++) _exp[i] = _exp[i - 255];
    }

    private int Multiply(int a, int b) => a == 0 || b == 0 ? 0 : _exp[_log[a] + _log[b]];

    /// <summary>
    /// The <paramref name="ecCount"/> error correction codewords of <paramref name="data"/>: the remainder of the
    /// data polynomial (first byte = highest power) times x^ecCount divided by the generator whose roots are
    /// alpha^firstRoot ... alpha^(firstRoot + ecCount - 1).
    /// </summary>
    public byte[] Remainder(byte[] data, int ecCount, int firstRoot)
    {
        // Every symbology that calls this has at least one error correction codeword; zero is a programming error.
        ArgumentOutOfRangeException.ThrowIfLessThan(ecCount, 1);

        // Generator polynomial g(x) = product of (x + root); coefficients stored lowest power first.
        var gen = new int[ecCount + 1];
        gen[0] = 1;
        for (var i = 0; i < ecCount; i++)
        {
            var root = _exp[(firstRoot + i) % 255];
            for (var k = i + 1; k >= 1; k--) gen[k] = gen[k - 1] ^ Multiply(root, gen[k]);
            gen[0] = Multiply(root, gen[0]);
        }

        // Long division one data byte at a time (the usual shift register). rem[0] is the highest power; the
        // generator is monic, so only its lower ecCount coefficients take part.
        var rem = new int[ecCount];
        foreach (var b in data)
        {
            var factor = b ^ rem[0];
            Array.Copy(rem, 1, rem, 0, ecCount - 1);
            rem[ecCount - 1] = 0;
            for (var i = 0; i < ecCount; i++) rem[i] ^= Multiply(gen[ecCount - 1 - i], factor);
        }
        return rem.Select(v => (byte)v).ToArray();
    }
}
