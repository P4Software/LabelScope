using SkiaSharp;

namespace LabelScope.Core.Fonts;

/// <summary>
/// The two open-licence fonts LabelScope ships (SIL OFL 1.1, THIRD-PARTY-NOTICES.txt section G). Loaded once per
/// process and kept for its lifetime, so painters share them and must never dispose them.
/// </summary>
internal static class BundledFonts
{
    private static readonly Lazy<SKTypeface> ScalableFace = new(() => Load("IBMPlexSansCondensed-Bold.ttf"));
    private static readonly Lazy<SKTypeface> MonoFace = new(() => Load("IBMPlexMono-Regular.ttf"));

    // The font bytes stay referenced for the life of the process: a typeface reads its glyphs from them lazily.
    private static readonly List<SKData> KeepAlive = [];

    /// <summary>IBM Plex Sans Condensed Bold: stands in for Zebra's scalable font 0 (CG Triumvirate Bold Condensed) and fonts P to V.</summary>
    public static SKTypeface Scalable => ScalableFace.Value;

    /// <summary>IBM Plex Mono Regular: stretched into the cells of Zebra's bitmap fonts A to H, and used for barcode text lines.</summary>
    public static SKTypeface Mono => MonoFace.Value;

    private static SKTypeface Load(string file)
    {
        using var stream = typeof(BundledFonts).Assembly.GetManifestResourceStream("LabelScope.Fonts." + file)
            ?? throw new InvalidOperationException($"The built-in font {file} is missing from LabelScope.Core.dll. Reinstall LabelScope.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var data = SKData.CreateCopy(buffer.ToArray());
        lock (KeepAlive) KeepAlive.Add(data);
        return SKTypeface.FromData(data)
            ?? throw new InvalidOperationException($"The built-in font {file} could not be read. Reinstall LabelScope.");
    }
}
