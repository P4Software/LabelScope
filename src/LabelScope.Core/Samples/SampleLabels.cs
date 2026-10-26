using System.Diagnostics.CodeAnalysis;
using System.Text;
using LabelScope.Core.Fonts;
using LabelScope.Core.Memory;
using LabelScope.Core.Rendering;

namespace LabelScope.Core.Samples;

/// <summary>
/// The built-in sample labels: the catalog, the loader for the embedded ZPL files and the one way to draw a sample.
/// </summary>
public static class SampleLabels
{
    /// <summary>
    /// The density every sample is written for and drawn at, whatever the density chosen in Printer setup: the samples
    /// are laid out in dots, so at another density a 4 x 6 inch sample would no longer be 4 x 6 inches.
    /// </summary>
    public const int Dpi = 203;

    // The files are embedded under this prefix (see LabelScope.Core.csproj), the same way the bundled fonts are, so the
    // app, the tests and the public samples folder all use the same bytes.
    private const string ResourcePrefix = "LabelScope.Samples.";

    // The loaded-label size (4 x 6 inches in millimetres) is only a fallback for a side the ZPL does not set; every
    // sample sets its own ^PW and ^LL (the catalog tests check it), so it should never be used.
    private const double DefaultWidthMm = 101.6;
    private const double DefaultHeightMm = 152.4;

    // UTF-8 without a byte-order mark; the sample files are written that way and a stray mark would show up as a
    // character before the first command.
    private static readonly UTF8Encoding Utf8 = new(false);

    /// <summary>Every sample, in catalog order. New samples are added at the end so numbers and files never move.</summary>
    public static IReadOnlyList<SampleLabel> All { get; } =
    [
        new(1, "parcel-shipping-label", SampleGroup.Shipping),
        new(2, "gs1-carton-label", SampleGroup.Shipping),
        new(3, "pallet-label", SampleGroup.Shipping),
        new(4, "freight-ltl-label", SampleGroup.Shipping),
        new(5, "return-label", SampleGroup.Shipping),
        new(6, "packing-slip", SampleGroup.Shipping),
        new(7, "itf14-case-label", SampleGroup.Shipping),
        new(8, "pick-ticket-saved-template", SampleGroup.Warehouse),
        new(9, "bin-location-label", SampleGroup.Warehouse),
        new(10, "license-plate-lpn", SampleGroup.Warehouse),
        new(11, "receiving-label", SampleGroup.Warehouse),
        new(12, "rack-label", SampleGroup.Warehouse),
        new(13, "inventory-count-tag", SampleGroup.Warehouse),
    ];

    /// <summary>Finds a sample by its id (the slug). Returns false for an unknown or null id, for example one kept by a later version.</summary>
    public static bool TryGet(string? id, [NotNullWhen(true)] out SampleLabel? sample)
    {
        sample = All.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal));
        return sample is not null;
    }

    /// <summary>Reads the ZPL of <paramref name="sample"/> from the file embedded in this assembly.</summary>
    public static string Load(SampleLabel sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        using var stream = typeof(SampleLabels).Assembly.GetManifestResourceStream(ResourcePrefix + sample.FileName)
            ?? throw new InvalidOperationException($"The sample file {sample.FileName} is missing from this copy of LabelScope.");
        using var reader = new StreamReader(stream, Utf8, detectEncodingFromByteOrderMarks: false);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Draws sample ZPL the one way samples are drawn: in a new printer memory of its own, at <see cref="Dpi"/>, with the
    /// app's fonts. It never takes a memory from the caller, so a sample can neither change what a real job sees (the
    /// size, home position, code page and stored objects a printer keeps between jobs) nor be changed by it.
    /// </summary>
    /// <param name="zpl">The sample's ZPL, as <see cref="Load"/> returns it.</param>
    /// <param name="fonts">The fonts the app uses for real labels.</param>
    /// <param name="clock">"Now" for the clock fields; pass the same value to get the same picture again.</param>
    public static RenderResult Render(string zpl, FontLibrary fonts, DateTimeOffset clock)
    {
        // Formatted first, as the window does for a received label, so a warning's line number matches the line the
        // ZPL panel shows. Formatting does not change the picture.
        var formatted = ZplFormatter.Format(zpl);
        var renderer = new ZplRenderer(new PrinterMemory(), fonts);
        return renderer.Render(formatted, new RenderOptions(Dpi, DefaultWidthMm, DefaultHeightMm) with { Clock = clock });
    }
}
