using System.Globalization;
using LabelScope.App.Localization;
using LabelScope.Core.Localization;

namespace LabelScope.App;

/// <summary>A label stock size, given in inches because that is how label rolls are sold.</summary>
/// <param name="WidthIn">Width across the print head, in inches.</param>
/// <param name="HeightIn">Length of one label along the feed direction, in inches.</param>
public sealed record LabelSize(double WidthIn, double HeightIn)
{
    /// <summary>Width in millimetres, the unit stored in settings.json.</summary>
    public double WidthMm => WidthIn * LabelSizes.MmPerInch;

    /// <summary>Height in millimetres, the unit stored in settings.json.</summary>
    public double HeightMm => HeightIn * LabelSizes.MmPerInch;

    /// <summary>The text shown in the Label size list, e.g. "4 × 6 in (101.6 × 152.4 mm)", in the current language.</summary>
    public string Describe() => SetupText.Get("Setup_SizeItem",
        LabelSizes.Format(WidthIn), LabelSizes.Format(HeightIn), LabelSizes.Format(WidthMm), LabelSizes.Format(HeightMm));
}

/// <summary>
/// The label sizes offered in Printer setup, plus the helpers that turn the millimetres stored in settings.json into
/// a choice in the list and back.
/// </summary>
public static class LabelSizes
{
    /// <summary>Millimetres per inch (exact by definition).</summary>
    public const double MmPerInch = 25.4;

    /// <summary>Smallest label side accepted, in millimetres; the same limit settings.json uses.</summary>
    public const double MinMm = 5;

    /// <summary>Largest label side accepted, in millimetres; the same limit settings.json uses.</summary>
    public const double MaxMm = 2000;

    // Two stored sizes count as the same preset when they differ by less than this. The file keeps what was typed,
    // so 101.6 written by hand and 4 × 25.4 computed here may differ in the last binary digit.
    private const double ToleranceMm = 0.05;

    /// <summary>The common thermal label sizes, largest first: 4 × 6 is the shipping label most printers carry.</summary>
    public static IReadOnlyList<LabelSize> Presets { get; } =
    [
        new(4, 6), new(4, 4), new(4, 3), new(4, 2), new(4, 1),
        new(3, 2), new(3, 1), new(2.25, 1.25), new(2, 1),
    ];

    /// <summary>The preset with these millimetre sides, or null when the size is a custom one.</summary>
    public static LabelSize? Find(double widthMm, double heightMm) =>
        Presets.FirstOrDefault(p => Math.Abs(p.WidthMm - widthMm) < ToleranceMm && Math.Abs(p.HeightMm - heightMm) < ToleranceMm);

    /// <summary>True when <paramref name="mm"/> is a usable label side: a real number from 5 to 2000 mm.</summary>
    public static bool IsValidMm(double mm) => double.IsFinite(mm) && mm >= MinMm && mm <= MaxMm;

    /// <summary>
    /// A size for display: at most two decimals, written the way the current language writes numbers
    /// ("101.6" in English, "101,6" in Spanish), and no trailing zeros ("4", not "4.00").
    /// </summary>
    public static string Format(double value) => value.ToString("0.##", Text.Culture);

    /// <summary>
    /// Reads a millimetre value typed by the user. A comma and a point both count as the decimal mark, because a
    /// Spanish user types "101,6" and an English one "101.6"; label sides are never large enough to need a
    /// thousands separator, so neither character can mean that here.
    /// </summary>
    public static bool TryParseMm(string? text, out double mm)
    {
        mm = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var normal = text.Trim().Replace(',', '.');
        return double.TryParse(normal, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite,
            CultureInfo.InvariantCulture, out mm);
    }
}
