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
    public LabelEntry(DateTimeOffset at, string source, string zpl, string originalZpl, bool complete, RenderedLabel label,
                      IReadOnlyList<RenderWarning> warnings, string? placeholderTitle = null)
    {
        PlaceholderTitle = placeholderTitle;
        At = at;
        Source = source;
        Zpl = zpl;
        OriginalZpl = originalZpl;
        Complete = complete;
        Label = label;
        Warnings = warnings;
    }

    /// <summary>When the label arrived.</summary>
    public DateTimeOffset At { get; }
    /// <summary>Sender address.</summary>
    public string Source { get; }
    /// <summary>
    /// The ZPL as shown in the window: laid out one command group per line. The picture was drawn from this very
    /// text, so the line numbers of the warnings match what is displayed.
    /// </summary>
    public string Zpl { get; }
    /// <summary>The ZPL exactly as it was received, for the "Copy original" button.</summary>
    public string OriginalZpl { get; }
    /// <summary>False when the sender disconnected before ^XZ.</summary>
    public bool Complete { get; }
    /// <summary>The rendered label (PNG bytes and size).</summary>
    public RenderedLabel Label { get; }
    /// <summary>Warnings produced while rendering.</summary>
    public IReadOnlyList<RenderWarning> Warnings { get; }

    /// <summary>
    /// Approximate memory this entry keeps (PNG bytes plus both ZPL texts); used to bound the whole history.
    /// Both texts are counted because each one is a separate string in memory.
    /// </summary>
    public long ApproximateBytes => HistoryBudget.SizeOf(Label.PngBytes.Length, Zpl) + HistoryBudget.SizeOf(0, OriginalZpl);

    /// <summary>
    /// Set when the data produced no label picture: the entry then shows a placeholder picture, the raw text
    /// and the warnings, and this text is its title. Null for a normal label.
    /// </summary>
    public string? PlaceholderTitle { get; }

    /// <summary>First line in the history list.</summary>
    public string Title => PlaceholderTitle ?? At.ToString("HH:mm:ss") + (Complete ? "" : "  (incomplete)");
    /// <summary>Second line in the history list.</summary>
    public string Subtitle => PlaceholderTitle is null ? $"{Label.WidthDots} x {Label.HeightDots} dots · {Source}" : $"{At:HH:mm:ss} · {Source}";
    /// <summary>One line for the info bar above the preview.</summary>
    public string Info => PlaceholderTitle is null
        ? $"{Label.WidthDots} x {Label.HeightDots} dots · {Label.Copies} copy/copies requested · {Warnings.Count} warning(s)"
        : $"{(PlaceholderTitle == PlaceholderLabel.NotDrawnTitle ? PlaceholderLabel.NotDrawnText : PlaceholderLabel.Text)} · {Warnings.Count} warning(s)";

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
