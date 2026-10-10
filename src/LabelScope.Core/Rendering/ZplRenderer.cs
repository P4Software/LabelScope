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
    /// Renders every <c>^XA…^XZ</c> block in <paramref name="zpl"/>. Bad or unsupported ZPL never throws;
    /// it produces warnings instead.
    /// </summary>
    public RenderResult Render(string zpl, RenderOptions options)
    {
        var labels = new List<RenderedLabel>();
        var warnings = new List<RenderWarning>();
        var notes = new List<string>();
        var context = new PaintContext(Memory, notes, Fonts);
        var block = new List<ZplCommand>();
        var inLabel = false;

        foreach (var cmd in ZplParser.Parse(zpl ?? ""))
        {
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
                    else if (StorageCommands.TryHandle(cmd, context, warnings)) { }
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

        return new RenderResult(labels, warnings) { MemoryNotes = notes };
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
