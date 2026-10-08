// src/LabelScope.Core/Listening/ZplStreamSplitter.cs
using System.Text;

namespace LabelScope.Core.Listening;

/// <summary>
/// Turns a stream of bytes from one TCP connection into complete ZPL labels.
/// A label ends at <c>^XZ</c>. One connection may carry many labels, and any label may
/// arrive in several chunks, so the splitter keeps the unfinished part between calls.
/// </summary>
public sealed class ZplStreamSplitter
{
    /// <summary>
    /// Largest amount of unfinished label text (in characters) the splitter keeps. A sender
    /// that never sends <c>^XZ</c> would otherwise make memory grow without limit. Above this
    /// size <see cref="Feed"/> discards the pending text and throws <see cref="ZplTooLargeException"/>.
    /// </summary>
    public const int MaxPendingChars = 16 * 1024 * 1024;

    private const string EndMarker = "^XZ";

    // A stateful Decoder keeps a multi-byte character that is cut by a chunk boundary.
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();

    // Pending text lives in a plain char array that is scanned in place. A StringBuilder or
    // string would force a full copy of the pending text on every Feed call, which is
    // quadratic for a large label that arrives in small pieces.
    private char[] _chars = new char[4096];
    private int _length;

    // Everything before this index was already searched. We back up 2 characters on the next
    // search so a marker split as "^X" | "Z" across chunks is still found.
    private int _scanned;

    /// <summary>
    /// Adds received bytes and returns every label that this data completed.
    /// Each returned label is trimmed and includes its trailing <c>^XZ</c>.
    /// </summary>
    /// <exception cref="ZplTooLargeException">
    /// The unfinished label text grew beyond <see cref="MaxPendingChars"/>. The pending text is
    /// discarded and the splitter stays usable for the next data.
    /// </exception>
    public IReadOnlyList<string> Feed(byte[] data, int count)
    {
        // Decode straight into the pending buffer so no temporary array is created per call.
        EnsureCapacity(_length + _decoder.GetCharCount(data, 0, count));
        _length += _decoder.GetChars(data, 0, count, _chars, _length);

        var completed = new List<string>();
        var start = 0;

        // Only the newly added text is scanned; the back-up covers a marker split across chunks.
        var i = Math.Max(0, _scanned - (EndMarker.Length - 1));
        while (i + EndMarker.Length <= _length)
        {
            if (IsEndMarkerAt(i))
            {
                var end = i + EndMarker.Length;
                // Strings are built only for completed labels, not for the pending text.
                completed.Add(new string(_chars, start, end - start).Trim());
                start = end;
                i = end;
            }
            else
            {
                i++;
            }
        }

        // Remove consumed text once per call (not once per label), so the next label starts at 0.
        var remaining = _length - start;
        if (start > 0)
        {
            Array.Copy(_chars, start, _chars, 0, remaining);
        }
        _length = remaining;
        _scanned = _length;

        if (_length > MaxPendingChars)
        {
            // Without a cap a connection that never sends ^XZ would grow memory forever.
            // Discard the pending text and reset the decoder so the next data starts clean.
            _length = 0;
            _scanned = 0;
            _decoder.Reset();
            throw new ZplTooLargeException();
        }

        return completed;
    }

    /// <summary>
    /// Returns text left over when the connection closed without a final <c>^XZ</c>
    /// (or null if there is none) and resets the splitter.
    /// </summary>
    public string? Flush()
    {
        var rest = new string(_chars, 0, _length).Trim();
        _length = 0;
        _scanned = 0;
        _decoder.Reset();
        return rest.Length == 0 ? null : rest;
    }

    // Grows the pending buffer by doubling so repeated small chunks cost amortised O(1) per char.
    private void EnsureCapacity(int needed)
    {
        if (_chars.Length >= needed) return;
        var size = Math.Max(needed, _chars.Length * 2);
        Array.Resize(ref _chars, size);
    }

    // Case-insensitive match of "^XZ" at index i, done char by char so no string is allocated.
    // The caller guarantees i + 3 <= _length.
    private bool IsEndMarkerAt(int i) =>
        _chars[i] == '^'
        && char.ToUpperInvariant(_chars[i + 1]) == 'X'
        && char.ToUpperInvariant(_chars[i + 2]) == 'Z';
}
