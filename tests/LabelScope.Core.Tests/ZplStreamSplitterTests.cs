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
}
