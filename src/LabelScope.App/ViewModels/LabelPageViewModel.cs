using System.IO;
using System.Windows.Media.Imaging;
using LabelScope.App.Localization;
using LabelScope.Core.Rendering;

namespace LabelScope.App.ViewModels;

/// <summary>
/// One label of a job (one ^XA..^XZ block) as the centre canvas, the Fields tab and the status bar show it.
/// Only the compact PNG is kept; the picture is decoded on demand for the page on screen (see <see cref="CreateImage"/>).
/// </summary>
public sealed class LabelPageViewModel
{
    private IReadOnlyList<FieldRowViewModel>? _fields;

    /// <summary>Creates the page for <paramref name="label"/>, page <paramref name="index"/> (0-based) of the job.</summary>
    /// <param name="label">The rendered label, or the placeholder picture when the job drew none.</param>
    /// <param name="index">0-based position of the label in its job.</param>
    /// <param name="isPlaceholder">True when <paramref name="label"/> is the placeholder picture, not a real label.</param>
    public LabelPageViewModel(RenderedLabel label, int index, bool isPlaceholder)
    {
        Label = label;
        Index = index;
        IsPlaceholder = isPlaceholder;
    }

    /// <summary>The rendered label (PNG, size, fields).</summary>
    public RenderedLabel Label { get; }

    /// <summary>0-based position of the label in its job.</summary>
    public int Index { get; }

    /// <summary>True for the placeholder picture shown when the job drew no label.</summary>
    public bool IsPlaceholder { get; }

    /// <summary>
    /// The rows of the Fields tab, built on first use: a label can hold 5000 fields and most pages are never opened
    /// on the Fields tab.
    /// </summary>
    public IReadOnlyList<FieldRowViewModel> Fields =>
        _fields ??= Label.Fields.Select((f, i) => new FieldRowViewModel(f, i)).ToList();

    /// <summary>Print resolution used to turn dots into inches; 203 when the label does not say (the placeholder).</summary>
    public int DisplayDpi => Label.Dpi > 0 ? Label.Dpi : 203;

    /// <summary>Label width in inches as shown in captions ("4", "2.5"), in the window language's number format.</summary>
    public string WidthInches => Inches(Label.WidthDots);

    /// <summary>Label height in inches as shown in captions.</summary>
    public string HeightInches => Inches(Label.HeightDots);

    /// <summary>Caption above the label: "4 in (812 dots)".</summary>
    public string WidthCaption => UiText.Get("Ui_Caption", WidthInches, Label.WidthDots);

    /// <summary>Caption beside the label, drawn rotated: "6 in (1218 dots)".</summary>
    public string HeightCaption => UiText.Get("Ui_Caption", HeightInches, Label.HeightDots);

    /// <summary>
    /// Hover text of the captions: where each side came from (the ZPL's ^PW / ^LL, or the label loaded in Printer
    /// setup when the ZPL does not say), so a surprising size can be explained.
    /// </summary>
    public string SizeTip => IsPlaceholder
        ? ""
        : UiText.Get("Ui_SizeTip",
            UiText.Get(Label.WidthFromZpl ? "Ui_SizeFromZpl" : "Ui_SizeFromSetup", Label.WidthDots, "^PW"),
            UiText.Get(Label.HeightFromZpl ? "Ui_SizeFromZpl" : "Ui_SizeFromSetup", Label.HeightDots, "^LL"),
            DisplayDpi, Label.Copies);

    private string Inches(int dots) => (dots / (double)DisplayDpi).ToString("0.##", LabelScope.Core.Localization.Text.Culture);

    /// <summary>
    /// Decodes the PNG into a frozen image WPF can show. The caller keeps only the image on screen and lets go of it
    /// when the page changes: a big label can need 160 MB decoded, and the list can hold 100 jobs.
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
