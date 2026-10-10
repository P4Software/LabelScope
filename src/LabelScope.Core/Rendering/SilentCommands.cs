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
    // ^PM (mirror), ^JM (half density doubles the format), ^MC (^MCN keeps the previous label as background),
    // ~CC / ~CD / ~CT (they change the command prefix and delimiter characters).
    private static readonly HashSet<string> Always = new()
    {
        "^MN", "^MM", "^MD", "^MT", "^PR", "^JU", "^JZ", "^XB", "^FX",
        "~TA", "~JS", "~SD",
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
            // Code page 0 is the default; others change how text is read, which LabelScope does not support yet, so they warn.
            "^CI" => a.Length == 0 || IsZero(a),
            // Half density (B) doubles the size of the whole format; A is the native density.
            "^JM" => a.Length == 0 || char.ToUpperInvariant(a[0]) == 'A',
            // Map clear: N keeps the finished label as the background of the next one. Y is the default.
            "^MC" => a.Length == 0 || char.ToUpperInvariant(a[0]) == 'Y',
            // Orientation and reverse print are handled inside a label; outside one the "off" value is a no-op,
            // while ^POI / ^LRY would be lost, so those keep their warning.
            "^PO" or "^LR" => a.Length == 0 || char.ToUpperInvariant(a[0]) == 'N',
            // Control, delimiter and format prefix characters: only the defaults keep the rest of the job readable.
            "~CC" => a.Length == 0 || a == "^",
            "~CD" => a.Length == 0 || a == ",",
            "~CT" => a.Length == 0 || a == "~",
            _ => false,
        };
    }

    private static bool IsZero(string text) =>
        int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v == 0;
}
