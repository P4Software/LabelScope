namespace LabelScope.Core.Rendering;

/// <summary>
/// How labels are drawn: the simulated printer and the label loaded in it. The loaded label size is used only for a
/// side the ZPL does not give with ^PW / ^LL (in this job or an earlier one), the way a real printer uses the label
/// it has loaded. Normally the sending program states the size in the ZPL.
/// </summary>
/// <param name="Dpi">Dots per inch of the simulated printer.</param>
/// <param name="LabelWidthMm">Width of the loaded label in millimetres (default 4 inches).</param>
/// <param name="LabelHeightMm">Length of the loaded label in millimetres (default 6 inches).</param>
public sealed record RenderOptions(int Dpi = 203, double LabelWidthMm = 101.6, double LabelHeightMm = 152.4)
{
    /// <summary>
    /// True when the job's bytes were not valid UTF-8 and were read as Windows-1252 instead (see
    /// <see cref="Listening.ZplStreamSplitter.Decode(byte[], int, int, out bool)"/>). A label that says its text is UTF-8 (^CI28) then gets a warning
    /// that accented letters may be wrong. Set per job, from what the listener or Open file reported.
    /// </summary>
    public bool TextReadAsWindows1252 { get; init; }
}

/// <summary>One rendered label.</summary>
/// <param name="PngBytes">The label image as PNG.</param>
/// <param name="WidthDots">Width in printer dots.</param>
/// <param name="HeightDots">Height in printer dots.</param>
/// <param name="Copies">Number of copies requested with ^PQ (1 when absent).</param>
/// <param name="WidthFromZpl">True when the width came from a ^PW (in this job or an earlier one); false when it is the width of the loaded label (Printer setup).</param>
/// <param name="HeightFromZpl">True when the height came from a ^LL (in this job or an earlier one); false when it is the length of the loaded label (Printer setup).</param>
/// <param name="Dpi">Dots per inch used to turn dots into millimetres (0 when unknown).</param>
public sealed record RenderedLabel(byte[] PngBytes, int WidthDots, int HeightDots, int Copies,
                                   bool WidthFromZpl = false, bool HeightFromZpl = false, int Dpi = 0)
{
    /// <summary>
    /// Every field drawn on the label, in drawing order (at most 5000, then one note that says how many there were);
    /// empty for a label that records none, such as the placeholder.
    /// </summary>
    public IReadOnlyList<LabelField> Fields { get; init; } = [];
}

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
