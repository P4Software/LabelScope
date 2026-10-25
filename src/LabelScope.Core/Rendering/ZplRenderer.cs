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
        // "Now" is read once per job: every label and serial image of the job shows the same clock (see PaintContext.Now).
        var context = new PaintContext(Memory, notes, Fonts) { Now = options.Clock ?? DateTimeOffset.Now };
        var block = new List<ZplCommand>();
        var inLabel = false;
        // Line of the command being handled, for the last-resort warning below.
        var line = 1;

        // ^XA..^XZ blocks met, and how many of them only stored in or deleted from printer memory instead of printing
        // (see PaintSafely). The window uses them to show a job made only of such blocks as "stored in memory", not as
        // a label that could not be drawn.
        var blocks = 0;
        var storedBlocks = 0;
        void PaintBlock()
        {
            blocks++;
            if (PaintSafely(block, options, warnings, labels, context)) storedBlocks++;
        }

        try
        {
            // The prefixes and delimiter are printer state: this job is read with what the last one left set, and the
            // next job with what this one leaves. The parser applies the changes itself, in stream order.
            var setup = Memory.Setup;
            var start = setup.Current.Prefixes ?? ZplPrefixes.Default;
            var refused = new List<ZplCommand>();
            var commands = ZplParser.Parse(zpl ?? "", start, out var end, refused);
            if (end != start) setup.Apply(PrinterSetup.Values.None with { Prefixes = end });
            var nextRefused = 0;

            foreach (var cmd in commands)
            {
                line = cmd.Line;
                switch (cmd.Name)
                {
                    case "^CC" or "^CD" or "^CT" or "~CC" or "~CD" or "~CT":
                        // Already applied by the parser; only a refused change has something to say. The refused list is
                        // in stream order, so one index walks it.
                        if (nextRefused < refused.Count && ReferenceEquals(refused[nextRefused], cmd))
                        {
                            nextRefused++;
                            warnings.Add(new(cmd.Line, Text.Get("Render_PrefixRefused", cmd.Name, ShownCharacter(cmd.Args))));
                        }
                        // ':' and '.' are accepted as a printer accepts them, but they also live inside parameters:
                        // the drive in R:LOGO.GRF, a file extension, a decimal number. Those now split, so say so.
                        else if (cmd.Name is "^CD" or "~CD" && cmd.Args is ":" or ".")
                        {
                            warnings.Add(new(cmd.Line, Text.Get("Render_DelimiterSplitsNames", cmd.Name, cmd.Args)));
                        }
                        break;

                    case "^XA":
                        if (inLabel)
                        {
                            warnings.Add(new(cmd.Line, Text.Get("Render_XaBeforeXz")));
                            PaintBlock();
                            block.Clear();
                        }
                        inLabel = true;
                        break;

                    case "^XZ":
                        if (inLabel)
                        {
                            PaintBlock();
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
                        // ^CI before a label's ^XA is common (ZebraDesigner sends it there). It is printer state: it
                        // applies to the labels that follow, in this job and later ones, until the next ^CI.
                        else if (cmd.Name == "^CI") LabelPainter.SetCharacterSetSetting(cmd, context, warnings);
                        // ^ST sets the printer clock, which is printer state: setup jobs often send it on its own.
                        else if (cmd.Name == "^ST") LabelPainter.SetClock(cmd, context, warnings);
                        // ^SO and ^SL are clock settings a printer keeps, so they count before ^XA as well.
                        else if (cmd.Name is "^SO" or "^SL") LabelPainter.SetClockSetting(cmd, context, warnings);
                        // ^MC is printer state too: a ^MCN before ^XA keeps the labels that follow.
                        else if (cmd.Name == "^MC") LabelPainter.SetMapClearSetting(cmd, context, warnings);
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
                PaintBlock();
            }

            // A job written with ^ that arrives after an earlier job changed the format prefix reads as plain text, so
            // nothing at all is drawn. A printer would print nothing either, but the user deserves to know why.
            if (start.Format != '^' && labels.Count == 0 && !commands.Any(c => c.Name == "^XA")
                && (zpl ?? "").Contains("^XA", StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add(new(1, Text.Get("Render_PrefixStillChanged", start.Format.ToString(), start.Control.ToString())));
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

        return new RenderResult(labels, warnings) { MemoryNotes = notes, LabelBlocks = blocks, StoredOnlyBlocks = storedBlocks };
    }

    /// <summary>
    /// A refused prefix character as a warning shows it: the character itself when it can be seen, otherwise its Unicode
    /// number (a space or a control character would be invisible in the message).
    /// </summary>
    private static string ShownCharacter(string value)
    {
        if (value.Length == 0) return "";
        var c = value[0];
        return char.IsWhiteSpace(c) || char.IsControl(c) ? $"U+{(int)c:X4}" : $"'{c}'";
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
    /// recalls replaced by the stored commands first (see <see cref="StoredFormats"/>). A block that a printer paints
    /// but does not print adds no label either: one with ^IS ..,N, and one that drew nothing and only stored in or
    /// deleted from printer memory. A failure inside Skia (for example, not enough memory for a very large label)
    /// becomes a warning, so one bad label cannot take the whole render down.
    /// </summary>
    /// <returns>
    /// True when the block printed nothing and only stored in or deleted from printer memory: a ^DF block (stored, or
    /// refused with a warning), a ^IS ..,N block, or a block of memory commands only.
    /// </returns>
    private static bool PaintSafely(List<ZplCommand> block, RenderOptions options, List<RenderWarning> warnings, List<RenderedLabel> labels, PaintContext context)
    {
        try
        {
            if (StoredFormats.TryStore(block, context, warnings)) return true;
            var expanded = StoredFormats.Expand(block, context, warnings);
            // A label with ^SN or ^SF may need more images. They must see printer memory exactly as the first image saw
            // it at every command, so a copy is taken before the first image changes anything. Only such labels pay
            // for the copy; every other label takes the same path as before.
            var serialBlock = expanded.Any(c => c.Name is "^SN" or "^SF");
            // A label that may end under ^MCN is painted a second time, without its ^FV fields, for the next label's
            // background; that paint needs the same copy of memory as it was before the label.
            var setup = context.Memory.Setup;
            var mayKeep = setup.Current.KeepImage == true || expanded.Any(c => c.Name == "^MC");
            var before = serialBlock || mayKeep ? context.Memory.Fork() : null;
            // Read before painting, to tell afterwards whether the block did anything but printer-memory work.
            var notesBefore = context.MemoryNotes.Count;
            var onKeptBackground = setup.KeptBackground is not null;
            var first = LabelPainter.Paint(expanded, options, warnings, context, 0, out var serials, out var imageSavePrints);

            // A block a printer does not print is no label. ^IS ..,N stores the label as an image only. A block that
            // drew no field and only stored in or deleted from printer memory feeds no label either: a WMS that loads
            // a template with ^IL ends the job with ^XA^IDR:x.GRF^FS^XZ, and a printer prints one label, not two. Both
            // are still painted above, because that stored the image and kept the printer setup the block sent. A
            // block that drew nothing and changed no memory (^XA^PW400^XZ) is still shown as an empty label, and so
            // is a ^IS ..,Y block or one drawn on a background ^MCN kept, which a printer does print.
            var storedOnly = imageSavePrints == false
                || (imageSavePrints is null && !onKeptBackground && first.Fields.Count == 0 && context.MemoryNotes.Count > notesBefore);

            // A label without a serial field (almost every label) is one image whose Copies is ^PQ q, as always.
            // The serial value the ^MCN background shows: the last one the printer prints (0 without serial fields).
            // A block that is not printed adds no image, not even for its serial values: the printer prints none.
            var keptValue = 0L;
            if (!storedOnly)
            {
                if (serials is null || before is null)
                    labels.Add(first);
                else
                    keptValue = PaintSerialImages(expanded, options, warnings, labels, context, before, first, serials);
            }

            // ^MC as it stands at the end of the label decides what the next label starts on. Labels that never met a
            // ^MCN (almost all) only read two values here.
            if (setup.Current.KeepImage == true && before is not null)
                KeepImage(expanded, options, warnings, context, before, keptValue);
            else if (setup.KeptBackground is not null)
                setup.KeptBackground = null;
            return storedOnly;
        }
        catch (Exception ex)
        {
            warnings.Add(new(block.Count > 0 ? block[0].Line : 1, Text.Get("Render_LabelNotDrawn", ex.Message)));
        }
        return false;
    }

    /// <summary>
    /// A label with ^SN or ^SF: one image per distinct value, each with Copies set to how often that value prints
    /// (^PQ r, the last value what is left of q). The same expanded block is painted again, so a ^XF is not recalled
    /// twice.
    /// </summary>
    /// <remarks>
    /// Each later image is drawn against its own copy of <paramref name="before"/>, the memory as it was before the
    /// first image, whose printer setup holds the ^CI in effect before ^XA. A ~DG, ^ID, ^CW or ^CI inside the label therefore acts at the same
    /// point on every image, while the real memory and printer setup are changed once, by the first image. The later
    /// images' warnings and memory notes are thrown away: they repeat the first image's line for line. Past
    /// <see cref="Serialization.MaxImages"/> values, or once the job's pixel budget
    /// (<see cref="PaintContext.SerialPixelsLeft"/>) has no room for one more image, one warning gives the total and
    /// the last values, worked out without drawing them. The first image is always drawn and always charged. A later
    /// image that fails ends the run with a warning; the images already drawn are kept.
    /// </remarks>
    /// <returns>
    /// The value number of the image ^MCN keeps for the next label: the last value the printer prints, even when
    /// LabelScope stopped drawing earlier (the background is painted for that value directly), or the last image drawn
    /// when a later image failed.
    /// </returns>
    private static long PaintSerialImages(IReadOnlyList<ZplCommand> block, RenderOptions options, List<RenderWarning> warnings, List<RenderedLabel> labels,
                                          PaintContext context, PrinterMemory before, RenderedLabel first, SerialRun serials)
    {
        var (quantity, replicates) = (serials.Quantity, serials.Replicates);
        var (_, distinct) = Serialization.Counts(quantity, replicates);
        labels.Add(first with { Copies = Serialization.CopiesOf(0, quantity, replicates) });

        // Every image of one label has the size of the first: the same block is painted against the same setup.
        var pixelsPerImage = Math.Max(1L, (long)first.WidthDots * first.HeightDots);
        context.SerialPixelsLeft -= pixelsPerImage;
        var images = Math.Min(distinct, Serialization.MaxImages);
        var scratch = new List<RenderWarning>();
        var drawn = 1L;
        for (long i = 1; i < images; i++)
        {
            if (context.SerialPixelsLeft < pixelsPerImage) break;
            context.SerialPixelsLeft -= pixelsPerImage;
            scratch.Clear();
            // The same moment as the first image: a job is drawn at one time.
            var copy = new PaintContext(before.Fork(), [], context.Fonts) { Now = context.Now };
            RenderedLabel image;
            try
            {
                image = LabelPainter.Paint(block, options, scratch, copy, i, out _, out _);
            }
            catch (Exception ex)
            {
                // The images drawn so far are right and stay; only the rest is missing.
                warnings.Add(new(block.Count > 0 ? block[0].Line : 1, Text.Get("Render_SerialStopped",
                    i, images.ToString(CultureInfo.InvariantCulture), ex.Message)));
                return i - 1;
            }
            labels.Add(image with { Copies = Serialization.CopiesOf(i, quantity, replicates) });
            drawn++;
        }

        if (drawn < distinct)
        {
            // Stopped by the pixel budget before the image cap: say the labels are too large, not only too many.
            var key = drawn < images ? "Render_SerialCappedLarge" : "Render_SerialCapped";
            warnings.Add(new(serials.Fields[0].Spec.Line, Text.Get(key,
                distinct.ToString(CultureInfo.InvariantCulture), quantity.ToString(CultureInfo.InvariantCulture),
                drawn, serials.ValuesAt(distinct - 1))));
        }
        // The printer prints every value, so its last label (and the ^MCN background it leaves) shows the last value.
        return distinct - 1;
    }

    /// <summary>
    /// ^MCN: keeps the label just drawn, painted again without its ^FV fields, as the background of the next label. A
    /// printer prints the values of a serial label in order, so the background shows the last value it prints (its ^FD
    /// and ^SN fields with their last values), even when LabelScope drew fewer images than the printer prints: the
    /// background is painted for that value directly.
    /// </summary>
    /// <remarks>
    /// The second paint follows the serial images' pattern: <paramref name="before"/> (memory as it was before the label,
    /// used by no one else any more, so its setup holds the ^CI from before ^XA) and the same moment, and a warnings list nobody
    /// reads. So a download, ^ID, ^CW or ^IS in the label acts on the copy, and every warning and memory note is given
    /// once, by the first paint. A failure here (for example no memory for a second 40-megapixel bitmap) costs only the
    /// background: the label is already drawn, so it gets its own warning, never "label not drawn", and nothing stale is
    /// left to draw under the next label.
    /// </remarks>
    private static void KeepImage(IReadOnlyList<ZplCommand> block, RenderOptions options, List<RenderWarning> warnings, PaintContext context,
                                  PrinterMemory before, long copyIndex)
    {
        var setup = context.Memory.Setup;
        try
        {
            var copy = new PaintContext(before, [], context.Fonts) { Now = context.Now };
            setup.KeptBackground = LabelPainter.PaintKeptImage(block, options, copy, copyIndex);
        }
        catch (Exception ex)
        {
            setup.KeptBackground = null;
            warnings.Add(new(block.Count > 0 ? block[0].Line : 1, Text.Get("Render_MapClearNotKept", ex.Message)));
        }
    }
}
