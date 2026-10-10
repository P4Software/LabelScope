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
            case "~DY":
                DownloadObject(cmd, context, warnings);
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

    // ~DY types that never change a picture (certificates, WML menus, web pages, feedback files): accepted quietly,
    // because a warning about them would only hide real problems (plan Decision 12).
    private static readonly HashSet<string> NonPictureTypes = ["NRD", "PAC", "C", "F", "H"];

    /// <summary>~DYd:f,b,x,t,w,data: a general download. Bitmaps (.GRF), PNG pictures and TrueType fonts are stored.</summary>
    private static void DownloadObject(ZplCommand cmd, PaintContext context, List<RenderWarning> warnings)
    {
        var a = ZplArgs.SplitFirst(cmd.Args, 5);
        var type = a.Length > 2 ? a[2].Trim().ToUpperInvariant() : "";
        if (NonPictureTypes.Contains(type)) return;

        // The ~DY file name has no extension of its own: the type letter decides it ("any other value = .GRF").
        var extension = type switch { "P" => "PNG", "T" => "TTF", "E" => "TTE", "X" => "PCX", _ => "GRF" };
        var name = ObjectName.Parse(a[0], extension, 'R') with { Extension = extension };
        var label = $"~DY {name.Display}";
        void Warn(string message) => warnings.Add(new(cmd.Line, $"{label}: {message}"));

        switch (ZplArgs.Letter(a, 1, ' '))
        {
            case 'A' or 'P':
                break;
            case 'B':
                Warn("Binary downloads (data format B) are planned for a later release of LabelScope; nothing was stored. The same file can be sent as ASCII hex (format A) or ZB64.");
                return;
            case 'C':
                Warn("AR-compressed downloads (data format C) use a compression method that Zebra does not publish, so nothing was stored.");
                return;
            default:
                Warn("The download needs a data format (A, B, C or P) after the name; nothing was stored.");
                return;
        }
        if (type == "E") { Warn("TrueType extension files (.TTE) are not supported; nothing was stored. Send the font as a .TTF (type T) instead."); return; }
        if (type == "X") { Warn("PCX pictures are not supported; nothing was stored. Send the picture as a PNG (type P) or a GRF bitmap (type G) instead."); return; }
        if (a.Length < 6) { Warn("The download has no data; nothing was stored."); return; }

        ZplArgs.TryLong(a, 3, out var total);
        // The declared size is checked before any data is read, so a hostile "t" cannot make a big buffer.
        var cap = extension == "PNG" ? PngImage.MaxFileBytes : extension == "TTF" ? FontFile.MaxFileBytes : 0;
        if (cap > 0 && total > cap)
        {
            Warn($"The {(extension == "PNG" ? "PNG" : "font")} is {total} bytes and LabelScope reads {(extension == "PNG" ? "pictures" : "fonts")} up to {cap} bytes ({cap / (1024 * 1024)} MB). Nothing was stored.");
            return;
        }
        var notes = new List<string>();
        void FlushNotes() { foreach (var n in notes) Warn(n); notes.Clear(); }
        try
        {
            switch (extension)
            {
                case "PNG":
                {
                    var file = GraphicData.DecodeFile(a[5], total, notes);
                    if (file.LongLength > PngImage.MaxFileBytes)
                    {
                        Warn($"The PNG is {file.LongLength} bytes and LabelScope reads pictures up to {PngImage.MaxFileBytes} bytes (5 MB). Nothing was stored.");
                        return;
                    }
                    var image = PngImage.Decode(file, notes);
                    FlushNotes();
                    StoreGraphic(cmd, context, warnings, name, image, label);
                    break;
                }
                case "TTF":
                {
                    var file = GraphicData.DecodeFile(a[5], total, notes);
                    FlushNotes();
                    // Defence in depth: DecodeFile already stops at the 16 MB file ceiling, but this cap must hold on its own if that changes.
                    if (file.LongLength > FontFile.MaxFileBytes)
                    {
                        Warn($"The font is {file.LongLength} bytes and LabelScope reads fonts up to {FontFile.MaxFileBytes} bytes (16 MB). Nothing was stored.");
                        return;
                    }
                    // Only the header is checked here: the bytes are kept for later use and never parsed by a library now.
                    if (FontFile.Check(file) is { } notFont) { Warn(notFont); return; }
                    var refusal = context.Memory.Store(name, new StoredFont(file));
                    if (refusal is not null) { Warn(refusal); return; }
                    context.MemoryNotes.Add($"Stored the font {name.Display} ({PrinterMemory.FormatBytes(file.LongLength)}) in LabelScope's printer memory.");
                    break;
                }
                default:
                {
                    // Declared and initialised up front: TryRows only runs when the bytes-per-row value parsed.
                    var rows = 0;
                    string? problem = null, sizeNote = null;
                    if (!ZplArgs.TryLong(a, 4, out var perRow) || !GraphicLimits.TryRows(total, perRow, out rows, out problem, out sizeNote))
                    {
                        Warn(problem is null
                            ? "The bitmap needs the total byte count and the bytes per row; nothing was stored."
                            : $"The bitmap was not stored because {problem}.");
                        return;
                    }
                    if (sizeNote is not null) notes.Add(sizeNote);
                    var image = GraphicData.DecodeBitmap(a[5], (int)perRow, rows, notes);
                    FlushNotes();
                    StoreGraphic(cmd, context, warnings, name, image, label);
                    break;
                }
            }
        }
        catch (GraphicDataException ex)
        {
            FlushNotes();
            Warn($"{ex.Message} Nothing was stored.");
        }
    }

    /// <summary>^IDd:o.x: deletes stored objects; '*' is a wildcard. A printer ignores a name that matches nothing.</summary>
    private static void Delete(ZplCommand cmd, PaintContext context)
    {
        var pattern = ObjectName.Parse(cmd.Args.Split(',')[0], "GRF", 'R');
        var count = context.Memory.Delete(pattern);
        context.MemoryNotes.Add(count == 0
            ? $"^ID {pattern.Display}: nothing in LabelScope's printer memory matched, so nothing was deleted."
            : $"Deleted {count} {(count == 1 ? "object" : "objects")} matching {pattern.Display} from LabelScope's printer memory.");
    }
}
