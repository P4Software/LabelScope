using System.Globalization;
using LabelScope.Core.Memory;

namespace LabelScope.Core.Rendering;

/// <summary>How one field's data counts from label to label: ^SN or ^SF.</summary>
/// <param name="Line">Line of the ^SN or ^SF, where problems with the counter are reported.</param>
internal abstract record SerialSpec(int Line)
{
    /// <summary>The field data as it prints on the label with value number <paramref name="index"/> (0 = the first).</summary>
    public abstract string Apply(string data, long index);
}

/// <summary>^SNv,n,z: the right-most run of digits counts by <paramref name="Step"/>.</summary>
/// <param name="Step">Added once per value; negative counts down. At most 12 digits, as the guide allows.</param>
/// <param name="Zeros">True for z=Y (leading zeros kept); false for z=N (leading zeros print as spaces).</param>
internal sealed record SnSpec(long Step, bool Zeros, int Line) : SerialSpec(Line)
{
    /// <inheritdoc />
    public override string Apply(string data, long index) => Serialization.ApplySn(data, Step, Zeros, index);
}

/// <summary>^SFa,b: the masked characters at the end of the data count like an odometer.</summary>
/// <param name="Mask">One placeholder per serialized character, aligned with the end of the data.</param>
/// <param name="Increment">What is added per value, aligned with the end of the mask.</param>
internal sealed record SfSpec(string Mask, string Increment, int Line) : SerialSpec(Line)
{
    /// <inheritdoc />
    public override string Apply(string data, long index) => Serialization.ApplySf(data, Mask, Increment, index);
}

/// <summary>
/// What the first image of a label with serial fields hands to <see cref="ZplRenderer"/>, so it can draw the
/// following values from the same block with the same starting state, and work out the last value without drawing it.
/// </summary>
/// <param name="Setup">Printer setup as it was before the first image: every image starts from it, not from what the first image left.</param>
/// <param name="CharacterSet">The ^CI sent before ^XA that the first image used up (null when none).</param>
/// <param name="Quantity">^PQ q: labels printed in all, replicates included.</param>
/// <param name="Replicates">^PQ r: prints of each value (0 counts as 1).</param>
/// <param name="FirstSpec">The counter of the first serial field drawn, named in the "more than 100 values" warning.</param>
/// <param name="FirstData">That field's data before counting (after ^FN, ^FE and ^FH).</param>
internal sealed record SerialRun(PrinterSetup.Values Setup, (int Set, int Line)? CharacterSet, long Quantity, long Replicates,
                                 SerialSpec FirstSpec, string FirstData);

/// <summary>
/// The counting rules of ^SN and ^SF. Pure functions of the data and the value number, so a redraw gives the same
/// pictures and the value of label 99 999 999 is worked out directly, without counting through the ones before it.
/// </summary>
internal static class Serialization
{
    /// <summary>
    /// Most distinct images drawn for one format, the same as the 100 labels a job holds (JobAssembler). Past it the
    /// renderer gives one warning with the total and the last value instead.
    /// </summary>
    public const int MaxImages = 100;

    /// <summary>The guide: "only the 12 right-most integers can be indexed".</summary>
    private const int MaxSnDigits = 12;

    /// <summary>Longest last value quoted in the warning; a 3K ^SF field is cut, as other quoted data is.</summary>
    private const int MaxShownValue = 40;

    /// <summary>
    /// ^SN applied to <paramref name="data"/> for value number <paramref name="index"/>. The start value is the
    /// right-most run of digits in the data itself (its right-most 12 digits), because ^SN's v is the field data and
    /// a recalled ^FN field brings its own. The counter keeps its width: it wraps past 999 and below 000 (as other ZPL viewers
    /// draw it; the guide does not say), and with <paramref name="zeros"/> false the leading zeros print as spaces,
    /// already on the first label. Data without digits prints unchanged.
    /// </summary>
    public static string ApplySn(string data, long step, bool zeros, long index)
    {
        // The right-most run of digits: skip whatever trails it (letters, spaces), then take the digits.
        var end = data.Length;
        while (end > 0 && !char.IsAsciiDigit(data[end - 1])) end--;
        if (end == 0) return data;
        var start = end;
        while (start > 0 && end - start < MaxSnDigits && char.IsAsciiDigit(data[start - 1])) start--;

        var width = end - start;
        // 10^12 times 10^12 still fits Int128, so no step or index (both reduced first) can overflow.
        var modulus = Int128.One;
        for (var i = 0; i < width; i++) modulus *= 10;
        var value = Int128.Parse(data.AsSpan(start, width), NumberStyles.None, CultureInfo.InvariantCulture);
        var offset = (Int128)step % modulus * ((Int128)index % modulus) % modulus;
        value = ((value + offset) % modulus + modulus) % modulus;

        var digits = value.ToString(CultureInfo.InvariantCulture);
        var counter = zeros ? digits.PadLeft(width, '0') : digits.PadLeft(width, ' ');
        return string.Concat(data.AsSpan(0, start), counter, data.AsSpan(end));
    }

    /// <summary>
    /// ^SF applied to <paramref name="data"/> for value number <paramref name="index"/>. The mask lines up with the end
    /// of the data and the increment with the end of the mask. Each masked position is one digit of a mixed-radix number
    /// (10 for D, 16 for H, 8 for O, 26 for A, 36 for N), so adding index x increment once, with the carry walking left
    /// past % positions, gives the same result as counting label by label. A carry out of the left-most position is
    /// dropped. Increment characters must match the mask's case (lower-case "i" against "N" counts as 0, which is the
    /// guide's own example); data characters are read in either case, and a changed character takes the mask's case.
    /// </summary>
    public static string ApplySf(string data, string mask, string increment, long index)
    {
        if (index == 0 || mask.Length == 0) return data;
        var chars = data.ToCharArray();
        var offset = data.Length - mask.Length;       // data position of mask position 0
        var incOffset = mask.Length - increment.Length; // mask position of increment position 0
        long carry = 0;
        for (var m = mask.Length - 1; m >= 0; m--)
        {
            var d = offset + m;
            if (d < 0) break; // the mask is longer than the data: nothing left of here to count
            if (Radix(mask[m]) is not { } radix) continue; // % (or an unknown letter): skipped, the carry passes on
            var i = m - incOffset;
            var step = i >= 0 ? DigitOf(increment[i], mask[m], strictCase: true) : 0;
            // index (at most 99 999 999) x step (at most 35) plus a carry below that: far inside a long.
            var add = index * step + carry;
            if (add == 0) continue; // nothing reaches this position: keep the character exactly as sent
            var sum = DigitOf(chars[d], mask[m], strictCase: false) + add;
            chars[d] = Symbol(sum % radix, mask[m]);
            carry = sum / radix;
        }
        return new string(chars);
    }

    /// <summary>Prints of each value and the number of distinct values for ^PQ q and r (r = 0 prints each value once).</summary>
    public static (long PerValue, long Distinct) Counts(long quantity, long replicates)
    {
        var perValue = Math.Max(1, replicates);
        return (perValue, (quantity + perValue - 1) / perValue);
    }

    /// <summary>How many times value number <paramref name="index"/> prints: the last value takes what is left of q.</summary>
    public static int CopiesOf(long index, long quantity, long replicates)
    {
        var (perValue, distinct) = Counts(quantity, replicates);
        return (int)(index == distinct - 1 ? quantity - (distinct - 1) * perValue : perValue);
    }

    /// <summary>A value as quoted in a warning: at most 40 characters.</summary>
    public static string Shown(string value) => value.Length > MaxShownValue ? value[..MaxShownValue] + "…" : value;

    /// <summary>
    /// Reads ^SNv,n,z. Returns the data the field starts with (v; "1" when empty) and its counter. An increment that is
    /// not a whole number of up to 12 digits counts by 1 instead, with a warning when <paramref name="warnings"/> is given
    /// (the field-number scan reads the same command silently).
    /// </summary>
    public static (string Data, SnSpec Spec) ReadSn(ZplCommand cmd, List<RenderWarning>? warnings)
    {
        var a = cmd.Args.Split(',');
        var data = a[0].Length > 0 ? a[0] : "1";
        long step = 1;
        var text = a.Length > 1 ? a[1].Trim() : "";
        if (text.Length > 0)
        {
            var digits = text.StartsWith('-') || text.StartsWith('+') ? text[1..] : text;
            if (digits.Length is > 0 and <= MaxSnDigits && digits.All(char.IsAsciiDigit))
                step = long.Parse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            else
                warnings?.Add(new(cmd.Line, Text.Get("Painter_SnBadIncrement", Shown(text))));
        }
        var zeros = ZplArgs.Letter(a, 2, 'N') == 'Y';
        return (data, new SnSpec(step, zeros, cmd.Line));
    }

    /// <summary>
    /// Reads ^SFa,b (b defaults to "1"). A ^SF without a mask changes nothing: null, with a warning when
    /// <paramref name="warnings"/> is given.
    /// </summary>
    public static SfSpec? ReadSf(ZplCommand cmd, List<RenderWarning>? warnings)
    {
        var comma = cmd.Args.IndexOf(',');
        var mask = comma >= 0 ? cmd.Args[..comma] : cmd.Args;
        var increment = comma >= 0 ? cmd.Args[(comma + 1)..] : "";
        if (mask.Length == 0)
        {
            warnings?.Add(new(cmd.Line, Text.Get("Painter_SfNoMask")));
            return null;
        }
        return new SfSpec(mask, increment.Length > 0 ? increment : "1", cmd.Line);
    }

    /// <summary>Values one mask position runs through; null for a position that is not counted (% or unknown).</summary>
    private static int? Radix(char mask) => char.ToUpperInvariant(mask) switch
    {
        'D' => 10,
        'H' => 16,
        'O' => 8,
        'A' => 26,
        'N' => 36,
        _ => null,
    };

    /// <summary>
    /// The value of <paramref name="c"/> in the alphabet of mask position <paramref name="mask"/>, or 0 when it is not
    /// in it ("invalid characters count as zero"). Letter positions count A (or a) as 0; N positions run 0-9 then A-Z.
    /// With <paramref name="strictCase"/> a letter must have the mask's case.
    /// </summary>
    private static int DigitOf(char c, char mask, bool strictCase)
    {
        var upperMask = char.IsUpper(mask);
        if (char.IsAsciiLetter(c))
        {
            if (strictCase && char.IsUpper(c) != upperMask) return 0;
            c = char.ToUpperInvariant(c);
        }
        var radix = Radix(mask) ?? 0;
        int value;
        if (char.ToUpperInvariant(mask) == 'A') value = c is >= 'A' and <= 'Z' ? c - 'A' : -1;
        else if (char.IsAsciiDigit(c)) value = c - '0';
        else value = c is >= 'A' and <= 'Z' ? c - 'A' + 10 : -1;
        return value >= 0 && value < radix ? value : 0;
    }

    /// <summary>The character for <paramref name="value"/> in a mask position, in the case of the mask letter.</summary>
    private static char Symbol(long value, char mask)
    {
        var v = (int)value;
        char c = char.ToUpperInvariant(mask) == 'A' ? (char)('A' + v) : v < 10 ? (char)('0' + v) : (char)('A' + v - 10);
        return char.IsLower(mask) ? char.ToLowerInvariant(c) : c;
    }
}
