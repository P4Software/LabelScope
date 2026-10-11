using LabelScope.Core.Memory;

namespace LabelScope.Core.Rendering;

/// <summary>
/// Stored formats: ^DF keeps a label's commands in printer memory, and ^XF puts them back into a later label. The
/// renderer calls both on every ^XA..^XZ block before it is painted, so the painter only ever sees plain commands.
/// </summary>
internal static class StoredFormats
{
    /// <summary>Most formats recalled inside each other. Deeper nesting is almost certainly a mistake.</summary>
    internal const int MaxDepth = 8;

    /// <summary>
    /// Most commands one expansion may visit, recalls included. Without it, a format that recalls another one forty
    /// times, seven levels deep, would ask for billions of commands, or billions of recalls when the last format draws
    /// nothing. No real label comes near this number.
    /// </summary>
    internal const int MaxExpandedCommands = 100_000;

    /// <summary>
    /// Stores the block as a format when it contains ^DF, and returns true; returns false for any other block. The
    /// commands after the ^DF are stored. The commands before it act as in any label (a ^PW there still sets the
    /// label width), but the block draws no label of its own: the printer prints nothing for a ^DF label either.
    /// </summary>
    public static bool TryStore(IReadOnlyList<ZplCommand> block, PaintContext ctx, List<RenderWarning> w)
    {
        var index = -1;
        for (var i = 0; i < block.Count; i++)
            if (block[i].Name == "^DF") { index = i; break; }
        if (index < 0) return false;

        if (index > 0)
        {
            // Painted for its side effects only (label setup, downloads, warnings); the picture is thrown away.
            LabelPainter.Paint(block.Take(index).ToList(), new RenderOptions(), w, ctx);
        }

        var df = block[index];
        // The guide: a format is always a .ZPL file, whatever extension was sent.
        var name = ObjectName.Parse(df.Args.Split(',')[0], "ZPL", 'R') with { Extension = "ZPL" };
        // Copied: the renderer reuses its block list for the next label.
        var commands = block.Skip(index + 1).ToArray();
        var refusal = ctx.Memory.Store(name, new StoredFormat(commands));
        if (refusal is not null)
        {
            w.Add(new(df.Line, Text.Get("Common_Prefixed", $"^DF {name.Display}", refusal)));
            return true;
        }
        ctx.MemoryNotes.Add(Text.Get("Storage_StoredFormat", name.Display));
        return true;
    }

    /// <summary>
    /// The block with every ^XF replaced by the commands of the format it names. Each inserted command takes the line
    /// of the ^XF in this job, so a warning or a click on the label points at a line the user can see. A missing
    /// format, a format that recalls itself, nesting deeper than <see cref="MaxDepth"/> and more than
    /// <see cref="MaxExpandedCommands"/> commands each leave the recall out with a warning.
    /// </summary>
    public static IReadOnlyList<ZplCommand> Expand(IReadOnlyList<ZplCommand> block, PaintContext ctx, List<RenderWarning> w)
    {
        // Most labels recall nothing: they are returned as they are, without a copy.
        if (!block.Any(c => c.Name == "^XF")) return block;
        var expansion = new Expansion(ctx.Memory, w);
        expansion.Add(block, line: null, depth: 0);
        return expansion.Result;
    }

    /// <summary>One <see cref="Expand"/> call: the commands gathered so far and the formats being recalled right now.</summary>
    private sealed class Expansion(PrinterMemory memory, List<RenderWarning> warnings)
    {
        // Formats being inserted at this moment, outermost first: a format already here recalls itself. Compared as
        // objects, not names, so R:A.ZPL inside E:A.ZPL is not mistaken for a loop.
        private readonly List<StoredFormat> _active = [];
        // Recall warnings already given: a label that recalls one missing format a thousand times hears it once.
        private readonly HashSet<string> _warned = new(StringComparer.Ordinal);
        private bool _full;
        // Commands visited so far, ^XF included, counted against MaxExpandedCommands: every step of the work is
        // counted, so neither long formats nor formats made only of recalls can run without end.
        private int _visited;
        // ^XF arguments already looked up, and names already reported missing (see Look and Recall).
        private readonly Dictionary<string, (ObjectName Name, StoredFormat? Format)> _lookups = new(StringComparer.Ordinal);
        private readonly HashSet<string> _missing = new(StringComparer.Ordinal);

        /// <summary>The expanded commands.</summary>
        public List<ZplCommand> Result { get; } = [];

        /// <summary>
        /// Adds <paramref name="commands"/>, replacing each ^XF. <paramref name="line"/> is the line of the outermost
        /// ^XF (null for the label's own commands, which keep their lines); <paramref name="depth"/> is how many
        /// formats are being inserted around these commands.
        /// </summary>
        public void Add(IReadOnlyList<ZplCommand> commands, int? line, int depth)
        {
            foreach (var cmd in commands)
            {
                if (_full) return;
                if (++_visited > MaxExpandedCommands)
                {
                    // One warning, then nothing more is done: every loop of the recursion stops at once on _full.
                    _full = true;
                    warnings.Add(new(line ?? cmd.Line, Text.Get("Format_TooLarge", MaxExpandedCommands)));
                    return;
                }
                if (cmd.Name != "^XF")
                {
                    Result.Add(line is { } l ? cmd with { Line = l } : cmd);
                    continue;
                }
                Recall(cmd, line ?? cmd.Line, depth);
            }
        }

        /// <summary>Inserts the format one ^XF names, or warns why it cannot.</summary>
        private void Recall(ZplCommand xf, int line, int depth)
        {
            if (_full) return;
            var (name, format) = Look(xf.Args);
            var shown = (name.Drive is null ? name.OnDrive('R') : name).Display;
            if (format is null)
            {
                // Checked before the message is built: a fan-out may meet the same missing name 100 000 times.
                if (!_missing.Add(name.Key)) return;
                var searched = name.Drive is { } d ? $"{d}:" : string.Join(", ", ObjectName.SearchOrder.Select(c => $"{c}:"));
                Warn(line, Text.Get("Format_NotFound", shown, searched));
                return;
            }
            if (_active.Any(a => ReferenceEquals(a, format)))
            {
                Warn(line, Text.Get("Format_RecallsItself", shown));
                return;
            }
            if (depth >= MaxDepth)
            {
                Warn(line, Text.Get("Format_TooDeep", shown, MaxDepth));
                return;
            }
            _active.Add(format);
            Add(format.Commands, line, depth + 1);
            _active.RemoveAt(_active.Count - 1);
        }

        /// <summary>
        /// The name an ^XF argument gives and the format it finds, looked up once per distinct argument: memory does
        /// not change while one label is expanded, and a fan-out repeats the same few recalls many thousand times.
        /// </summary>
        private (ObjectName Name, StoredFormat? Format) Look(string args)
        {
            if (_lookups.TryGetValue(args, out var hit)) return hit;
            // Without a drive the format is searched on R:, E:, B:, A: in turn, as the guide says for ^XF.
            var name = ObjectName.Parse(args.Split(',')[0], "ZPL", null) with { Extension = "ZPL" };
            hit = (name, memory.Find(name, out _) as StoredFormat);
            _lookups[args] = hit;
            return hit;
        }

        /// <summary>Adds a recall warning unless the same one was already given for this label.</summary>
        private void Warn(int line, string message)
        {
            if (_warned.Add(message)) warnings.Add(new(line, message));
        }
    }
}
