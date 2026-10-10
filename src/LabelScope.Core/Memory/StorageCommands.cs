using LabelScope.Core.Graphics;
using LabelScope.Core.Rendering;

namespace LabelScope.Core.Memory;

/// <summary>
/// Commands that act on printer memory instead of drawing. They are handled wherever they appear: outside a label
/// (the usual case: a download is often a job of its own) and inside one.
/// </summary>
internal static class StorageCommands
{
    /// <summary>Handles <paramref name="cmd"/> when it is a storage command; returns false for any other command.</summary>
    public static bool TryHandle(ZplCommand cmd, PaintContext context, List<RenderWarning> warnings)
    {
        switch (cmd.Name)
        {
            case "~DG":
                DownloadGraphic(cmd, context, warnings);
                return true;
            case "^ID":
                Delete(cmd, context);
                return true;
            case "~EG":
                warnings.Add(new(cmd.Line, "~EG (erase graphics) is described in the ZPL guide only as \"see ^ID\", so LabelScope did not erase anything. Use ^IDR:*.GRF to delete stored graphics."));
                return true;
            case "~DN":
                // Ends a ~DG download early. Each command is complete by the time LabelScope reads it, so nothing is pending.
                return true;
            default:
                return false;
        }
    }

    /// <summary>~DGd:o.x,t,w,data: stores a bitmap given as (compressed) ASCII hex or ZB64.</summary>
    private static void DownloadGraphic(ZplCommand cmd, PaintContext context, List<RenderWarning> warnings)
    {
        var a = ZplArgs.SplitFirst(cmd.Args, 3);
        // The guide: ".GRF is automatically appended" when another extension is given, so a ~DG graphic is always a GRF.
        var name = ObjectName.Parse(a[0], "GRF", 'R') with { Extension = "GRF" };
        var label = $"~DG {name.Display}";

        if (a.Length < 4 || !ZplArgs.TryLong(a, 1, out var total) || !ZplArgs.TryLong(a, 2, out var perRow))
        {
            warnings.Add(new(cmd.Line, $"{label}: The download needs the total byte count and the bytes per row before the data; nothing was stored."));
            return;
        }
        if (!GraphicLimits.TryRows(total, perRow, out var rows, out var problem, out var sizeNote))
        {
            warnings.Add(new(cmd.Line, $"{label}: The graphic was not stored because {problem}."));
            return;
        }

        var notes = new List<string>();
        if (sizeNote is not null) notes.Add(sizeNote);
        MonoImage image;
        try
        {
            image = GraphicData.DecodeBitmap(a[3], (int)perRow, rows, notes);
        }
        catch (GraphicDataException ex)
        {
            warnings.Add(new(cmd.Line, $"{label}: {ex.Message} Nothing was stored."));
            return;
        }
        foreach (var note in notes) warnings.Add(new(cmd.Line, $"{label}: {note}"));
        StoreGraphic(cmd, context, warnings, name, image, label);
    }

    /// <summary>Stores a decoded graphic and records a note, or a warning when memory refuses it.</summary>
    internal static void StoreGraphic(ZplCommand cmd, PaintContext context, List<RenderWarning> warnings, ObjectName name, MonoImage image, string label)
    {
        var refusal = context.Memory.Store(name, new StoredGraphic(image));
        if (refusal is not null)
        {
            warnings.Add(new(cmd.Line, $"{label}: {refusal}"));
            return;
        }
        // The guide allows 1-8 characters and does not say whether a printer refuses or shortens a longer name, so the
        // warning says "may". LabelScope keeps the full name so the preview still works.
        if (name.IsLongerThanZebraAllows)
            warnings.Add(new(cmd.Line, $"{label}: The name is longer than the 8 characters a printer accepts, so a real printer may refuse or shorten it. LabelScope stored it anyway; use a name of 8 characters or fewer."));
        context.MemoryNotes.Add($"Stored the graphic {(name.Drive is null ? name.OnDrive('R') : name).Display} ({image.Width} x {image.Height} dots) in LabelScope's printer memory.");
    }

    /// <summary>^IDd:o.x: deletes stored objects; '*' is a wildcard. A printer ignores a name that matches nothing.</summary>
    private static void Delete(ZplCommand cmd, PaintContext context)
    {
        var pattern = ObjectName.Parse(cmd.Args.Split(',')[0], "GRF", 'R');
        var count = context.Memory.Delete(pattern);
        context.MemoryNotes.Add(count == 0
            ? $"^ID {pattern.Display}: nothing in LabelScope's printer memory matched, so nothing was deleted."
            : $"Deleted {count} object(s) matching {pattern.Display} from LabelScope's printer memory.");
    }
}
