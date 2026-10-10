using System.IO.Compression;

namespace LabelScope.Core.Graphics;

/// <summary>
/// Zebra's ZB64 encodings (reference part 7): ":B64:data:crc" is Base64, ":Z64:data:crc" is zlib-compressed
/// (LZ77, "as in PKZIP and PNG") and then Base64. The four-digit check value is read but not verified, because
/// Zebra does not publish the CRC algorithm (plan Decision 2); zlib's own Adler-32 still protects Z64 data.
/// </summary>
internal static class Zb64
{
    private const string NoteTooMuch = "The graphic data unpacks to more than the size given; the extra was ignored.";
    private const string ErrorDamaged = "The compressed graphic data (:Z64:) is damaged and cannot be unpacked.";

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
            throw new GraphicDataException("The graphic data does not start with :Z64: or :B64:, so it cannot be read.");
        var text = data.AsSpan().Trim();
        var compressed = char.ToUpperInvariant(text[1]) == 'Z';
        var body = text[5..];

        ReadOnlySpan<char> payload;
        var lastColon = body.LastIndexOf(':');
        if (lastColon < 0)
        {
            payload = body;
            notes.Add("The :Z64: or :B64: graphic data has no check value (CRC) at the end; a printer may reject it.");
        }
        else
        {
            payload = body[..lastColon];
            var crc = body[(lastColon + 1)..].Trim();
            if (crc.Length != 4 || !IsHex(crc))
                notes.Add("The check value at the end of the :Z64: or :B64: graphic data is not four hexadecimal digits; a printer may reject it.");
        }

        // Base64 may be wrapped over several lines for readability; the line breaks are not data.
        var clean = new char[payload.Length];
        var n = 0;
        foreach (var c in payload)
            if (c is not (' ' or '\t' or '\r' or '\n')) clean[n++] = c;

        var raw = new byte[n / 4 * 3 + 3];
        if (!Convert.TryFromBase64Chars(clean.AsSpan(0, n), raw, out var written))
            throw new GraphicDataException("The graphic data marked :Z64: or :B64: is not valid Base64 text, so it cannot be read.");

        byte[] result;
        bool more;
        if (compressed)
        {
            if (written == 0) throw new GraphicDataException(ErrorDamaged);
            try
            {
                // Zebra calls the format zlib (LZ77 as in PKZIP and PNG) but the guide does not say whether the
                // zlib wrapper (2-byte header + Adler-32) is really present. Accept both: a valid zlib header
                // means zlib, anything else is read as raw deflate. Deciding by the header (not by trying one
                // after the other) keeps a damaged zlib stream from being re-read as raw deflate, which could
                // come out as wrong pixels instead of an error.
                var source = new MemoryStream(raw, 0, written);
                var zlib = LooksLikeZlib(raw, written);
                using Stream z = zlib
                    ? new ZLibStream(source, CompressionMode.Decompress)
                    : new DeflateStream(source, CompressionMode.Decompress);
                result = ReadCapped(z, maxBytes, out more);

                // .NET does not report a stream that simply stops early (a half-sent download) and does not always
                // check the zlib trailer, so a cut-off picture would look like a short one. When the output is
                // complete, compare it with the Adler-32 stored in the last four bytes. (Raw deflate has no
                // checksum, so a truncated raw stream can only show up as the "ended early" note below.)
                if (zlib && !more && !Adler32Matches(result, raw, written))
                    throw new InvalidDataException();
            }
            catch (InvalidDataException)
            {
                // Truncated, corrupt or checksum mismatch: .NET reports all of them this way.
                throw new GraphicDataException(ErrorDamaged);
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
            notes.Add($"The graphic data ended early ({result.Length} of {maxBytes} bytes); the rest is blank.");
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
