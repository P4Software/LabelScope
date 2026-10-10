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
                            warnings.Add(new(cmd.Line, "A new label (^XA) started before the previous one ended with ^XZ. The earlier label was drawn anyway."));
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
                            warnings.Add(new(cmd.Line, "^XZ was found without a matching ^XA and was ignored."));
                        }
                        break;

                    default:
                        if (inLabel) block.Add(cmd);
                        // Downloads usually travel outside any label; they act on printer memory right here, in stream order.
                        else if (HandleStorageSafely(cmd, context, warnings)) { }
                        else if (!SilentCommands.IsSilent(cmd.Name, cmd.Args))
                            warnings.Add(new(cmd.Line, $"{cmd.Name} is outside a label (^XA … ^XZ) or not supported yet, and was ignored."));
                        break;
                }
            }

            if (inLabel)
            {
                warnings.Add(new(block.Count > 0 ? block[^1].Line : 1, "The label did not end with ^XZ; it was drawn as far as it arrived."));
                PaintSafely(block, options, warnings, labels, context);
            }
        }
        catch (Exception)
        {
            // Last line of defence: every known problem is already a warning, and a label that fails to paint is
            // caught on its own. Whatever still gets here (a bug) must not throw out of the renderer, which runs on a
            // socket thread: the labels drawn so far are returned, and the user is told where reading stopped. The
            // exception itself is not quoted, because even reading its message might fail.
            warnings.Add(new(line, $"LabelScope stopped reading this job at line {line} because of a problem it did not expect; the labels before that line are shown. Send the job again, and if this keeps happening, report it with the job attached."));
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
            warnings.Add(new(cmd.Line, $"{cmd.Name} could not be handled because of an internal error; nothing was stored."));
            return true;
        }
    }

    /// <summary>
    /// Paints one block. A failure inside Skia (for example, not enough memory for a very large label)
    /// becomes a warning, so one bad label cannot take the whole render down.
    /// </summary>
    private static void PaintSafely(List<ZplCommand> block, RenderOptions options, List<RenderWarning> warnings, List<RenderedLabel> labels, PaintContext context)
    {
        try
        {
            labels.Add(LabelPainter.Paint(block, options, warnings, context));
        }
        catch (Exception ex)
        {
            warnings.Add(new(block.Count > 0 ? block[0].Line : 1, $"This label could not be drawn ({ex.Message})."));
        }
    }
}
