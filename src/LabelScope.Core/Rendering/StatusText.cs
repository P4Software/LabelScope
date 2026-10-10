namespace LabelScope.Core.Rendering;

/// <summary>Short status-bar messages built from render results.</summary>
public static class StatusText
{
    /// <summary>
    /// The printer-memory notes of one job as one status line: up to three notes, then "(and N more)". A download job
    /// with hundreds of graphics must not fill the status bar. Null when there are no notes.
    /// </summary>
    /// <param name="notes">The plain-language notes from <see cref="RenderResult.MemoryNotes"/>.</param>
    public static string? ForMemoryNotes(IReadOnlyList<string> notes)
    {
        if (notes.Count == 0) return null;
        var text = string.Join(" ", notes.Take(3));
        return notes.Count > 3 ? Text.Get("Status_AndMore", text, notes.Count - 3) : text;
    }

    /// <summary>The short status-bar segment for printer memory, for example "Memory: 3 graphics, 1 font, 420 KB".</summary>
    /// <param name="summary">What printer memory holds right now.</param>
    public static string ForMemoryLine(Memory.MemorySummary summary) => Text.Get("Status_MemoryLine", summary.Describe());

    /// <summary>
    /// The status message for data without any ^XA. When the data also raised warnings, it was probably a download
    /// that failed, so the user is pointed to the warnings instead of being told only that there is no label.
    /// </summary>
    /// <param name="hasWarnings">True when the render produced warnings.</param>
    public static string ForNoLabel(bool hasWarnings) => hasWarnings
        ? Text.Get("Status_NoLabelWithWarnings")
        : Text.Get("Status_NoLabel");
}
