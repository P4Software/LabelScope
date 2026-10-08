// src/LabelScope.Core/Rendering/RenderModels.cs
namespace LabelScope.Core.Rendering;

/// <summary>Defaults used when the ZPL itself does not give a label size.</summary>
/// <param name="Dpi">Dots per inch of the simulated printer.</param>
/// <param name="LabelWidthMm">Default label width in millimetres.</param>
/// <param name="LabelHeightMm">Default label height in millimetres.</param>
public sealed record RenderOptions(int Dpi = 203, double LabelWidthMm = 101.6, double LabelHeightMm = 152.4);

/// <summary>One rendered label.</summary>
/// <param name="PngBytes">The label image as PNG.</param>
/// <param name="WidthDots">Width in printer dots.</param>
/// <param name="HeightDots">Height in printer dots.</param>
/// <param name="Copies">Number of copies requested with ^PQ (1 when absent).</param>
public sealed record RenderedLabel(byte[] PngBytes, int WidthDots, int HeightDots, int Copies);

/// <summary>Something in the ZPL that could not be drawn as written.</summary>
/// <param name="Line">1-based line in the ZPL text.</param>
/// <param name="Message">Plain-language explanation.</param>
public sealed record RenderWarning(int Line, string Message);

/// <summary>Everything the renderer produced for one piece of ZPL text.</summary>
/// <param name="Labels">One image per ^XA..^XZ block.</param>
/// <param name="Warnings">Problems found, in the order they occurred.</param>
public sealed record RenderResult(IReadOnlyList<RenderedLabel> Labels, IReadOnlyList<RenderWarning> Warnings);
