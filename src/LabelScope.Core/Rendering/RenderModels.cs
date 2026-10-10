namespace LabelScope.Core.Rendering;

/// <summary>How labels are drawn. The label size always comes from the ZPL (^PW, ^LL), never from a setting.</summary>
/// <param name="Dpi">Dots per inch of the simulated printer.</param>
public sealed record RenderOptions(int Dpi = 203);

/// <summary>One rendered label.</summary>
/// <param name="PngBytes">The label image as PNG.</param>
/// <param name="WidthDots">Width in printer dots.</param>
/// <param name="HeightDots">Height in printer dots.</param>
/// <param name="Copies">Number of copies requested with ^PQ (1 when absent).</param>
/// <param name="WidthFromZpl">True when the width came from a ^PW (in this job or an earlier one); false when the picture ends at the right-most thing drawn.</param>
/// <param name="HeightFromZpl">True when the height came from a ^LL (in this job or an earlier one); false when the picture ends at the lowest thing drawn.</param>
/// <param name="Dpi">Dots per inch used to turn dots into millimetres (0 when unknown).</param>
public sealed record RenderedLabel(byte[] PngBytes, int WidthDots, int HeightDots, int Copies,
                                   bool WidthFromZpl = false, bool HeightFromZpl = false, int Dpi = 0);

/// <summary>Something in the ZPL that could not be drawn as written.</summary>
/// <param name="Line">1-based line in the ZPL text.</param>
/// <param name="Message">Plain-language explanation.</param>
public sealed record RenderWarning(int Line, string Message);

/// <summary>Everything the renderer produced for one piece of ZPL text.</summary>
/// <param name="Labels">One image per ^XA..^XZ block.</param>
/// <param name="Warnings">Problems found, in the order they occurred.</param>
public sealed record RenderResult(IReadOnlyList<RenderedLabel> Labels, IReadOnlyList<RenderWarning> Warnings)
{
    /// <summary>What this ZPL stored in or deleted from printer memory, in plain language; empty when nothing.</summary>
    public IReadOnlyList<string> MemoryNotes { get; init; } = [];
}
