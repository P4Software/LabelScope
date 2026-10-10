using System.Globalization;

namespace LabelScope.Core.Rendering;

/// <summary>
/// The character sets of ^CI and how far LabelScope supports them. LabelScope reads every job as UTF-8 (Windows-1252
/// when the bytes are not valid UTF-8) before any ZPL is seen, so the text is already characters when ^CI arrives:
/// the Unicode sets (28-30) need nothing more, and the byte-based sets (0-27, 31-36) match the printer exactly as long
/// as the text is plain ASCII. Mapping the upper half of those code pages byte by byte is not done yet.
/// </summary>
internal static class CharacterSets
{
    /// <summary>^CI28: field data is UTF-8.</summary>
    public const int Utf8 = 28;

    /// <summary>Reads the set number from the first ^CI argument; false when it is not a whole number.</summary>
    public static bool TryParse(string[] args, out int set)
    {
        set = 0;
        var text = args.Length > 0 ? args[0].Trim() : "";
        // An empty ^CI keeps the power-up default, set 0.
        return text.Length == 0 || int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out set);
    }

    /// <summary>True for ^CI28-30, whose text LabelScope draws exactly as a printer does (already decoded).</summary>
    public static bool IsUnicode(int set) => set is >= 28 and <= 30;

    /// <summary>
    /// The set's name in the current language for a byte-based set (shown in the "not fully supported" warning), or
    /// null for a number that is no character set (18-23, 25, 32, above 36) or for a Unicode set.
    /// </summary>
    public static string? Describe(int set) => set switch
    {
        >= 0 and <= 12 => Text.Get("CharSet_SingleByte"),
        13 => Text.Get("CharSet_CodePage", 850),
        14 or 15 or 16 or 24 or 26 => Text.Get("CharSet_Asian"),
        17 => Text.Get("CharSet_Ucs2"),
        27 => Text.Get("CharSet_CodePage", 1252),
        31 => Text.Get("CharSet_CodePage", 1250),
        33 => Text.Get("CharSet_CodePage", 1251),
        34 => Text.Get("CharSet_CodePage", 1253),
        35 => Text.Get("CharSet_CodePage", 1254),
        36 => Text.Get("CharSet_CodePage", 1255),
        _ => null,
    };

    /// <summary>True when ^CI is followed by character remapping pairs (any argument after the set that is not empty).</summary>
    public static bool HasRemapping(string[] args)
    {
        for (var i = 1; i < args.Length; i++)
            if (args[i].Trim().Length > 0) return true;
        return false;
    }

    /// <summary>The first ^CI argument as it can be quoted back to the user: trimmed and cut to a readable length.</summary>
    public static string Shown(string[] args)
    {
        var text = args.Length > 0 ? args[0].Trim() : "";
        return text.Length > 10 ? text[..10] + "..." : text;
    }

    /// <summary>True when <paramref name="text"/> has a character outside 7-bit ASCII.</summary>
    public static bool HasNonAscii(string text)
    {
        foreach (var c in text)
            if (c > '\u007F') return true;
        return false;
    }
}
