using System.Text;

namespace LabelScope.Core.Listening;

/// <summary>One label's text and how its bytes were read.</summary>
/// <param name="Text">The decoded ZPL.</param>
/// <param name="ReadAsWindows1252">True when the bytes were not valid UTF-8 and were read as Windows-1252.</param>
public sealed record DecodedZpl(string Text, bool ReadAsWindows1252);

/// <summary>
/// Turns a stream of bytes from one TCP connection into complete ZPL labels.
/// A label ends at <c>^XZ</c>. One connection may carry many labels, and any label may
/// arrive in several chunks, so the splitter keeps the unfinished part between calls.
/// </summary>
/// <remarks>
/// The end marker is searched as plain ASCII bytes, which is safe in UTF-8 and in the Windows code pages
/// alike (no multi-byte UTF-8 character contains the bytes of <c>^</c>, <c>X</c> or <c>Z</c>). Each finished
/// label is then decoded as a whole: strictly as UTF-8 first, and as Windows-1252 when that fails. Many label
/// programs and the Windows text-only printer driver send Windows-1252 text (for example "Año" as the single
/// byte 0xF1), which is not valid UTF-8. Decoding a whole label at once also means a character cut by a chunk
/// boundary needs no special handling.
/// </remarks>
public sealed class ZplStreamSplitter
{
    /// <summary>
    /// Largest amount of unfinished label data (in bytes) the splitter keeps. A sender
    /// that never sends <c>^XZ</c> would otherwise make memory grow without limit. Above this
    /// size <see cref="Feed"/> discards the pending data and throws <see cref="ZplTooLargeException"/>.
    /// </summary>
    public const int MaxPendingChars = 16 * 1024 * 1024;

    // Strict: invalid bytes throw instead of becoming U+FFFD, which is the signal to try Windows-1252.
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    // Windows-1252 lives in the code-pages package; the provider must be registered once per process.
    private static readonly Encoding Windows1252 = CreateWindows1252();

    // Pending data lives in a plain byte array that is scanned in place. A copy of the pending data on every
    // Feed call would be quadratic for a large label that arrives in small pieces.
    private byte[] _bytes = new byte[4096];
    private int _length;

    // Everything before this index was already searched. We back up 2 bytes on the next
    // search so a marker split as "^X" | "Z" across chunks is still found.
    private int _scanned;

    private static Encoding CreateWindows1252()
    {
        // Safe to call more than once, but the static initializer already guarantees it runs once.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1252);
    }

    /// <summary>
    /// Adds received bytes and returns every label that this data completed.
    /// Each returned label is trimmed and includes its trailing <c>^XZ</c>.
    /// </summary>
    /// <exception cref="ZplTooLargeException">
    /// The unfinished label grew beyond <see cref="MaxPendingChars"/> bytes. The pending data is
    /// discarded and the splitter stays usable for the next data.
    /// </exception>
    public IReadOnlyList<string> Feed(byte[] data, int count) => FeedDecoded(data, count).Select(d => d.Text).ToList();

    /// <summary>
    /// Like <see cref="Feed"/>, and also says for each label whether its bytes were read as Windows-1252, which a label
    /// that announces UTF-8 (^CI28) needs to warn about.
    /// </summary>
    /// <exception cref="ZplTooLargeException">As for <see cref="Feed"/>.</exception>
    public IReadOnlyList<DecodedZpl> FeedDecoded(byte[] data, int count)
    {
        EnsureCapacity(_length + count);
        Buffer.BlockCopy(data, 0, _bytes, _length, count);
        _length += count;

        var completed = new List<DecodedZpl>();
        var start = 0;

        // Only the newly added data is scanned; the back-up covers a marker split across chunks.
        var i = Math.Max(0, _scanned - 2);
        while (i + 3 <= _length)
        {
            if (IsEndMarkerAt(i))
            {
                var end = i + 3;
                // Strings are built only for completed labels, not for the pending data.
                completed.Add(Decode(start, end - start));
                start = end;
                i = end;
            }
            else
            {
                i++;
            }
        }

        // Remove consumed data once per call (not once per label), so the next label starts at 0.
        var remaining = _length - start;
        if (start > 0)
        {
            Buffer.BlockCopy(_bytes, start, _bytes, 0, remaining);
        }
        _length = remaining;
        _scanned = _length;

        if (_length > MaxPendingChars)
        {
            // Without a cap a connection that never sends ^XZ would grow memory forever.
            _length = 0;
            _scanned = 0;
            throw new ZplTooLargeException();
        }

        return completed;
    }

    /// <summary>
    /// Returns text left over when the connection closed without a final <c>^XZ</c>
    /// (or null if there is none) and resets the splitter.
    /// </summary>
    public string? Flush() => FlushDecoded()?.Text;

    /// <summary>Like <see cref="Flush"/>, and also says whether the bytes were read as Windows-1252.</summary>
    public DecodedZpl? FlushDecoded()
    {
        var rest = Decode(0, _length);
        _length = 0;
        _scanned = 0;
        return rest.Text.Length == 0 ? null : rest;
    }

    private DecodedZpl Decode(int start, int count)
    {
        var text = Decode(_bytes, start, count, out var windows1252);
        // A leading byte order mark would otherwise show up as an invisible character in the ZPL.
        return new DecodedZpl(text.Trim().Trim('\uFEFF').Trim(), windows1252);
    }

    /// <summary>
    /// Reads ZPL bytes the way the printer port does: strictly as UTF-8 first, and as Windows-1252 when that fails.
    /// Windows-1252 maps every byte to some character, so this never throws. Open file can use it too, so a file opened
    /// and the same file printed give the same label.
    /// </summary>
    /// <param name="bytes">The bytes.</param>
    /// <param name="start">Index of the first byte.</param>
    /// <param name="count">Number of bytes.</param>
    /// <param name="readAsWindows1252">True when the bytes were not valid UTF-8.</param>
    public static string Decode(byte[] bytes, int start, int count, out bool readAsWindows1252)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        try
        {
            readAsWindows1252 = false;
            return StrictUtf8.GetString(bytes, start, count);
        }
        catch (DecoderFallbackException)
        {
            readAsWindows1252 = true;
            return Windows1252.GetString(bytes, start, count);
        }
    }

    // Grows the pending buffer by doubling so repeated small chunks cost amortised O(1) per byte.
    private void EnsureCapacity(int needed)
    {
        if (_bytes.Length >= needed) return;
        var size = Math.Max(needed, _bytes.Length * 2);
        Array.Resize(ref _bytes, size);
    }

    // Case-insensitive match of "^XZ" at index i, done byte by byte so no string is allocated.
    // The caller guarantees i + 3 <= _length.
    private bool IsEndMarkerAt(int i) =>
        _bytes[i] == (byte)'^'
        && (_bytes[i + 1] | 0x20) == 'x'
        && (_bytes[i + 2] | 0x20) == 'z';
}
