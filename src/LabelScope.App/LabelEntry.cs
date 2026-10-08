using System.IO;
using System.Windows.Media.Imaging;
using LabelScope.Core;
using LabelScope.Core.Rendering;

namespace LabelScope.App;

/// <summary>
/// One row of the history list: a received label with its PNG bytes and warnings.
/// Only the compact PNG is kept per entry. A decoded bitmap can be far larger (a big label may need
/// 160 MB), and the history can hold 100 entries, so the picture is decoded on demand for the
/// selected entry only (see <see cref="CreateImage"/>).
/// </summary>
public sealed class LabelEntry
{
    /// <summary>Creates the entry; nothing is decoded here.</summary>
    public LabelEntry(DateTimeOffset at, string source, string zpl, bool complete, RenderedLabel label,
                      IReadOnlyList<RenderWarning> warnings)
    {
        At = at;
        Source = source;
        Zpl = zpl;
        Complete = complete;
        Label = label;
        Warnings = warnings;
    }

    /// <summary>When the label arrived.</summary>
    public DateTimeOffset At { get; }
    /// <summary>Sender address.</summary>
    public string Source { get; }
    /// <summary>Raw ZPL text exactly as received.</summary>
    public string Zpl { get; }
    /// <summary>False when the sender disconnected before ^XZ.</summary>
    public bool Complete { get; }
    /// <summary>The rendered label (PNG bytes and size).</summary>
    public RenderedLabel Label { get; }
    /// <summary>Warnings produced while rendering.</summary>
    public IReadOnlyList<RenderWarning> Warnings { get; }

    /// <summary>Approximate memory this entry keeps (PNG bytes plus ZPL text); used to bound the whole history.</summary>
    public long ApproximateBytes => HistoryBudget.SizeOf(Label.PngBytes.Length, Zpl);

    /// <summary>First line in the history list.</summary>
    public string Title => At.ToString("HH:mm:ss") + (Complete ? "" : "  (incomplete)");
    /// <summary>Second line in the history list.</summary>
    public string Subtitle => $"{Label.WidthDots} x {Label.HeightDots} dots · {Source}";
    /// <summary>One line for the info bar above the preview.</summary>
    public string Info => $"{Label.WidthDots} x {Label.HeightDots} dots · {Label.Copies} copy/copies requested · {Warnings.Count} warning(s)";

    /// <summary>
    /// Decodes the PNG into a frozen image that WPF can show. The caller should keep only the image of
    /// the selected entry and let go of it when the selection changes, so memory stays small.
    /// </summary>
    public BitmapImage CreateImage()
    {
        using var ms = new MemoryStream(Label.PngBytes);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad; // read everything now so the stream can be disposed
        image.StreamSource = ms;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
