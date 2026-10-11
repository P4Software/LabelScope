using System.Globalization;
using LabelScope.Core.Fonts;
using LabelScope.Core.Memory;

namespace LabelScope.Core.Rendering;

/// <summary>Converts ZPL text into label images without any online service.</summary>
public sealed class ZplRenderer
{
    /// <summary>Creates a renderer with its own, empty printer memory and only the built-in fonts.</summary>
    public ZplRenderer() : this(new PrinterMemory(), FontLibrary.Empty)
    {
    }

    /// <summary>
    /// Creates a renderer that keeps downloaded graphics and fonts in <paramref name="memory"/> and uses only the
    /// built-in fonts. The window passes one memory for the whole session, so a graphic downloaded in one job can be
    /// used by a label in a later job.
    /// </summary>
    public ZplRenderer(PrinterMemory memory) : this(memory, FontLibrary.Empty)
    {
    }

    /// <summary>
    /// Creates a renderer that keeps downloaded graphics and fonts in <paramref name="memory"/> and finds fonts named by
    /// ^A@ and ^CW in <paramref name="fonts"/>. The window passes one memory and one library for the whole session.
    /// </summary>
    public ZplRenderer(PrinterMemory memory, FontLibrary fonts)
    {
        Memory = memory ?? throw new ArgumentNullException(nameof(memory));
        Fonts = fonts ?? throw new ArgumentNullException(nameof(fonts));
    }

    /// <summary>The fonts from the FontsFolder setting.</summary>
    public FontLibrary Fonts { get; }

    /// <summary>The printer memory this renderer stores downloads in and recalls them from.</summary>
    public PrinterMemory Memory { get; }

    /// <summary>
    /// Handles storage commands that arrive outside a label (<see cref="StorageCommands.TryHandle"/>). Tests replace it
    /// to simulate a failure no one expected, which must still become a warning instead of an error.
    /// </summary>
    internal Func<ZplCommand, PaintContext, List<RenderWarning>, bool> StorageHandler { get; set; } = StorageCommands.TryHandle;

    /// <summary>
    /// Renders every <c>^XA…^XZ</c> block in <paramref name="zpl"/>. Bad or unsupported ZPL never throws;
    /// it produces warnings instead.
    /// </summary>
    public RenderResult Render(string zpl, RenderOptions options)
    {
        // Jobs are drawn one at a time: printer setup and memory carry from one job to the next, as on a printer.
        lock (Memory.Setup.JobGate) return RenderJob(zpl, options);
    }

    private RenderResult RenderJob(string zpl, RenderOptions options)
    {
        var labels = new List<RenderedLabel>();
        var warnings = new List<RenderWarning>();
        var notes = new List<string>();
        var context = new PaintContext(Memory, notes, Fonts);
        var block = new List<ZplCommand>();
        var inLabel = false;
        // Line of the command being handled, for the last-resort warning below.
        var line = 1;

        try
        {
            foreach (var cmd in ZplParser.Parse(zpl ?? ""))
            {
                line = cmd.Line;
                switch (cmd.Name)
                {
                    case "^XA":
                        if (inLabel)
                        {
                            warnings.Add(new(cmd.Line, Text.Get("Render_XaBeforeXz")));
                            PaintSafely(block, options, warnings, labels, context);
                            block.Clear();
                        }
                        inLabel = true;
                        break;

                    case "^XZ":
                        if (inLabel)
                        {
                            PaintSafely(block, options, warnings, labels, context);
                            block.Clear();
                            inLabel = false;
                        }
                        else
                        {
                            warnings.Add(new(cmd.Line, Text.Get("Render_XzWithoutXa")));
                        }
                        break;

                    default:
                        if (inLabel) block.Add(cmd);
                        // ^CI before a label's ^XA is common (ZebraDesigner sends it there); it applies to that label.
                        else if (cmd.Name == "^CI")
                        {
                            if (LabelPainter.CheckCharacterSetCommand(cmd, cmd.Args.Split(','), warnings) is { } set)
                                context.PendingCharacterSet = (set, cmd.Line);
                        }
                        // Downloads usually travel outside any label; they act on printer memory right here, in stream order.
                        else if (HandleStorageSafely(cmd, context, warnings)) { }
                        else if (!SilentCommands.IsSilent(cmd.Name, cmd.Args))
                            warnings.Add(new(cmd.Line, Text.Get("Render_OutsideLabel", cmd.Name)));
                        break;
                }
            }

            if (inLabel)
            {
                warnings.Add(new(block.Count > 0 ? block[^1].Line : 1, Text.Get("Render_NoXz")));
                PaintSafely(block, options, warnings, labels, context);
            }
        }
        catch (Exception)
        {
            // Last line of defence: every known problem is already a warning, and a label that fails to paint is
            // caught on its own. Whatever still gets here (a bug) must not throw out of the renderer, which runs on a
            // socket thread: the labels drawn so far are returned, and the user is told where reading stopped. The
            // exception itself is not quoted, because even reading its message might fail.
            warnings.Add(new(line, Text.Get("Render_StoppedAtLine", line)));
        }

        return new RenderResult(labels, warnings) { MemoryNotes = notes };
    }

    /// <summary>
    /// Runs a storage command found outside a label. Every known problem is already a warning; anything else (a bug)
    /// becomes one too, so a broken download costs that download, never the labels after it.
    /// </summary>
    private bool HandleStorageSafely(ZplCommand cmd, PaintContext context, List<RenderWarning> warnings)
    {
        try
        {
            return StorageHandler(cmd, context, warnings);
        }
        catch (Exception)
        {
            // A fixed sentence, never the exception's own text: that is written for programmers, may be in another
            // language, and reading it can itself fail. Core has no log of its own, so the detail stays out of sight.
            warnings.Add(new(cmd.Line, Text.Get("Render_CommandInternalError", cmd.Name)));
            return true;
        }
    }

    /// <summary>
    /// Paints one block. A ^DF block is stored as a format instead and adds no label; any other block has its ^XF
    /// recalls replaced by the stored commands first (see <see cref="StoredFormats"/>). A failure inside Skia (for
    /// example, not enough memory for a very large label) becomes a warning, so one bad label cannot take the whole
    /// render down.
    /// </summary>
    private static void PaintSafely(List<ZplCommand> block, RenderOptions options, List<RenderWarning> warnings, List<RenderedLabel> labels, PaintContext context)
    {
        try
        {
            if (StoredFormats.TryStore(block, context, warnings)) return;
            var expanded = StoredFormats.Expand(block, context, warnings);
            // A label with ^SN or ^SF may need more images. They must see printer memory exactly as the first image saw
            // it at every command, so a copy is taken before the first image changes anything. Only such labels pay
            // for the copy; every other label takes the same path as before.
            var serialBlock = expanded.Any(c => c.Name is "^SN" or "^SF");
            var before = serialBlock ? context.Memory.Fork() : null;
            var characterSet = context.PendingCharacterSet;
            var first = LabelPainter.Paint(expanded, options, warnings, context, 0, out var serials);
            // A label without a serial field (almost every label) is one image whose Copies is ^PQ q, as always.
            if (serials is null || before is null)
            {
                labels.Add(first);
                return;
            }
            PaintSerialImages(expanded, options, warnings, labels, context.Fonts, before, characterSet, first, serials);
        }
        catch (Exception ex)
        {
            warnings.Add(new(block.Count > 0 ? block[0].Line : 1, Text.Get("Render_LabelNotDrawn", ex.Message)));
        }
    }

    /// <summary>
    /// A label with ^SN or ^SF: one image per distinct value, each with Copies set to how often that value prints
    /// (^PQ r, the last value what is left of q). The same expanded block is painted again, so a ^XF is not recalled
    /// twice.
    /// </summary>
    /// <remarks>
    /// Each later image is drawn against its own copy of <paramref name="before"/>, the memory as it was before the
    /// first image, with the same ^CI from before ^XA. A ~DG, ^ID or ^CW inside the label therefore acts at the same
    /// point on every image, while the real memory and printer setup are changed once, by the first image. The later
    /// images' warnings and memory notes are thrown away: they repeat the first image's line for line. Past
    /// <see cref="Serialization.MaxImages"/> values one warning gives the total and the last values, worked out without
    /// drawing them. A later image that fails ends the run with a warning; the images already drawn are kept.
    /// </remarks>
    private static void PaintSerialImages(IReadOnlyList<ZplCommand> block, RenderOptions options, List<RenderWarning> warnings, List<RenderedLabel> labels,
                                          FontLibrary fonts, PrinterMemory before, (int Set, int Line)? characterSet, RenderedLabel first, SerialRun serials)
    {
        var (quantity, replicates) = (serials.Quantity, serials.Replicates);
        var (_, distinct) = Serialization.Counts(quantity, replicates);
        labels.Add(first with { Copies = Serialization.CopiesOf(0, quantity, replicates) });

        var images = Math.Min(distinct, Serialization.MaxImages);
        var scratch = new List<RenderWarning>();
        for (long i = 1; i < images; i++)
        {
            scratch.Clear();
            var copy = new PaintContext(before.Fork(), [], fonts) { PendingCharacterSet = characterSet };
            RenderedLabel image;
            try
            {
                image = LabelPainter.Paint(block, options, scratch, copy, i, out _);
            }
            catch (Exception ex)
            {
                // The images drawn so far are right and stay; only the rest is missing.
                warnings.Add(new(block.Count > 0 ? block[0].Line : 1, Text.Get("Render_SerialStopped",
                    i, images.ToString(CultureInfo.InvariantCulture), ex.Message)));
                return;
            }
            labels.Add(image with { Copies = Serialization.CopiesOf(i, quantity, replicates) });
        }

        if (distinct > Serialization.MaxImages)
        {
            warnings.Add(new(serials.Fields[0].Spec.Line, Text.Get("Render_SerialCapped",
                distinct.ToString(CultureInfo.InvariantCulture), quantity.ToString(CultureInfo.InvariantCulture),
                Serialization.MaxImages, serials.ValuesAt(distinct - 1))));
        }
    }
}
