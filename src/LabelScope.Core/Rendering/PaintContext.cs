using LabelScope.Core.Memory;

namespace LabelScope.Core.Rendering;

/// <summary>What one <see cref="ZplRenderer.Render"/> call shares with the painters of its labels.</summary>
internal sealed class PaintContext(PrinterMemory memory, List<string> memoryNotes)
{
    /// <summary>The session's printer memory.</summary>
    public PrinterMemory Memory { get; } = memory;

    /// <summary>Plain-language notes about what this job stored or deleted; they end up in <see cref="RenderResult.MemoryNotes"/>.</summary>
    public List<string> MemoryNotes { get; } = memoryNotes;
}
