// tests/LabelScope.Core.Tests/ZplStreamSplitterTests.cs
using System.Text;
using LabelScope.Core.Listening;
using Xunit;

namespace LabelScope.Core.Tests;

public sealed class ZplStreamSplitterTests
{
    private static IReadOnlyList<string> Feed(ZplStreamSplitter s, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return s.Feed(bytes, bytes.Length);
    }

    [Fact]
    public void OneLabel_InOneChunk()
    {
        var labels = Feed(new ZplStreamSplitter(), "^XA^FDHi^FS^XZ");
        Assert.Equal(new[] { "^XA^FDHi^FS^XZ" }, labels);
    }

    [Fact]
    public void TwoLabels_InOneChunk_AreSplit()
    {
        var labels = Feed(new ZplStreamSplitter(), "^XA^FDA^FS^XZ\r\n^XA^FDB^FS^XZ");
        Assert.Equal(2, labels.Count);
        Assert.StartsWith("^XA^FDB", labels[1]);
    }

    [Fact]
    public void XzSplitAcrossChunks_IsStillDetected()
    {
        var s = new ZplStreamSplitter();
        Assert.Empty(Feed(s, "^XA^FDHi^FS^X"));
        var labels = Feed(s, "Z");
        Assert.Single(labels);
        Assert.EndsWith("^XZ", labels[0]);
    }

    [Fact]
    public void MultiByteCharacterSplitAcrossChunks_IsDecodedCorrectly()
    {
        var s = new ZplStreamSplitter();
        var all = Encoding.UTF8.GetBytes("^XA^FDé^FS^XZ");
        var cut = Array.IndexOf(all, (byte)0xC3) + 1; // between the two bytes of "é"
        Assert.Empty(s.Feed(all[..cut], cut));
        var labels = s.Feed(all[cut..], all.Length - cut);
        Assert.Contains("é", labels.Single());
    }

    [Fact]
    public void Flush_ReturnsIncompleteLabel_ThenNull()
    {
        var s = new ZplStreamSplitter();
        Feed(s, "^XA^FDHi^FS");
        Assert.Equal("^XA^FDHi^FS", s.Flush());
        Assert.Null(s.Flush());
    }

    [Fact]
    public void Flush_WithOnlyWhitespace_ReturnsNull()
    {
        var s = new ZplStreamSplitter();
        Feed(s, "^XA^XZ\r\n  ");
        Assert.Null(s.Flush());
    }

    [Fact]
    public void LargeLabel_FedOneByteAtATime_CompletesQuicklyAsOneLabel()
    {
        // Regression guard for the quadratic cost: the old splitter copied the whole pending
        // text on every call, so a 200k-character label fed byte by byte took minutes.
        var text = "^XA^FD" + new string('A', 200_000) + "^FS^XZ";
        var bytes = Encoding.UTF8.GetBytes(text);
        var s = new ZplStreamSplitter();
        var labels = new List<string>();

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var one = new byte[1];
        foreach (var b in bytes)
        {
            one[0] = b;
            labels.AddRange(s.Feed(one, 1));
        }
        clock.Stop();

        Assert.Single(labels);
        Assert.Equal(text, labels[0]);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"Took {clock.Elapsed}.");
    }

    [Fact]
    public void MarkerSplitOneAndTwo_IsFound()
    {
        var s = new ZplStreamSplitter();
        Assert.Empty(Feed(s, "^XA^FDx^FS^"));
        var labels = Feed(s, "XZ");
        Assert.Equal(new[] { "^XA^FDx^FS^XZ" }, labels);
    }

    [Fact]
    public void ChunkEndsExactlyAfterMarker_NextLabelInNextChunk()
    {
        var s = new ZplStreamSplitter();
        Assert.Single(Feed(s, "^XA^FDA^FS^XZ"));
        var labels = Feed(s, "^XA^FDB^FS^XZ");
        Assert.Equal(new[] { "^XA^FDB^FS^XZ" }, labels);
        Assert.Null(s.Flush());
    }

    [Fact]
    public void LowercaseMarker_EndsLabel()
    {
        // Deliberate: the end marker is matched case-insensitively, so "^xz" also ends a label.
        var labels = Feed(new ZplStreamSplitter(), "^XA^FDa^FS^xz");
        Assert.Equal(new[] { "^XA^FDa^FS^xz" }, labels);
    }

    [Fact]
    public void InvalidUtf8Bytes_DoNotThrow()
    {
        // Printers can send non-UTF-8 bytes; they must be replaced, not crash the listener.
        var bytes = new byte[] { 0xFF, 0xFE }.Concat(Encoding.ASCII.GetBytes("^XA^XZ")).ToArray();
        var labels = new ZplStreamSplitter().Feed(bytes, bytes.Length);
        Assert.Single(labels);
        Assert.EndsWith("^XZ", labels[0]);
    }

    [Fact]
    public void CountZero_ReturnsEmptyList()
    {
        var s = new ZplStreamSplitter();
        Assert.Empty(s.Feed(new byte[] { (byte)'^', (byte)'X' }, 0));
        Assert.Empty(s.Feed(Array.Empty<byte>(), 0));
    }

    [Fact]
    public void ExceedingMaxPending_ThrowsPlainMessage_ThenSplitterStillWorks()
    {
        var s = new ZplStreamSplitter();
        var endless = new byte[ZplStreamSplitter.MaxPendingChars + 1]; // ASCII, no marker
        Array.Fill(endless, (byte)'A');

        var ex = Assert.Throws<ZplTooLargeException>(() => s.Feed(endless, endless.Length));
        Assert.Contains("16 MB", ex.Message);

        // The pending text was discarded, so a normal label afterwards comes back intact.
        var labels = Feed(s, "^XA^FDOK^FS^XZ");
        Assert.Equal(new[] { "^XA^FDOK^FS^XZ" }, labels);
        Assert.Null(s.Flush());
    }
}
