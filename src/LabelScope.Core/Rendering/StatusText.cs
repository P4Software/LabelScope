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
        return notes.Count > 3 ? $"{text} (and {notes.Count - 3} more)" : text;
    }
}
