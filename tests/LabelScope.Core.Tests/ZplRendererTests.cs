// tests/LabelScope.Core.Tests/ZplRendererTests.cs
using System.Diagnostics;
using LabelScope.Core.Rendering;
using SkiaSharp;
using Xunit;

namespace LabelScope.Core.Tests;

public sealed class ZplRendererTests
{
    private static readonly RenderOptions Opt = new();

    private static SKBitmap Decode(RenderedLabel l) => SKBitmap.Decode(l.PngBytes);
    private static bool IsBlack(SKBitmap b, int x, int y) => b.GetPixel(x, y).Red < 60;
    private static bool IsWhite(SKBitmap b, int x, int y) => b.GetPixel(x, y).Red > 200;

    [Fact]
    public void LabelSize_ComesFromPwAndLl()
    {
        var r = new ZplRenderer().Render("^XA^PW100^LL50^XZ", Opt);

        var label = Assert.Single(r.Labels);
        Assert.Equal(100, label.WidthDots);
        Assert.Equal(50, label.HeightDots);
    }

    [Fact]
    public void LabelSize_FallsBackToOptions()
    {
        var r = new ZplRenderer().Render("^XA^XZ", new RenderOptions(203, 101.6, 152.4));

        Assert.Equal(812, r.Labels[0].WidthDots);   // 101.6 mm at 203 dpi
        Assert.Equal(1218, r.Labels[0].HeightDots); // 152.4 mm at 203 dpi
    }

    [Fact]
    public void GraphicBox_IsDrawnAtFieldOrigin_PlusLabelHome()
    {
        var r = new ZplRenderer().Render("^XA^PW100^LL100^LH10,10^FO10,10^GB30,20,20^FS^XZ", Opt);

        using var bmp = Decode(r.Labels[0]);
        Assert.True(IsBlack(bmp, 25, 25));   // inside: LH(10)+FO(10)=20..50 x, 20..40 y
        Assert.True(IsWhite(bmp, 15, 15));   // outside
        Assert.True(IsWhite(bmp, 60, 25));
    }

    [Fact]
    public void ThinBox_IsAnOutline_NotFilled()
    {
        var r = new ZplRenderer().Render("^XA^PW100^LL100^FO10,10^GB60,60,3^FS^XZ", Opt);

        using var bmp = Decode(r.Labels[0]);
        Assert.True(IsBlack(bmp, 11, 40));  // left edge
        Assert.True(IsWhite(bmp, 40, 40));  // hollow centre
    }

    [Fact]
    public void Circle_IsDrawn()
    {
        var r = new ZplRenderer().Render("^XA^PW100^LL100^FO10,10^GC40,40^FS^XZ", Opt);

        using var bmp = Decode(r.Labels[0]);
        Assert.True(IsBlack(bmp, 30, 30));  // centre of a filled circle (thickness >= radius)
        Assert.True(IsWhite(bmp, 11, 11));  // bounding-box corner stays empty
    }

    [Fact]
    public void FieldReverse_InvertsWhatIsUnderneath()
    {
        var r = new ZplRenderer().Render(
            "^XA^PW100^LL100^FO0,0^GB40,40,40^FS^FO10,10^FR^GB10,10,10^FS^XZ", Opt);

        using var bmp = Decode(r.Labels[0]);
        Assert.True(IsWhite(bmp, 15, 15));  // black inverted to white
        Assert.True(IsBlack(bmp, 30, 30));  // untouched black
    }

    [Fact]
    public void Text_DrawsInkInsideItsArea_OnlyThere()
    {
        var r = new ZplRenderer().Render("^XA^PW300^LL200^FO10,10^A0N,40,40^FDHELLO^FS^XZ", Opt);

        using var bmp = Decode(r.Labels[0]);
        var ink = 0;
        for (var y = 5; y < 70; y++)
            for (var x = 5; x < 250; x++)
                if (IsBlack(bmp, x, y)) ink++;
        Assert.True(ink > 50, "expected visible text");
        Assert.True(IsWhite(bmp, 250, 150));
    }

    [Fact]
    public void FieldBlock_WrapsTextOntoSeveralLines()
    {
        var r = new ZplRenderer().Render(
            "^XA^PW300^LL200^FO0,0^A0N,30,30^FB100,3,0,L^FDAAAA BBBB CCCC DDDD^FS^XZ", Opt);

        using var bmp = Decode(r.Labels[0]);
        var inkLowerLines = 0;
        for (var y = 40; y < 90; y++)
            for (var x = 0; x < 120; x++)
                if (IsBlack(bmp, x, y)) inkLowerLines++;
        Assert.True(inkLowerLines > 20, "text should continue on lower lines");
        Assert.True(IsWhite(bmp, 200, 20), "nothing right of the block");
    }

    [Fact]
    public void PrintQuantity_IsReported()
    {
        var r = new ZplRenderer().Render("^XA^PQ3^XZ", Opt);

        Assert.Equal(3, r.Labels[0].Copies);
    }

    [Fact]
    public void TwoLabels_GiveTwoImages()
    {
        var r = new ZplRenderer().Render("^XA^XZ^XA^XZ", Opt);

        Assert.Equal(2, r.Labels.Count);
    }

    [Fact]
    public void UnsupportedCommand_AddsWarning_WithLineNumber()
    {
        var r = new ZplRenderer().Render("^XA\n^BCN,100\n^FDX^FS\n^XZ", Opt);

        var w = Assert.Single(r.Warnings);
        Assert.Equal(2, w.Line);
        Assert.Contains("^BC", w.Message);
    }

    [Fact]
    public void CommandsWithNoVisualEffect_AreNotWarnedAbout()
    {
        var r = new ZplRenderer().Render("^XA^MMT^PR4^XZ", Opt);

        Assert.Empty(r.Warnings);
    }

    [Fact]
    public void LabelWithoutXz_IsStillDrawn_WithWarning()
    {
        var r = new ZplRenderer().Render("^XA^PW100^LL50", Opt);

        Assert.Single(r.Labels);
        Assert.Contains(r.Warnings, w => w.Message.Contains("^XZ"));
    }

    [Fact]
    public void AbsurdLabelSize_IsClamped_WithWarning()
    {
        var r = new ZplRenderer().Render("^XA^PW99999^LL0^XZ", Opt);

        Assert.Equal(8000, r.Labels[0].WidthDots);
        Assert.Equal(1, r.Labels[0].HeightDots);
        Assert.Equal(2, r.Warnings.Count);
    }

    [Fact]
    public void GarbageInput_NeverThrows()
    {
        var r = new ZplRenderer().Render("^XA^FO,^GB,,,^A^FB-5,-5,-5^PW-1^XZ ^^^~~~", Opt);

        Assert.NotNull(r);
    }

    // ---- robustness: hostile numbers must be clamped, never throw, and return promptly ----------

    /// <summary>Renders and asserts that nothing throws and the work finishes quickly.</summary>
    private static RenderResult RenderPromptly(string zpl)
    {
        var sw = Stopwatch.StartNew();
        var r = new ZplRenderer().Render(zpl, Opt);
        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"took {sw.Elapsed}");
        return r;
    }

    [Fact]
    public void NegativeCoordinates_AreAccepted()
    {
        var r = RenderPromptly("^XA^PW100^LL100^FO-5,-5^GB30,30,30^FS^XZ");

        using var bmp = Decode(r.Labels[0]);
        Assert.True(IsBlack(bmp, 5, 5));    // box spans -5..25, so this dot is inside
        Assert.True(IsWhite(bmp, 40, 40));
    }

    [Fact]
    public void HugeBox_IsClamped_AndReturnsPromptly()
    {
        var r = RenderPromptly("^XA^PW100^LL100^FO0,0^GB999999999,999999999,3^FS^XZ");

        Assert.Single(r.Labels);
    }

    [Fact]
    public void HugeCircle_IsClamped_AndReturnsPromptly()
    {
        var r = RenderPromptly("^XA^PW100^LL100^FO0,0^GC999999999,999999999^FS^XZ");

        Assert.Single(r.Labels);
    }

    [Fact]
    public void HugeFontSize_IsClamped_AndReturnsPromptly()
    {
        var r = RenderPromptly("^XA^PW100^LL100^FO0,0^A0N,99999,99999^FDWIDE TEXT^FS^XZ");

        Assert.Single(r.Labels);
    }

    [Fact]
    public void HugeCoordinates_DoNotOverflow()
    {
        var r = RenderPromptly("^XA^LH2147483647,2147483647^FO2147483647,2147483647^GB10,10,10^FS^XZ");

        Assert.Single(r.Labels);
    }

    [Theory]
    [InlineData("^FB0,0,0,L")]
    [InlineData("^FB-100,-3,-7,C")]
    [InlineData("^FB999999999,999999999,999999999,R")]
    public void OddFieldBlocks_AreHandled(string fieldBlock)
    {
        var r = RenderPromptly($"^XA^PW200^LL100^FO0,0^A0N,20,20{fieldBlock}^FDsome words to wrap^FS^XZ");

        Assert.Single(r.Labels);
    }

    [Fact]
    public void HugePrintQuantity_IsClamped()
    {
        var r = RenderPromptly("^XA^PQ99999999999999^XZ");
        Assert.Equal(1, r.Labels[0].Copies); // not a valid int, so the default is kept

        r = RenderPromptly("^XA^PQ2000000000^XZ");
        Assert.InRange(r.Labels[0].Copies, 1, 99_999_999);
    }

    [Fact]
    public void HugeTotalArea_IsCutToPixelBudget_WithWarning()
    {
        var r = RenderPromptly("^XA^PW8000^LL8000^XZ");

        var label = Assert.Single(r.Labels);
        Assert.Equal(8000, label.WidthDots);
        Assert.True((long)label.WidthDots * label.HeightDots <= 40_000_000);
        Assert.Contains(r.Warnings, w => w.Message.Contains("too large") && w.Line == 1);
    }

    [Fact]
    public void FourBySixInchLabelAt600Dpi_IsNotReduced()
    {
        var r = RenderPromptly("^XA^PW2436^LL3654^XZ");

        var label = Assert.Single(r.Labels);
        Assert.Equal(2436, label.WidthDots);
        Assert.Equal(3654, label.HeightDots);
        Assert.Empty(r.Warnings);
    }

    // ---- ^PM, ^FT graphics and default-size clamp ----------------------------------------------------------

    [Fact]
    public void PrintMirror_IsNotSupported_SoItWarns()
    {
        // ^PM mirrors the picture, so ignoring it silently would show a label that differs from the printed one.
        var r = new ZplRenderer().Render("^XA^PW100^LL100^PMY^XZ", Opt);

        Assert.Contains(r.Warnings, w => w.Message.Contains("^PM") && w.Message.Contains("not supported"));
    }

    [Theory]
    [InlineData("^MNN")]
    [InlineData("^MMT")]
    [InlineData("^MDN")]
    [InlineData("^MTT")]
    [InlineData("^PR4")]
    [InlineData("^JUS")]
    public void CommandsWithoutVisualEffect_StayQuiet(string command)
    {
        var r = new ZplRenderer().Render($"^XA^PW100^LL100{command}^XZ", Opt);

        Assert.Empty(r.Warnings);
    }

    [Fact]
    public void FieldTypesetBox_HasItsBottomLeftCornerAtTheGivenPosition()
    {
        // ^FT100 with a 20 dot high box: it fills y 80..100, not 100..120.
        var r = new ZplRenderer().Render("^XA^PW100^LL150^FT10,100^GB30,20,20^FS^XZ", Opt);

        using var bmp = Decode(r.Labels[0]);
        Assert.True(IsBlack(bmp, 20, 90));
        Assert.True(IsBlack(bmp, 20, 81));
        Assert.True(IsWhite(bmp, 20, 110)); // the place where ^FO would have drawn it
        Assert.True(IsWhite(bmp, 20, 70));
    }

    [Fact]
    public void FieldTypesetCircle_HasItsBottomLeftCornerAtTheGivenPosition()
    {
        // Diameter 40 at ^FT10,100 fills the square x 10..50, y 60..100; its centre is (30, 80).
        var r = new ZplRenderer().Render("^XA^PW100^LL150^FT10,100^GC40,40^FS^XZ", Opt);

        using var bmp = Decode(r.Labels[0]);
        Assert.True(IsBlack(bmp, 30, 80));
        Assert.True(IsWhite(bmp, 30, 120)); // where ^FO would have put the centre
        Assert.True(IsWhite(bmp, 11, 61));  // corner of the bounding box stays empty
    }

    [Fact]
    public void FieldOriginBox_IsStillDrawnBelowTheGivenPosition()
    {
        var r = new ZplRenderer().Render("^XA^PW100^LL150^FO10,100^GB30,20,20^FS^XZ", Opt);

        using var bmp = Decode(r.Labels[0]);
        Assert.True(IsBlack(bmp, 20, 110));
        Assert.True(IsWhite(bmp, 20, 90));
    }

    [Fact]
    public void DefaultWidthFromSettings_ThatIsTooLarge_IsCutWithAWarningNamingTheSetting()
    {
        var r = new ZplRenderer().Render("^XA^XZ", new RenderOptions(203, 5000, 100));

        Assert.Equal(8000, r.Labels[0].WidthDots);
        var warning = Assert.Single(r.Warnings);
        Assert.Contains("DefaultLabelWidthMm", warning.Message);
        Assert.DoesNotContain("DefaultLabelHeightMm", warning.Message);
    }

    [Fact]
    public void DefaultHeightFromSettings_ThatIsTooSmall_IsRaisedWithAWarningNamingTheSetting()
    {
        var r = new ZplRenderer().Render("^XA^XZ", new RenderOptions(203, 100, 0.001));

        Assert.Equal(1, r.Labels[0].HeightDots);
        var warning = Assert.Single(r.Warnings);
        Assert.Contains("DefaultLabelHeightMm", warning.Message);
    }

    [Fact]
    public void DefaultSizeFromSettings_ThatIsOverriddenByPwAndLl_NeedsNoWarning()
    {
        // The settings value is never used here, so complaining about it would be noise.
        var r = new ZplRenderer().Render("^XA^PW100^LL100^XZ", new RenderOptions(203, 5000, 5000));

        Assert.Empty(r.Warnings);
    }
}
