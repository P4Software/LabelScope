using System.IO.Compression;

namespace LabelScope.Core.Graphics;

/// <summary>
/// Zebra's ZB64 encodings (as the ZPL guide describes them): ":B64:data:crc" is Base64, ":Z64:data:crc" is zlib-compressed
/// (LZ77, "as in PKZIP and PNG") and then Base64. The four-digit check value is read but not verified, because
/// Zebra does not publish the CRC algorithm, so it cannot be checked; zlib's own Adler-32 still protects Z64 data.
/// </summary>
internal static class Zb64
{
    private static string NoteTooMuch => Text.Get("Graphics_Zb64_TooMuch");
    private static string ErrorDamaged => Text.Get("Graphics_Zb64_Damaged");

    /// <summary>True when <paramref name="data"/> starts with ":Z64:" or ":B64:" (any letter case).</summary>
    public static bool IsZb64(ReadOnlySpan<char> data)
    {
        var t = data.TrimStart();
        return t.Length >= 5 && t[0] == ':' && t[4] == ':' &&
               (t[1..4].Equals("Z64", StringComparison.OrdinalIgnoreCase) || t[1..4].Equals("B64", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Decodes ZB64 data. With <paramref name="exact"/> the result is exactly <paramref name="maxBytes"/> long (a
    /// bitmap of a declared size); otherwise it is at most that long (a whole file). Never reads more than
    /// <paramref name="maxBytes"/> + 1 bytes out of the decompressor, so compressed data cannot expand without limit.
    /// </summary>
    /// <param name="data">The whole field, starting with ":Z64:" or ":B64:".</param>
    /// <param name="maxBytes">The declared size. Values below 0 count as 0 and values above
    /// <see cref="GraphicData.MaxFileBytes"/> are lowered to it, so a hostile size can never drive an allocation.</param>
    /// <param name="exact">True to pad short data with zero bytes (white) up to <paramref name="maxBytes"/>.</param>
    /// <param name="notes">Receives plain-language remarks about harmless oddities.</param>
    /// <exception cref="GraphicDataException">The Base64 text or the compressed data is damaged.</exception>
    public static byte[] Decode(string data, long maxBytes, bool exact, ICollection<string> notes)
    {
        // Bounded here as well as by the callers: this is the one place that allocates from a declared size.
        maxBytes = Math.Clamp(maxBytes, 0, GraphicData.MaxFileBytes);

        if (!IsZb64(data))
            throw new GraphicDataException(Text.Get("Graphics_Zb64_NoHeader"));
        var text = data.AsSpan().Trim();
        var compressed = char.ToUpperInvariant(text[1]) == 'Z';
        var body = text[5..];

        ReadOnlySpan<char> payload;
        var lastColon = body.LastIndexOf(':');
        if (lastColon < 0)
        {
            payload = body;
            notes.Add(Text.Get("Graphics_Zb64_NoCrc"));
        }
        else
        {
            payload = body[..lastColon];
            var crc = body[(lastColon + 1)..].Trim();
            if (crc.Length != 4 || !IsHex(crc))
                notes.Add(Text.Get("Graphics_Zb64_BadCrc"));
        }

        // Base64 may be wrapped over several lines for readability; the line breaks are not data.
        var clean = new char[payload.Length + 2];
        var n = 0;
        foreach (var c in payload)
            if (c is not (' ' or '\t' or '\r' or '\n')) clean[n++] = c;
        // Some encoders leave out the closing '=' padding. Two or three characters left over still hold whole bytes,
        // so the padding is put back; one left over cannot be Base64 either way and stays an error below.
        if (n % 4 is 2 or 3)
            while (n % 4 != 0) clean[n++] = '=';

        var raw = new byte[n / 4 * 3 + 3];
        if (!Convert.TryFromBase64Chars(clean.AsSpan(0, n), raw, out var written))
            throw new GraphicDataException(Text.Get("Graphics_Zb64_NotBase64"));

        byte[]? result;
        bool more;
        if (compressed)
        {
            if (written == 0) throw new GraphicDataException(ErrorDamaged);

            // Zebra calls the format zlib (LZ77 as in PKZIP and PNG) but the guide does not say whether the zlib
            // wrapper (2-byte header + Adler-32) is really present, so both are accepted. Data whose first two bytes
            // pass the zlib header check is read as zlib first. About 1 in 500 raw deflate streams also pass that
            // check by chance, so when the zlib reading fails it is tried once more as raw deflate. A damaged zlib
            // stream practically never survives that second reading: its header bytes, read as deflate, open a
            // stored block whose length must equal the complement of the next two bytes. A stream that unpacked
            // completely but whose checksum is wrong is practically certain to be damaged zlib, and is not tried again.
            result = null;
            more = false;
            if (LooksLikeZlib(raw, written))
            {
                try
                {
                    using var z = new ZLibStream(new MemoryStream(raw, 0, written), CompressionMode.Decompress);
                    result = ReadCapped(z, maxBytes, out more);
                }
                catch (InvalidDataException)
                {
                    result = null; // not zlib after all: try raw deflate below
                }
                // .NET does not report a stream that simply stops early (a half-sent download) and does not always
                // check the zlib trailer, so a cut-off picture would look like a short one. When the output is
                // complete, compare it with the Adler-32 stored in the last four bytes. Bytes a sender added after
                // the zlib stream would sit where the checksum is expected and fail this test too; Zebra's own
                // encoder writes nothing after the checksum, so such data is reported as damaged.
                if (result is not null && !more && !Adler32Matches(result, raw, written))
                    throw new GraphicDataException(ErrorDamaged);
            }
            if (result is null)
            {
                try
                {
                    using var d = new DeflateStream(new MemoryStream(raw, 0, written), CompressionMode.Decompress);
                    result = ReadCapped(d, maxBytes, out more);
                }
                catch (InvalidDataException)
                {
                    // Truncated or corrupt: .NET reports both this way. Raw deflate has no checksum, so a truncated
                    // raw stream that happens to end cleanly can only show up as the "ended early" note below.
                    throw new GraphicDataException(ErrorDamaged);
                }
            }
        }
        else
        {
            more = written > maxBytes;
            result = raw.AsSpan(0, (int)Math.Min(written, maxBytes)).ToArray();
        }

        if (more)
            notes.Add(NoteTooMuch);
        if (exact && result.LongLength < maxBytes)
        {
            notes.Add(Text.Get("Graphics_Zb64_EndedEarly", result.Length, maxBytes));
            Array.Resize(ref result, (int)maxBytes);
        }
        return result;
    }

    /// <summary>RFC 1950: method 8 (deflate) in the low nibble of the first byte, and the first two bytes divisible by 31.</summary>
    private static bool LooksLikeZlib(byte[] raw, int length) =>
        length >= 2 && (raw[0] & 0x0F) == 8 && ((raw[0] << 8) | raw[1]) % 31 == 0;

    /// <summary>True when the big-endian Adler-32 in the last four bytes of the zlib stream matches <paramref name="output"/>.</summary>
    private static bool Adler32Matches(byte[] output, byte[] zlib, int length)
    {
        if (length < 6) return false;
        // long arithmetic: the running sums stay below 2^32 only because of the modulo after every byte.
        long a = 1, b = 0;
        foreach (var x in output)
        {
            a = (a + x) % 65521;
            b = (b + a) % 65521;
        }
        var stored = ((long)zlib[length - 4] << 24) | ((long)zlib[length - 3] << 16) | ((long)zlib[length - 2] << 8) | zlib[length - 1];
        return stored == ((b << 16) | a);
    }

    /// <summary>Reads at most <paramref name="cap"/> bytes in chunks and reports whether more data was waiting.</summary>
    private static byte[] ReadCapped(Stream source, long cap, out bool more)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        while (ms.Length < cap)
        {
            var read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, cap - ms.Length));
            if (read == 0) break;
            ms.Write(buffer, 0, read);
        }
        // One probe byte tells "exactly the size" from "longer"; it never costs more than one byte.
        more = ms.Length >= cap && source.ReadByte() >= 0;
        return ms.ToArray();
    }

    private static bool IsHex(ReadOnlySpan<char> text)
    {
        foreach (var c in text)
            if (!char.IsAsciiHexDigit(c)) return false;
        return true;
    }
}
