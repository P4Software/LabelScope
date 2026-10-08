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
    private const string EndMarker = "^XZ";

    // A stateful Decoder keeps a multi-byte character that is cut by a chunk boundary.
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly StringBuilder _buffer = new();

    // Everything before this index was already searched. We back up 2 characters on the next
    // search so a marker split as "^X" | "Z" across chunks is still found.
    private int _scanned;

    /// <summary>Adds received bytes and returns every label that this data completed.</summary>
    public IReadOnlyList<string> Feed(byte[] data, int count)
    {
        var chars = new char[_decoder.GetCharCount(data, 0, count)];
        var written = _decoder.GetChars(data, 0, count, chars, 0);
        _buffer.Append(chars, 0, written);

        var text = _buffer.ToString();
        var completed = new List<string>();
        var start = 0;
        var searchFrom = Math.Max(0, _scanned - (EndMarker.Length - 1));

        while (true)
        {
            var at = text.IndexOf(EndMarker, searchFrom, StringComparison.OrdinalIgnoreCase);
            if (at < 0) break;

            var end = at + EndMarker.Length;
            completed.Add(text[start..end].Trim());
            start = end;
            searchFrom = end;
        }

        _buffer.Clear().Append(text, start, text.Length - start);
        _scanned = _buffer.Length;
        return completed;
    }

    /// <summary>
    /// Returns text left over when the connection closed without a final <c>^XZ</c>
    /// (or null if there is none) and resets the splitter.
    /// </summary>
    public string? Flush()
    {
        var rest = _buffer.ToString().Trim();
        _buffer.Clear();
        _scanned = 0;
        _decoder.Reset();
        return rest.Length == 0 ? null : rest;
    }
}
