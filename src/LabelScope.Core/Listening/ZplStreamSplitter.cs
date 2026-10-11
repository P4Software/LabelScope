using System.Text;
using LabelScope.Core.Rendering;

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
/// <para>
/// The splitter follows ^CC, ^CT and ^CD (and their ~ forms) the way <see cref="ZplParser"/> reads them: after
/// <c>~CC/</c> a label ends at <c>/XZ</c>. Every byte is read once, with a small reading state that survives between
/// chunks, so a command cut by a chunk boundary is still seen and the cost stays linear however small the chunks are.
/// The characters are kept for this connection only: another connection starts from the defaults. A label that never
/// ends is delivered when its connection closes, unless it grew past <see cref="MaxPendingChars"/> first: then it is
/// discarded with <see cref="ZplTooLargeException"/>. Only printable ASCII is accepted as a new character
/// (<see cref="ZplPrefixes.TryChange"/>), so the bytes read here and the characters the parser reads agree.
/// </para>
/// <para>
/// Names are read as the parser reads them: case is ignored and line breaks inside a name are skipped, so
/// <c>^X</c>, a line break and <c>Z</c> also end a label. The binary data a ^GF or ~DY header announces (format B or
/// C) is skipped by its declared byte count, as in the parser, so bytes inside a graphic or font download that look
/// like <c>^XZ</c> or <c>~CC</c> neither end the label nor change the prefix.
/// </para>
/// <para>
/// The end marker is searched as plain ASCII bytes, which is safe in UTF-8 and in the Windows code pages
/// alike (no multi-byte UTF-8 character contains the bytes of <c>^</c>, <c>X</c> or <c>Z</c>). Each finished
/// label is then decoded as a whole: strictly as UTF-8 first, and as Windows-1252 when that fails. Many label
/// programs and the Windows text-only printer driver send Windows-1252 text (for example "Año" as the single
/// byte 0xF1), which is not valid UTF-8. Decoding a whole label at once also means a character cut by a chunk
/// boundary needs no special handling.
/// </para>
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

    // Everything before this index was already read; the reading state below says where in a command it stopped.
    private int _scanned;

    // The prefixes and delimiter in force on this connection, changed by ^CC, ^CT and ^CD.
    private ZplPrefixes _prefixes = ZplPrefixes.Default;

    // Where reading stopped inside a command when the last chunk ended, so nothing is read twice.
    private Reading _reading;
    private bool _formatMarker;   // the command being named started with the format prefix
    private bool _hasFirstLetter;
    private byte _firstLetter;
    private char _changeKind;     // 'C', 'T' or 'D' while waiting for a change command's character

    // The header of a ^GF or ~DY being read (see BinaryDownloads.Layout), and the binary bytes still to skip.
    private int _headerBytes;
    private int _headerField;
    private int _headerDelimiters, _formatIndex, _lengthIndex;
    private readonly StringBuilder _formatField = new();
    private readonly StringBuilder _lengthField = new();
    private long _binaryLeft;

    /// <summary>What the splitter was in the middle of when the last chunk ended.</summary>
    private enum Reading
    {
        /// <summary>Between commands, or inside a command's parameters.</summary>
        Text,
        /// <summary>Right after a prefix: reading the command's name.</summary>
        Name,
        /// <summary>After ^CC, ^CT or ^CD: waiting for the new character.</summary>
        ChangeCharacter,
        /// <summary>After ^GF or ~DY: reading the header that may announce binary data.</summary>
        Header,
        /// <summary>Inside announced binary data: its bytes are skipped, whatever they look like.</summary>
        Binary,
    }

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

        // Only the newly added data is read; the reading state carries a command cut by the chunk boundary.
        var i = _scanned;
        while (i < _length)
        {
            if (!Step(ref i)) continue;
            // Strings are built only for completed labels, not for the pending data.
            completed.Add(Decode(start, i - start));
            start = i;
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
            // Without a cap a connection that never sends ^XZ would grow memory forever. The prefixes are printer
            // state and stay; a command cut off with the discarded data is forgotten with it.
            _length = 0;
            _scanned = 0;
            _reading = Reading.Text;
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
        _reading = Reading.Text;
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

    /// <summary>
    /// Reads from <paramref name="i"/> and moves it on: past many bytes at once between commands, otherwise past at most
    /// one byte. A prefix that cuts a name short, or a refused prefix character, is left where it is, so the next step
    /// reads it as a new command, as the parser does. Returns true when a label ended just before the new
    /// <paramref name="i"/>. Names are read like the parser reads them: line breaks inside are skipped, case is
    /// ignored, and ^A has a single letter.
    /// </summary>
    private bool Step(ref int i)
    {
        var format = (byte)_prefixes.Format;
        var control = (byte)_prefixes.Control;
        switch (_reading)
        {
            case Reading.Text:
            {
                // The common case: a vectorised search to the next prefix, so ordinary data costs what it did before.
                var next = _bytes.AsSpan(i, _length - i).IndexOfAny(format, control);
                if (next < 0)
                {
                    i = _length;
                    return false;
                }
                i += next;
                _formatMarker = _bytes[i] == format;
                _hasFirstLetter = false;
                _reading = Reading.Name;
                i++;
                return false;
            }

            case Reading.Name:
            {
                var b = _bytes[i];
                if (b == format || b == control)
                {
                    _reading = Reading.Text; // the name was cut short: a new command starts at this byte
                    return false;
                }
                i++;
                if (b is (byte)'\r' or (byte)'\n') return false;
                if (!_hasFirstLetter)
                {
                    _hasFirstLetter = true;
                    _firstLetter = b;
                    // ^A takes one letter; what follows is its font, never part of a name.
                    if (_formatMarker && (b | 0x20) == 'a') _reading = Reading.Text;
                    return false;
                }
                _reading = Reading.Text;
                var first = _firstLetter | 0x20;
                var second = b | 0x20;
                if (_formatMarker && first == 'x' && second == 'z') return true;
                if (_formatMarker && first == 'g' && second == 'f') StartHeader("^GF");
                else if (!_formatMarker && first == 'd' && second == 'y') StartHeader("~DY");
                else if (ZplPrefixes.IsChange((char)_firstLetter, (char)b, out var kind))
                {
                    _changeKind = kind;
                    _reading = Reading.ChangeCharacter;
                }
                return false;
            }

            case Reading.Header:
                return StepHeader(ref i, format, control);

            case Reading.Binary:
            {
                // Skipped in bulk: a large font download costs one step per chunk, not one per byte.
                var take = (int)Math.Min(_binaryLeft, _length - i);
                i += take;
                _binaryLeft -= take;
                if (_binaryLeft == 0) _reading = Reading.Text;
                return false;
            }

            default: // Reading.ChangeCharacter
            {
                var b = _bytes[i];
                if (b is (byte)'\r' or (byte)'\n')
                {
                    i++; // as in the parser, a line break before the character is skipped
                    return false;
                }
                _reading = Reading.Text;
                if (_prefixes.TryChange(_changeKind, (char)b, out var changed))
                {
                    _prefixes = changed;
                    i++;
                }
                else if (b != format && b != control)
                {
                    i++; // a refused character is swallowed, except a prefix, which starts the next command
                }
                return false;
            }
        }
    }

    /// <summary>Starts reading the header of <paramref name="name"/> (^GF or ~DY), which may announce binary data.</summary>
    private void StartHeader(string name)
    {
        (_headerDelimiters, _formatIndex, _lengthIndex) = BinaryDownloads.Layout(name);
        _headerBytes = 0;
        _headerField = 0;
        _formatField.Clear();
        _lengthField.Clear();
        _reading = Reading.Header;
    }

    /// <summary>
    /// One byte of a ^GF or ~DY header, read by the rules of <see cref="BinaryDownloads.TryMeasure(string, int, string, ZplPrefixes, out int, out long)"/>:
    /// at most <see cref="BinaryDownloads.MaxHeaderChars"/> bytes, line breaks skipped, a prefix ending the header without
    /// data. When the last header delimiter announces binary data, its bytes are skipped next, so a <c>^XZ</c> or a
    /// <c>~CC</c> inside a graphic or a font download neither ends the label nor changes the prefix. The parser skips
    /// the very same bytes.
    /// </summary>
    private bool StepHeader(ref int i, byte format, byte control)
    {
        // Not a header after all: the byte is read again as ordinary text (or as the next command), as the parser does.
        if (_headerBytes >= BinaryDownloads.MaxHeaderChars)
        {
            _reading = Reading.Text;
            return false;
        }
        var b = _bytes[i];
        if (b == format || b == control)
        {
            _reading = Reading.Text;
            return false;
        }
        _headerBytes++;
        i++;
        if (b is (byte)'\r' or (byte)'\n') return false;
        if (b != (byte)_prefixes.Delimiter)
        {
            // Only the two parameters that matter are kept; a header is at most 256 bytes, so they stay small.
            if (_headerField == _formatIndex) _formatField.Append((char)b);
            else if (_headerField == _lengthIndex) _lengthField.Append((char)b);
            return false;
        }
        if (++_headerField < _headerDelimiters) return false;

        if (BinaryDownloads.TryReadLength(_formatField.ToString(), _lengthField.ToString(), out var length))
        {
            _binaryLeft = length;
            _reading = Reading.Binary;
        }
        else
        {
            _reading = Reading.Text;
        }
        return false;
    }
}
