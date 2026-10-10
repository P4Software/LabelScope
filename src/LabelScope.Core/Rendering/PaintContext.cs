using LabelScope.Core.Fonts;
using LabelScope.Core.Memory;

namespace LabelScope.Core.Rendering;

/// <summary>What one <see cref="ZplRenderer.Render"/> call shares with the painters of its labels.</summary>
internal sealed class PaintContext(PrinterMemory memory, List<string> memoryNotes, FontLibrary fonts)
{
    /// <summary>The session's printer memory.</summary>
    public PrinterMemory Memory { get; } = memory;

    /// <summary>Plain-language notes about what this job stored or deleted; they end up in <see cref="RenderResult.MemoryNotes"/>.</summary>
    public List<string> MemoryNotes { get; } = memoryNotes;

    /// <summary>Fonts from the FontsFolder setting.</summary>
    public FontLibrary Fonts { get; } = fonts;

    /// <summary>
    /// A ^CI sent outside a label (set number and its line), for the next label only; null when none is waiting. The
    /// listener hands each label over with whatever came before its ^XA, so applying it to the next label alone gives
    /// the same warnings whether a send is drawn label by label or as one job.
    /// </summary>
    public (int Set, int Line)? PendingCharacterSet { get; set; }
}
