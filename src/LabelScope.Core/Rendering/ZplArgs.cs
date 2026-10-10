using System.Globalization;

namespace LabelScope.Core.Rendering;

/// <summary>Argument helpers for commands whose last parameter is free data that may itself contain commas.</summary>
internal static class ZplArgs
{
    /// <summary>
    /// Splits on the first <paramref name="count"/> commas only; the last element keeps the rest unchanged. Graphic
    /// data needs this: in Zebra's compressed hex a ',' means "fill the rest of the row with white".
    /// </summary>
    public static string[] SplitFirst(string args, int count) => args.Split(',', count + 1);

    /// <summary>First letter of parameter <paramref name="index"/> in upper case, or <paramref name="fallback"/> when it is empty or missing.</summary>
    public static char Letter(string[] a, int index, char fallback)
    {
        var t = index < a.Length ? a[index].Trim() : "";
        return t.Length > 0 ? char.ToUpperInvariant(t[0]) : fallback;
    }

    /// <summary>
    /// Parameter <paramref name="index"/> as a <see cref="long"/>. Sizes are read as long so a hostile value near
    /// int.MaxValue is still compared, not wrapped; a value too large even for long counts as missing.
    /// </summary>
    public static bool TryLong(string[] a, int index, out long value)
    {
        value = 0;
        return index < a.Length && long.TryParse(a[index].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }
}
