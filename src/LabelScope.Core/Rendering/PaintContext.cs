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
    /// The moment this job is drawn at (<see cref="RenderOptions.Clock"/>, or the PC clock read once). One value for the
    /// whole job: every label and every serial image of a job shows the same time, as ^SLS and ^SLT both do when a job
    /// is drawn at one moment.
    /// </summary>
    public DateTimeOffset Now { get; init; }
}
