namespace LabelScope.Core.Memory;

/// <summary>
/// The name of an object in printer memory as ZPL writes it: <c>d:o.x</c>, for example <c>R:LOGO.GRF</c>.
/// Names are upper-cased (a printer treats them without regard to case) and used only as dictionary keys,
/// never as file paths.
/// </summary>
internal readonly record struct ObjectName(char? Drive, string Name, string Extension)
{
    /// <summary>Drives searched when a command names none: Zebra's order for ^XG and ^IM ("R:, E:, B:, A:").</summary>
    public static readonly char[] SearchOrder = ['R', 'E', 'B', 'A'];

    /// <summary>
    /// Longest name kept. Printers allow 8 characters (16 for formats); a hostile megabyte-long name is cut so a key
    /// cannot grow without bound. Two names that differ only after 64 characters are treated as one.
    /// </summary>
    public const int MaxNameLength = 64;

    /// <summary>
    /// Reads <c>d:o.x</c>. A missing drive becomes <paramref name="defaultDrive"/> (null = "search"), a missing extension
    /// becomes <paramref name="defaultExtension"/>, and an empty name becomes UNKNOWN, the default the ZPL guide gives.
    /// </summary>
    public static ObjectName Parse(string? text, string defaultExtension, char? defaultDrive)
    {
        var t = (text ?? "").Trim();
        var drive = defaultDrive;
        if (t.Length >= 2 && t[1] == ':' && char.IsAsciiLetter(t[0]))
        {
            drive = char.ToUpperInvariant(t[0]);
            t = t[2..].Trim();
        }
        var dot = t.LastIndexOf('.');
        var name = (dot >= 0 ? t[..dot] : t).Trim().ToUpperInvariant();
        var ext = (dot >= 0 ? t[(dot + 1)..] : "").Trim().ToUpperInvariant();
        if (name.Length == 0) name = "UNKNOWN";
        if (ext.Length == 0) ext = defaultExtension.ToUpperInvariant();
        if (name.Length > MaxNameLength) name = name[..MaxNameLength];
        if (ext.Length > 8) ext = ext[..8];
        return new ObjectName(drive, name, ext);
    }

    /// <summary>The name as messages show it, with its drive when it has one: R:LOGO.GRF.</summary>
    public string Display => Drive is { } d ? $"{d}:{Name}.{Extension}" : $"{Name}.{Extension}";

    /// <summary>Dictionary key; an object without a drive is kept on R:.</summary>
    internal string Key => $"{Drive ?? 'R'}:{Name}.{Extension}";

    /// <summary>The same name on drive <paramref name="drive"/>.</summary>
    public ObjectName OnDrive(char drive) => this with { Drive = drive };

    /// <summary>
    /// True when this name matches <paramref name="pattern"/> as ^ID reads it: same drive (R: when omitted) and
    /// '*' standing for any run of characters in the name and in the extension.
    /// </summary>
    public bool Matches(ObjectName pattern) =>
        (pattern.Drive ?? 'R') == (Drive ?? 'R') && Glob(pattern.Name, Name) && Glob(pattern.Extension, Extension);

    /// <summary>Iterative '*' matcher: linear in practice and immune to the exponential blow-up of naive recursion.</summary>
    private static bool Glob(string pattern, string text)
    {
        int p = 0, t = 0, star = -1, mark = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && pattern[p] != '*' && pattern[p] == text[t]) { p++; t++; }
            else if (p < pattern.Length && pattern[p] == '*') { star = p++; mark = t; }
            else if (star >= 0) { p = star + 1; t = ++mark; }
            else return false;
        }
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }
}
