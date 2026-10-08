// tests/LabelScope.Core.Tests/ZplParserTests.cs
using LabelScope.Core.Rendering;
using Xunit;

namespace LabelScope.Core.Tests;

public sealed class ZplParserTests
{
    [Fact]
    public void SimpleLabel_IsSplitIntoCommands()
    {
        var cmds = ZplParser.Parse("^XA^FO10,20^FDHello^FS^XZ");

        Assert.Equal(new[] { "^XA", "^FO", "^FD", "^FS", "^XZ" }, cmds.Select(c => c.Name));
        Assert.Equal("10,20", cmds[1].Args);
        Assert.Equal("Hello", cmds[2].Args);
    }

    [Fact]
    public void FontCommand_HasOneCharacterName_AndFontLetterInArgs()
    {
        var cmd = ZplParser.Parse("^A0N,30,40").Single();

        Assert.Equal("^A", cmd.Name);
        Assert.Equal("0N,30,40", cmd.Args);
    }

    [Fact]
    public void CommandNames_AreUpperCased()
    {
        Assert.Equal("^XA", ZplParser.Parse("^xa").Single().Name);
    }

    [Fact]
    public void LineNumbers_StartAtOne_AndFollowNewlines()
    {
        var cmds = ZplParser.Parse("^XA\r\n^FO1,1\r\n^FDx^FS\r\n^XZ");

        Assert.Equal(new[] { 1, 2, 3, 3, 4 }, cmds.Select(c => c.Line));
    }

    [Fact]
    public void FieldData_KeepsLeadingSpaces_ButDropsTrailingNewline()
    {
        var fd = ZplParser.Parse("^FD  padded\r\n^FS").First();

        Assert.Equal("  padded", fd.Args);
    }

    [Fact]
    public void TildeCommands_AreParsed()
    {
        Assert.Equal("~DG", ZplParser.Parse("~DGR:LOGO.GRF,10,5,").Single().Name);
    }

    [Fact]
    public void TextWithoutCommands_ReturnsNothing()
    {
        Assert.Empty(ZplParser.Parse("hello world"));
        Assert.Empty(ZplParser.Parse(""));
    }

    [Theory]
    [InlineData("^", 0)]
    [InlineData("~", 0)]
    [InlineData("^FO10,20^", 1)]
    [InlineData("^A", 1)]
    [InlineData("^X", 1)]
    public void TruncatedInput_AtEndOfText_DoesNotThrow(string zpl, int expectedCount)
    {
        // A command marker at the very end of the text has no name; the parser must stop
        // cleanly instead of reading past the end of the string.
        var cmds = ZplParser.Parse(zpl);

        Assert.Equal(expectedCount, cmds.Count);
    }
}
