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

    // ---- Truncated names and line breaks -------------------------------------------------------------------

    [Fact]
    public void TruncatedName_DoesNotSwallowTheNextMarker()
    {
        var cmds = ZplParser.Parse("^X^FS");

        Assert.Equal(new[] { "^X", "^FS" }, cmds.Select(c => c.Name));
    }

    [Fact]
    public void DoubleMarker_GivesOnlyTheSecondCommand()
    {
        var cmds = ZplParser.Parse("^^FS");

        Assert.Equal(new[] { "^FS" }, cmds.Select(c => c.Name));
    }

    [Fact]
    public void OneLetterName_ThenFieldData_AreKeptApart()
    {
        var cmds = ZplParser.Parse("^F^FDtext^FS");

        Assert.Equal(new[] { "^F", "^FD", "^FS" }, cmds.Select(c => c.Name));
        Assert.Equal("text", cmds[1].Args);
    }

    [Fact]
    public void TildeBeforeCaret_IsSkipped_AndTheNextCommandStillParses()
    {
        var cmds = ZplParser.Parse("~^XA");

        Assert.Equal(new[] { "^XA" }, cmds.Select(c => c.Name));
    }

    [Fact]
    public void Null_GivesAnEmptyList()
    {
        Assert.Empty(ZplParser.Parse(null));
    }

    [Fact]
    public void LineBreaksInsideArguments_AreRemoved()
    {
        var cmd = ZplParser.Parse("^FO50,1\r\n50,10").Single();

        Assert.Equal("^FO", cmd.Name);
        Assert.Equal("50,150,10", cmd.Args);
    }

    [Fact]
    public void LineBreaksInsideFieldData_AreRemoved_ButSpacesStay()
    {
        var cmd = ZplParser.Parse("^FD  ab\r\ncd\nef\rgh^FS").First();

        Assert.Equal("  abcdefgh", cmd.Args);
    }

    [Fact]
    public void LineBreakInsideTheName_IsIgnored_AndDoesNotShiftLaterLines()
    {
        var cmds = ZplParser.Parse("^F\nO10,10^FS\n^XZ");

        Assert.Equal(new[] { "^FO", "^FS", "^XZ" }, cmds.Select(c => c.Name));
        Assert.Equal("10,10", cmds[0].Args);
        Assert.Equal(new[] { 1, 2, 3 }, cmds.Select(c => c.Line));
    }

    [Fact]
    public void LoneCarriageReturn_CountsAsALineBreak_AndCrLfCountsOnce()
    {
        var cmds = ZplParser.Parse("^XA\r^FO1,1\r\n^FS\n^XZ");

        Assert.Equal(new[] { 1, 2, 3, 4 }, cmds.Select(c => c.Line));
    }

    [Fact]
    public void CommandStartingBeforeALineBreak_KeepsItsStartLine()
    {
        var cmds = ZplParser.Parse("^FO1,\n2^FS");

        Assert.Equal(1, cmds[0].Line);
        Assert.Equal("1,2", cmds[0].Args);
        Assert.Equal(2, cmds[1].Line);
    }
}
