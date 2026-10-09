namespace LabelScope.Core.Barcodes;

/// <summary>A barcode ready to be drawn; the painter picks the drawing routine from the concrete type.</summary>
/// <param name="Orientation">N, R, I or B.</param>
internal abstract record BarcodeField(char Orientation);

/// <summary>A linear (bar/space) barcode.</summary>
/// <param name="Symbol">The widths and interpretation text.</param>
/// <param name="Look">Bar height and interpretation-line placement.</param>
/// <param name="Narrow">Module width in dots (from <c>^BY</c>); sizes the text and guard bars.</param>
/// <param name="Orientation">N, R, I or B.</param>
internal sealed record LinearField(LinearSymbol Symbol, BarcodeLook Look, int Narrow, char Orientation) : BarcodeField(Orientation);

/// <summary>Maps <c>^B?</c> commands to encoders and names the ones that are planned but not built yet.</summary>
internal static class BarcodeFactory
{
    /// <summary>
    /// One entry per supported command; each symbology task adds its line here. The function receives the
    /// parameters and the decoded field data and throws <see cref="BarcodeDataException"/> for data it cannot encode.
    /// </summary>
    private static readonly Dictionary<string, Func<BarcodeArgs, string, BarcodeField>> Encoders = new()
    {
        ["^BC"] = Code128Encoder.Build,
        ["^B3"] = Code39Family.BuildCode39,
        ["^BL"] = Code39Family.BuildLogmars,
        ["^BA"] = Code39Family.BuildCode93,
        ["^BE"] = EanUpcEncoder.BuildEan13,
        ["^BU"] = EanUpcEncoder.BuildUpcA,
        ["^B8"] = EanUpcEncoder.BuildEan8,
        ["^B9"] = EanUpcEncoder.BuildUpcE,
        ["^B2"] = OtherLinearEncoders.BuildInterleaved2of5,
        ["^BK"] = OtherLinearEncoders.BuildCodabar,
        ["^B1"] = OtherLinearEncoders.BuildCode11,
        ["^BM"] = OtherLinearEncoders.BuildMsi,
        // (further symbologies are added by later tasks)
    };

    /// <summary>Symbologies that exist in ZPL but are planned for plan 4; see the deferral table of plan 2.</summary>
    private static readonly Dictionary<string, string> Deferred = new()
    {
        ["^B0"] = "Aztec", ["^BO"] = "Aztec", ["^B4"] = "Code 49", ["^B5"] = "PLANET Code",
        ["^BB"] = "CODABLOCK", ["^BD"] = "MaxiCode", ["^BF"] = "MicroPDF417",
        ["^BI"] = "Industrial 2 of 5", ["^BJ"] = "Standard 2 of 5", ["^BP"] = "Plessey",
        ["^BR"] = "GS1 DataBar", ["^BS"] = "UPC/EAN extension", ["^BT"] = "TLC39", ["^BZ"] = "postal code",
    };

    /// <summary>True when <paramref name="command"/> (for example "^BC") can be drawn.</summary>
    public static bool IsSupported(string command) => Encoders.ContainsKey(command);

    /// <summary>The symbology name when the command is planned for a later release; otherwise null.</summary>
    public static string? DeferredName(string command) => Deferred.GetValueOrDefault(command);

    /// <summary>Encodes <paramref name="data"/> for <paramref name="command"/>.</summary>
    /// <exception cref="BarcodeDataException">The data cannot be written in this symbology.</exception>
    /// <exception cref="KeyNotFoundException">The command is not supported; call <see cref="IsSupported"/> first.</exception>
    public static BarcodeField Build(string command, string[] args, string data, BarDefaults by, char fieldOrientation) =>
        Encoders[command](new BarcodeArgs(args, by, fieldOrientation), data);
}
