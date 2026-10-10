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
                warnings.Add(new(cmd.Line, Text.Get("Storage_EgIgnored")));
                return true;
            case "^CW":
                AssignFontLetter(cmd, context, warnings);
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

        if (!ZplArgs.TryLong(a, 1, out var total) || !ZplArgs.TryLong(a, 2, out var perRow))
        {
            warnings.Add(new(cmd.Line, Text.Get("Storage_DgNeedsSize", label)));
            return;
        }
        if (!GraphicLimits.TryRows(total, perRow, out var rows, out var problem, out var sizeNote))
        {
            warnings.Add(new(cmd.Line, Text.Get("Storage_GraphicSizeProblem", label, problem)));
            return;
        }
        // Without data a printer stores nothing useful; storing a blank graphic would hide the mistake until a label
        // recalled it and printed nothing.
        if (a.Length < 4 || a[3].Trim().Length == 0)
        {
            warnings.Add(new(cmd.Line, Text.Get("Storage_DgNoData", label)));
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
            warnings.Add(new(cmd.Line, Text.Get("Storage_NothingStored", label, ex.Message)));
            return;
        }
        foreach (var note in notes) warnings.Add(new(cmd.Line, Text.Get("Common_Prefixed", label, note)));
        StoreGraphic(cmd, context, warnings, name, image, label);
    }

    /// <summary>Stores a decoded graphic and records a note, or a warning when memory refuses it.</summary>
    internal static void StoreGraphic(ZplCommand cmd, PaintContext context, List<RenderWarning> warnings, ObjectName name, MonoImage image, string label)
    {
        var refusal = context.Memory.Store(name, new StoredGraphic(image));
        if (refusal is not null)
        {
            warnings.Add(new(cmd.Line, Text.Get("Common_Prefixed", label, refusal)));
            return;
        }
        // The guide allows 1-8 characters and does not say whether a printer refuses or shortens a longer name, so the
        // warning says "may". LabelScope keeps the full name so the preview still works.
        if (name.IsLongerThanZebraAllows)
            warnings.Add(new(cmd.Line, Text.Get("Storage_NameTooLong", label)));
        context.MemoryNotes.Add(Text.Get("Storage_StoredGraphic", (name.Drive is null ? name.OnDrive('R') : name).Display, image.Width, image.Height));
    }

    // ~DY types that never change a picture (certificates, WML menus, web pages, feedback files): accepted quietly,
    // because they never change a label, and a warning about them would only hide real problems.
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
        void Warn(string message) => warnings.Add(new(cmd.Line, Text.Get("Common_Prefixed", label, message)));

        switch (ZplArgs.Letter(a, 1, ' '))
        {
            case 'A' or 'P':
                break;
            case 'B':
                Warn(Text.Get("Storage_DyBinary"));
                return;
            case 'C':
                Warn(Text.Get("Storage_DyCompressed"));
                return;
            default:
                Warn(Text.Get("Storage_DyNeedsFormat"));
                return;
        }
        if (type == "E") { Warn(Text.Get("Storage_DyTte")); return; }
        if (type == "X") { Warn(Text.Get("Storage_DyPcx")); return; }
        if (a.Length < 6) { Warn(Text.Get("Storage_DyNoData")); return; }

        ZplArgs.TryLong(a, 3, out var total);
        // The declared size is checked before any data is read, so a hostile "t" cannot make a big buffer.
        var cap = extension == "PNG" ? PngImage.MaxFileBytes : extension == "TTF" ? FontFile.MaxFileBytes : 0;
        if (cap > 0 && total > cap)
        {
            var tooLarge = extension == "PNG" ? "Storage_DyPngTooLarge" : "Storage_DyFontTooLarge";
            Warn(Text.Get(tooLarge, total, cap, cap / (1024 * 1024)));
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
                        Warn(Text.Get("Storage_DyPngTooLarge", file.LongLength, PngImage.MaxFileBytes, 5));
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
                        Warn(Text.Get("Storage_DyFontTooLarge", file.LongLength, FontFile.MaxFileBytes, 16));
                        return;
                    }
                    // Only the header is checked here: the bytes are kept for later use and never parsed by a library now.
                    if (FontFile.Check(file) is { } notFont) { Warn(notFont); return; }
                    var refusal = context.Memory.Store(name, new StoredFont(file));
                    if (refusal is not null) { Warn(refusal); return; }
                    context.MemoryNotes.Add(Text.Get("Storage_StoredFont", name.Display, PrinterMemory.FormatBytes(file.LongLength)));
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
                            ? Text.Get("Storage_BitmapNeedsSize")
                            : Text.Get("Storage_BitmapSizeProblem", problem));
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
            Warn(Text.Get("Storage_MessageNothingStored", ex.Message));
        }
    }

    /// <summary>
    /// ^CWa,d:o.x: gives a downloaded or folder font a one-character name, kept for the session in printer memory as
    /// a printer keeps it until it is switched off. The file is not looked up here: the name is accepted now, and the font is found, or reported
    /// missing, when a label uses the letter.
    /// </summary>
    private static void AssignFontLetter(ZplCommand cmd, PaintContext context, List<RenderWarning> warnings)
    {
        var a = cmd.Args.Split(',', 2);
        var id = a[0].Trim();
        if (id.Length != 1 || !char.IsAsciiLetterOrDigit(id[0]) || a.Length < 2 || a[1].Trim().Length == 0)
        {
            warnings.Add(new(cmd.Line, Text.Get("Storage_CwNeedsArgs")));
            return;
        }
        // Parsed like every stored object's name: upper-cased, cut to a safe length, and only ever a dictionary key.
        var file = ObjectName.Parse(a[1], "TTF", 'R');
        context.Memory.AssignFont(id[0], file);
        context.MemoryNotes.Add(Text.Get("Storage_CwAssigned", char.ToUpperInvariant(id[0]), file.Display));
    }

    /// <summary>^IDd:o.x: deletes stored objects; '*' is a wildcard. A printer ignores a name that matches nothing.</summary>
    private static void Delete(ZplCommand cmd, PaintContext context)
    {
        var pattern = ObjectName.Parse(cmd.Args.Split(',')[0], "GRF", 'R');
        var count = context.Memory.Delete(pattern);
        context.MemoryNotes.Add(count == 0
            ? Text.Get("Storage_IdNoMatch", pattern.Display)
            : Text.Get(count == 1 ? "Storage_IdDeleted_One" : "Storage_IdDeleted_Many", count, pattern.Display));
    }
}
