using System.IO;
using System.Windows.Media.Imaging;
using LabelScope.App.Localization;
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

    /// <summary>Source value used for a job opened from a file (shown as "Opened from file" in the window language).</summary>
    public const string FileSource = "<file>";
    /// <summary>Source value used for a job pasted from the clipboard (shown as "Pasted" in the window language).</summary>
    public const string PastedSource = "<pasted>";

    // Computed once: the ZPL never changes, and the job list asks for the name on every redraw.
    private string? _jobName;

    /// <summary>
    /// Job name for the card and the window title: the text of a ^FX comment before the first field, else the first
    /// ^FD text (trimmed, at most 40 characters), else "Label". A placeholder entry uses its placeholder title.
    /// </summary>
    public string JobName
    {
        get
        {
            if (PlaceholderTitle is not null) return PlaceholderTitle;
            _jobName ??= JobNaming.NameFrom(Zpl);
            // The fallback is looked up on each call so it follows a language switch.
            return _jobName.Length > 0 ? _jobName : UiText.Get("Ui_DefaultJobName");
        }
    }

    /// <summary>Arrival time for the card and the status bar.</summary>
    public string TimeText => At.ToString("HH:mm:ss");

    /// <summary>
    /// Where the job came from, in the window language: "This computer" for the local machine, "Opened from file",
    /// "Pasted", or the sender's address. Resolved on each call so a language switch shows at once.
    /// </summary>
    public string SourceText => Source switch
    {
        "127.0.0.1" or "::1" or "::ffff:127.0.0.1" => UiText.Get("Ui_SourceThisComputer"),
        FileSource => UiText.Get("Ui_SourceFile"),
        PastedSource => UiText.Get("Ui_SourcePasted"),
        _ => Source,
    };

    /// <summary>True for a job opened from a file or pasted, rather than received over the network.</summary>
    public bool IsLocal => Source is FileSource or PastedSource;

    /// <summary>Second line of the job card: source and size in dots, plus a note when the job arrived incomplete.</summary>
    public string CardDetail => UiText.Get("Ui_CardDots", SourceText, Label.WidthDots, Label.HeightDots)
                                + (Complete ? "" : " · " + UiText.Get("Ui_Incomplete"));

    /// <summary>True when rendering reported warnings; the card then shows its status in red.</summary>
    public bool IsError => Warnings.Count > 0;

    /// <summary>Third line of the job card: "Rendered OK", or the warning count with the first warning, shortened.</summary>
    public string CardStatus
    {
        get
        {
            if (Warnings.Count == 0) return UiText.Get("Ui_RenderedOk");
            var first = Shorten(Warnings[0].Message, 40);
            return Warnings.Count == 1 ? UiText.Get("Ui_WarningOne", first) : UiText.Get("Ui_WarningMany", Warnings.Count, first);
        }
    }

    /// <summary>Print resolution used to turn dots into inches; the default printer resolution when the label has none.</summary>
    private int DpiOr203 => Label.Dpi > 0 ? Label.Dpi : 203;

    /// <summary>Label width in inches, as shown in captions ("4", "2.5").</summary>
    public string WidthInches => (Label.WidthDots / (double)DpiOr203).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
    /// <summary>Label height in inches, as shown in captions.</summary>
    public string HeightInches => (Label.HeightDots / (double)DpiOr203).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
    /// <summary>Caption above the label: "4 in (812 dots)".</summary>
    public string WidthCaption => UiText.Get("Ui_Caption", WidthInches, Label.WidthDots);
    /// <summary>Caption beside the label, drawn rotated: "6 in (1218 dots)".</summary>
    public string HeightCaption => UiText.Get("Ui_Caption", HeightInches, Label.HeightDots);
    /// <summary>The resolution shown in the status bar.</summary>
    public int DisplayDpi => DpiOr203;
    /// <summary>Number of lines of the ZPL as shown in the ZPL tab.</summary>
    public int LineCount => Zpl.Length == 0 ? 0 : Zpl.Count(c => c == '\n') + 1;

    /// <summary>Cuts <paramref name="text"/> to <paramref name="max"/> characters with an ellipsis, on one line.</summary>
    private static string Shorten(string text, int max)
    {
        var line = text.ReplaceLineEndings(" ");
        return line.Length <= max ? line : line[..(max - 1)].TrimEnd() + "…";
    }

    /// <summary>
    /// Says where the label size came from: the sender's ^PW / ^LL, or the default size in settings.json
    /// (used when the ZPL does not state it, as a real printer would use the size stored in the printer).
    /// </summary>
    public string SizeText
    {
        get
        {
            string Part(string name, int dots, bool fromZpl, string command)
            {
                var mm = Label.Dpi > 0 ? $" = {dots * 25.4 / Label.Dpi:0.#} mm" : "";
                return $"{name} {dots} dots{mm} ({(fromZpl ? "sent by the program in " + command : "not in the ZPL, so the picture ends at the last thing drawn")})";
            }
            return Part("Width", Label.WidthDots, Label.WidthFromZpl, "^PW") + " · " +
                   Part("Height", Label.HeightDots, Label.HeightFromZpl, "^LL") +
                   (Label.Dpi > 0 ? $" · at {Label.Dpi} dpi" : "");
        }
    }

    /// <summary>One line for the info bar above the preview.</summary>
    public string Info => PlaceholderTitle is null
        ? $"{SizeText} · {Label.Copies} copy/copies requested · {Warnings.Count} warning(s)"
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
