using System.Globalization;

namespace LabelScope.Core.Rendering;

/// <summary>
/// Printer-setup commands that cannot change the picture. A warning about them is noise: ZebraDesigner and similar
/// programs start every job with a dozen of them, and a warning list full of "not supported" hides the real problems.
/// </summary>
internal static class SilentCommands
{
    // Commands whose every value is about the printer mechanics, memory or comments, never the drawn label.
    // Deliberately NOT here (each can change the picture or how the rest of the job is read, see IsSilent):
    // ^PM (mirror), ^JM (half density doubles the format).
    // ^CC / ^CD / ^CT and their ~ forms are not here: the parser applies them and the renderer warns about a refused one.
    // ^CI and ^MC are not here either: the painter and the renderer handle them themselves (CharacterSets, and the ^MCN
    // background in ZplRenderer.PaintSafely).
    private static readonly HashSet<string> Always = new()
    {
        "^MN", "^MM", "^MD", "^MT", "^PR", "^JU", "^JZ", "^XB", "^FX",
        "~TA", "~JS", "~SD",
        // ^KD only chooses how the clock is shown on the printer's display and configuration label, never on a label.
        "^KD",
    };

    /// <summary>True when <paramref name="name"/> with <paramref name="args"/> cannot affect the drawn label.</summary>
    public static bool IsSilent(string name, string args)
    {
        if (Always.Contains(name)) return true;

        var a = args.Trim();
        return name switch
        {
            // Label top / label shift: harmless only when they shift by nothing.
            "^LT" or "^LS" => a.Length == 0 || a.Split(',').All(IsZero),
            // Print mirror: only the default (N) leaves the picture alone.
            "^PM" => a.Length == 0 || char.ToUpperInvariant(a[0]) == 'N',
            // Half density (B) doubles the size of the whole format; A is the native density.
            "^JM" => a.Length == 0 || char.ToUpperInvariant(a[0]) == 'A',
            // Orientation and reverse print are handled inside a label; outside one the "off" value is a no-op,
            // while ^POI / ^LRY would be lost, so those keep their warning.
            "^PO" or "^LR" => a.Length == 0 || char.ToUpperInvariant(a[0]) == 'N',
            _ => false,
        };
    }

    private static bool IsZero(string text) =>
        int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v == 0;
}
