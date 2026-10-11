using System.Globalization;
using System.Text;
using LabelScope.Core.Fonts;
using SkiaSharp;

namespace LabelScope.Core.Rendering;

/// <summary>
/// Field records: every drawn field leaves its kind, data, box on the label, ZPL line and a plain-language detail, so
/// the window can list the fields and select one when the label is clicked.
/// </summary>
internal sealed partial class LabelPainter
{
    /// <summary>
    /// Most fields recorded per label. A hostile job can hold tens of thousands of fields; the list in the window and
    /// the click test stay quick with this many, and nobody reads further. Past it one note says how many there were.
    /// </summary>
    internal const int MaxFieldRecords = 5000;

    /// <summary>Longest field data kept in a record; a 64 K text field is shown on the label, not copied 5000 times.</summary>
    private const int MaxRecordedData = 4096;

    /// <summary>Longest text quoted in a summary, so the Fields list stays one readable line.</summary>
    private const int MaxSummaryText = 40;

    /// <summary>
    /// Rounding slack when judging whether a field leaves the label: a rotated rectangle maps to coordinates such as
    /// 811.99997, which must not count as outside an 812-dot label.
    /// </summary>
    private const float EdgeSlack = 0.5f;

    // Recorded fields, judged against the label edges in ToResult, once the whole label has been drawn.
    private readonly List<PendingField> _fields = [];

    // Every field met in this label, recorded or not; past MaxFieldRecords only this number grows.
    private int _fieldCount;

    // Line of the first field that was not recorded, for the "too many" note.
    private int _firstUnlistedLine;

    // Line of the ^FO or ^FT of the field being built; 0 when the field has none. Unlike the position it does not
    // survive ^FS: a later field without its own origin is reported at its drawing command, not at an old ^FO.
    private int _originLine;

    /// <summary>A field as drawn, before its problem is judged against the final label size.</summary>
    /// <param name="Kind">What was drawn.</param>
    /// <param name="Summary">Short name in the current language.</param>
    /// <param name="Data">Field data, already cut to <see cref="MaxRecordedData"/>.</param>
    /// <param name="Box">Device-space box in dots (^PO I is already part of the canvas matrix).</param>
    /// <param name="Judge">The part that must be on the label (the ink of a graphic, else the box); empty for a field not drawn.</param>
    /// <param name="Line">ZPL line the field is reported at.</param>
    /// <param name="Detail">How it was drawn, in the current language.</param>
    /// <param name="Problem">Why it was not drawn; null when it was (the off-label check comes later).</param>
    private readonly record struct PendingField(FieldKind Kind, string Summary, string Data, SKRect Box, SKRect Judge,
                                                int Line, string Detail, string? Problem);

    /// <summary>
    /// Counts one field and says whether it is still recorded. Callers build the summary and detail only when it is,
    /// so a label with 100 000 fields does no text work past the cap.
    /// </summary>
    private bool RoomForField(int drawLine)
    {
        _fieldCount++;
        if (_fieldCount <= MaxFieldRecords) return true;
        if (_fieldCount == MaxFieldRecords + 1) _firstUnlistedLine = FieldLine(drawLine);
        return false;
    }

    /// <summary>The line a field is reported at: its ^FO or ^FT, or else the command that drew it.</summary>
    private int FieldLine(int drawLine) => _originLine > 0 ? _originLine : drawLine;

    /// <summary>
    /// Records a field whose <paramref name="local"/> box is in the current canvas coordinates; the canvas matrix
    /// (field rotation and ^PO I) turns it into label dots. Call <see cref="RoomForField"/> first.
    /// </summary>
    /// <param name="kind">What was drawn.</param>
    /// <param name="summary">Short name in the current language.</param>
    /// <param name="data">Field data (cut to <see cref="MaxRecordedData"/> here).</param>
    /// <param name="local">The field box in current canvas coordinates.</param>
    /// <param name="drawLine">Line of the drawing command, used when the field has no ^FO or ^FT.</param>
    /// <param name="detail">How it was drawn, in the current language.</param>
    /// <param name="judgeLocal">Part that must land on the label, when it differs from the box (the ink of a graphic).</param>
    /// <param name="problem">Why the field was not drawn; it wins over the off-label check.</param>
    private void RecordField(FieldKind kind, string summary, string data, SKRect local, int drawLine, string detail,
                             SKRect? judgeLocal = null, string? problem = null)
    {
        var matrix = _canvas.TotalMatrix;
        var box = matrix.MapRect(local);
        var judge = problem is null ? matrix.MapRect(judgeLocal ?? local) : SKRect.Empty;
        _fields.Add(new PendingField(kind, summary, Clip(data, MaxRecordedData), box, judge, FieldLine(drawLine), detail, problem));
    }

    /// <summary>Records a field that was not drawn at all: an empty box at the field origin, with the reason as its problem.</summary>
    private void RecordNotDrawn(FieldKind kind, string summary, string data, int drawLine, string detail, string problem) =>
        RecordField(kind, summary, data, new SKRect(_x, _y, _x, _y), drawLine, detail, problem: problem);

    /// <summary>
    /// How a text field was drawn: the font (letter, or the file named by ^A@) and its height in dots, plus the ^FB
    /// block when there is one. Uses only what the font already knows, so no text is measured again.
    /// </summary>
    private string TextDetail(ZplFont font)
    {
        var letter = char.ToUpperInvariant(_fieldFont ?? _defaultFont);
        var name = letter == '@' && _lastFontFile is { } file ? file.Display : letter.ToString();
        var height = (int)Math.Round(font.LineHeight);
        return _fieldBlock is { } b
            ? Text.Get("Field_TextBlockDetail", name, height, b.Width, b.MaxLines)
            : Text.Get("Field_TextDetail", name, height);
    }

    /// <summary>The text quoted in a summary, cut to a readable length.</summary>
    private static string Quote(string text) => Clip(text, MaxSummaryText);

    /// <summary>Cuts <paramref name="text"/> to <paramref name="max"/> characters with an ellipsis, never between the halves of a surrogate pair.</summary>
    private static string Clip(string text, int max)
    {
        if (text.Length <= max) return text;
        var cut = char.IsHighSurrogate(text[max - 1]) ? max - 1 : max;
        return string.Concat(text.AsSpan(0, cut), "…");
    }

    /// <summary>
    /// The finished field list: each field judged against the label size (from the ZPL or the loaded label) and,
    /// past the cap, one note that says how many fields there were.
    /// </summary>
    private List<LabelField> FinishFields()
    {
        int width = _bitmap.Width, height = _bitmap.Height;
        var result = new List<LabelField>(_fields.Count + 1);
        foreach (var f in _fields)
        {
            var problem = f.Problem ?? OffLabelProblem(f.Kind, f.Judge, width, height);
            // Whole dots that cover the box: a field from x 10.4 to 20.2 occupies dots 10 to 20.
            int x = (int)Math.Floor(f.Box.Left), y = (int)Math.Floor(f.Box.Top);
            int w = (int)Math.Ceiling(f.Box.Right) - x, h = (int)Math.Ceiling(f.Box.Bottom) - y;
            result.Add(new LabelField(f.Kind, f.Summary, f.Data, x, y, w, h, f.Line, f.Detail, problem));
        }
        if (_fieldCount > MaxFieldRecords)
            result.Add(new LabelField(FieldKind.Text, Text.Get("Fields_TooMany", _fieldCount), "", 0, 0, 0, 0,
                _firstUnlistedLine, "", null));
        return result;
    }

    /// <summary>
    /// Null when <paramref name="judge"/> lies on the label; otherwise whether part or all of it is outside. A barcode
    /// that is only partly on the label gets its own message: the visible part looks fine, but it will not scan.
    /// </summary>
    private static string? OffLabelProblem(FieldKind kind, SKRect judge, int width, int height)
    {
        if (judge.Right <= EdgeSlack || judge.Bottom <= EdgeSlack || judge.Left >= width - EdgeSlack || judge.Top >= height - EdgeSlack)
            return Text.Get("Field_OffLabel");
        if (judge.Left < -EdgeSlack || judge.Top < -EdgeSlack || judge.Right > width + EdgeSlack || judge.Bottom > height + EdgeSlack)
            return kind == FieldKind.Barcode ? Text.Get("Field_BarcodePartlyOffLabel") : Text.Get("Field_PartlyOffLabel");
        return null;
    }

    // ---- field numbers (^FN) and concatenation (^FE) ----------------------------------------------

    /// <summary>Highest field number ^FN accepts (the guide: 0 to 9999).</summary>
    private const int MaxFieldNumber = 9999;

    /// <summary>Most field numbers listed in the "got no data" warning; the rest are shown as "…".</summary>
    private const int MaxListedNumbers = 10;

    /// <summary>Longest reference quoted in a ^FE warning.</summary>
    private const int MaxShownReference = 40;

    /// <summary>What <see cref="PlanFieldNumbers"/> decided for one field.</summary>
    /// <param name="Data">Data the field draws instead of its own ^FD; null to keep its own.</param>
    /// <param name="HexIndicator">The ^FH indicator that goes with <paramref name="Data"/> (the supplier's).</param>
    /// <param name="Skip">True when the field only supplied data for numbered fields and is not drawn itself.</param>
    /// <param name="Serial">The supplier's ^SN, which counts the data it gave; null to keep the field's own counter. Only
    /// ^SN travels, because ^SN is data; a ^SF modifies the field it is written on, and a supplier is never drawn, so
    /// its ^SF is dropped (as other ZPL viewers do; the guide is silent).</param>
    /// <param name="Clock">The supplier's ^FC, which goes with the data it wrote, like its ^FH; null to keep the field's own.</param>
    private readonly record struct FieldPlan(string? Data, char? HexIndicator, bool Skip, SnSpec? Serial = null, ClockIndicators? Clock = null);

    // Decisions per field, keyed by the position of the field's ^FS in the painted block; null when the label has no
    // ^FN or ^FE at all, which is almost every label.
    private Dictionary<int, FieldPlan>? _fieldPlans;

    // Position in the painted block of the command being handled, set by Paint before each command.
    private int _commandIndex;

    // Line of the ^FN of the field being built (0 = none), where a field that takes recalled data is reported.
    private int _fieldNumberLine;

    /// <summary>The plan for the field the current ^FS ends, if any.</summary>
    private FieldPlan? FieldPlanHere() =>
        _fieldPlans is not null && _fieldPlans.TryGetValue(_commandIndex, out var plan) ? plan : null;

    /// <summary>
    /// Reads the label once, before drawing, to decide where ^FN data goes. A field is everything up to its ^FS.
    /// A field with ^FNn and no data waits for data. A later field with ^FNn and ^FD (or ^FV) fills every field
    /// still waiting for n and is then not drawn itself, which is how the ^FN..^FD..^FS lines of a label that recalls
    /// a format work. When no field is waiting it is drawn like any field, so a plain label that happens to number its
    /// fields still prints. A field still waiting at the end draws nothing, with one warning for the label.
    /// </summary>
    /// <remarks>
    /// This order (only fields before the supplier are filled; a second supplier finds nothing waiting and is drawn)
    /// follows how other ZPL viewers behave, because the guide does not say. ^FE references are resolved here as well, from
    /// the data numbered fields gave before the field.
    /// </remarks>
    /// <param name="copyIndex">The serial value this image draws: a ^FE reference to a numbered ^SN field sees the counted
    /// value of this image, not the start value.</param>
    private static Dictionary<int, FieldPlan>? PlanFieldNumbers(IReadOnlyList<ZplCommand> block, List<RenderWarning> warnings, long copyIndex)
    {
        if (!block.Any(c => c.Name is "^FN" or "^FE")) return null;
        // Only the painter warns about ^SN; this scan reads it again silently.
        const List<RenderWarning>? silent = null;

        var plans = new Dictionary<int, FieldPlan>();
        var known = new Dictionary<int, string>();
        // Fields waiting for data, per number, with the line of the first one (for the warning).
        var waiting = new Dictionary<int, (int Line, List<int> Fields)>();
        var badNumberWarned = false;

        int? number = null;
        var numberLine = 0;
        string? data = null;
        char? hex = null, delimiter = null;
        var concatenated = false;
        SnSpec? serial = null;
        ClockIndicators? clock = null;

        for (var index = 0; index < block.Count; index++)
        {
            var cmd = block[index];
            switch (cmd.Name)
            {
                case "^FN":
                    number = ReadFieldNumber(cmd, warnings, ref badNumberWarned);
                    numberLine = cmd.Line;
                    break;
                case "^FH":
                    // The same reading as the painter's ^FH, so the supplier's escapes decode the same way.
                    var a = cmd.Args.Split(',');
                    hex = a[0].Length > 0 ? a[0][0] : '_';
                    break;
                case "^FC":
                    clock = FieldClock.ReadIndicators(cmd.Args);
                    break;
                case "^FE":
                    delimiter = cmd.Args.Length > 0 ? cmd.Args[0] : '#';
                    break;
                case "^FD":
                case "^FV":
                    // ^FE applies to the next ^FD only.
                    concatenated = delimiter is not null;
                    data = delimiter is { } d ? Concatenate(cmd, d, known, warnings) : cmd.Args;
                    delimiter = null;
                    serial = null; // as in the painter: ^FD replaces the ^SN data and its counter
                    break;
                case "^SN":
                    // ^SN is data too, so a numbered ^SN field supplies its number rather than waiting for data.
                    (data, var sn) = Serialization.ReadSn(cmd, silent);
                    serial = sn;
                    concatenated = false;
                    break;
                case "^FS":
                    // A field is known by the position of its ^FS, the same key the painter looks up.
                    var field = index;
                    if (number is { } n)
                    {
                        if (data is null)
                        {
                            if (!waiting.TryGetValue(n, out var w)) waiting[n] = w = (numberLine, []);
                            w.Fields.Add(field);
                        }
                        else
                        {
                            // ^FE sees what the field prints on this image. The counter runs on the data before ^FH
                            // decoding here (the painter counts after it); ^SN data rarely carries ^FH escapes.
                            known[n] = serial is { } counted ? counted.Apply(data, copyIndex) : data;
                            if (waiting.Remove(n, out var w))
                            {
                                foreach (var target in w.Fields) plans[target] = new FieldPlan(data, hex, false, serial, clock);
                                plans[field] = new FieldPlan(null, null, true);
                            }
                            else if (concatenated)
                            {
                                plans[field] = new FieldPlan(data, hex, false);
                            }
                        }
                    }
                    else if (concatenated && data is not null)
                    {
                        plans[field] = new FieldPlan(data, hex, false);
                    }
                    number = null; data = null; hex = null; delimiter = null; concatenated = false; serial = null; clock = null;
                    break;
            }
        }

        if (waiting.Count > 0)
        {
            // One warning per label, however many numbers wait: a label of 10 000 empty fields is one problem.
            var ordered = waiting.OrderBy(kv => kv.Value.Line).ThenBy(kv => kv.Key).ToList();
            var shown = string.Join(", ", ordered.Take(MaxListedNumbers).Select(kv => kv.Key.ToString(CultureInfo.InvariantCulture)));
            if (ordered.Count > MaxListedNumbers) shown += ", …";
            warnings.Add(new(ordered[0].Value.Line, Text.Get("Painter_FnNoData", shown)));
        }
        return plans;
    }

    /// <summary>
    /// The number of ^FN#"prompt" (0 when none is given). The prompt is only shown by keypads, never printed. A number
    /// outside 0 to 9999 gives null, so the field is drawn as an unnumbered one; the first such number in a label is
    /// warned about.
    /// </summary>
    private static int? ReadFieldNumber(ZplCommand cmd, List<RenderWarning> warnings, ref bool warned)
    {
        var quote = cmd.Args.IndexOf('"');
        var text = (quote >= 0 ? cmd.Args[..quote] : cmd.Args).Trim();
        if (text.Length == 0) return 0;
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n <= MaxFieldNumber) return n;
        if (!warned)
        {
            warned = true;
            warnings.Add(new(cmd.Line, Text.Get("Painter_FnBadNumber", Clip(text, 20))));
        }
        return null;
    }

    /// <summary>
    /// ^FE: the ^FD text with every <c>#n#</c> replaced by the data of field n, and every <c>#n,f,x,y#</c> or
    /// <c>#n,b,x,y#</c> by part of it (y characters from position x, counted from the start or from the end).
    /// Text between delimiters that is not a reference stays as it is. The result is cut just past
    /// <see cref="MaxTextFieldCharacters"/>, so many references to long data cannot build a huge string; DrawText then
    /// says the text was too long. At most one warning per field.
    /// </summary>
    private static string Concatenate(ZplCommand cmd, char delimiter, Dictionary<int, string> known, List<RenderWarning> warnings)
    {
        var raw = cmd.Args;
        var result = new StringBuilder();
        var warned = false;
        void Warn(string message)
        {
            if (warned) return;
            warned = true;
            warnings.Add(new(cmd.Line, message));
        }

        var i = 0;
        while (i < raw.Length && result.Length <= MaxTextFieldCharacters)
        {
            if (raw[i] != delimiter)
            {
                result.Append(raw[i++]);
                continue;
            }
            var close = raw.IndexOf(delimiter, i + 1);
            if (close < 0)
            {
                result.Append(raw, i, raw.Length - i);
                break;
            }
            var inner = raw.Substring(i + 1, close - i - 1);
            var shown = Clip(delimiter + inner + delimiter, MaxShownReference);
            switch (ReadReference(inner, out var number, out var backward, out var position, out var count))
            {
                case ReferenceShape.NotAReference:
                    // A lone delimiter in ordinary text ("Item #5"): kept, and the search goes on after it.
                    result.Append(raw[i++]);
                    continue;
                case ReferenceShape.BadNumber:
                    Warn(Text.Get("Painter_FeBadNumber", shown));
                    break;
                case var shape:
                    if (!known.TryGetValue(number, out var value))
                        Warn(Text.Get("Painter_FeNoData", shown, number));
                    else if (shape == ReferenceShape.Whole)
                        AppendCapped(result, value, 0, value.Length);
                    else
                        AppendPart(result, value, backward, position, count);
                    break;
            }
            i = close + 1;
        }
        if (result.Length > MaxTextFieldCharacters + 1) result.Length = MaxTextFieldCharacters + 1;
        return result.ToString();
    }

    /// <summary>Appends the part of field data a <c>f|b,x,y</c> reference asks for.</summary>
    private static void AppendPart(StringBuilder result, string value, bool backward, int position, int count)
    {
        // The guide: a position of 0 or less means the insertion is ignored; a count of 0 or less inserts nothing.
        if (position <= 0 || count <= 0 || position > value.Length) return;
        int start, length;
        if (backward)
        {
            // Position 1 is the last character; the part ends there and reaches back count characters.
            var end = value.Length - position + 1;
            start = Math.Max(0, end - count);
            length = end - start;
        }
        else
        {
            start = position - 1;
            length = Math.Min(count, value.Length - start);
        }
        AppendCapped(result, value, start, length);
    }

    /// <summary>
    /// Appends part of <paramref name="value"/>, never more than the cap can still take, so many references to 64 K of
    /// data stay cheap.
    /// </summary>
    private static void AppendCapped(StringBuilder result, string value, int start, int length)
    {
        var room = MaxTextFieldCharacters + 1 - result.Length;
        if (room > 0) result.Append(value, start, Math.Min(length, room));
    }

    /// <summary>What the text between two ^FE delimiters is.</summary>
    private enum ReferenceShape { NotAReference, BadNumber, Whole, Part }

    /// <summary>
    /// Reads <c>n</c> or <c>n,f|b,x,y</c>. Anything else is not a reference. A reference whose numbers are too large
    /// (a field number above 9999, a position that is not a whole number LabelScope can hold) is a bad number.
    /// </summary>
    private static ReferenceShape ReadReference(string inner, out int number, out bool backward, out int position, out int count)
    {
        number = 0; backward = false; position = 0; count = 0;
        var parts = inner.Split(',');
        if (parts.Length is not (1 or 4) || !IsDigits(parts[0].Trim())) return ReferenceShape.NotAReference;
        if (parts.Length == 4)
        {
            var direction = parts[1].Trim().ToUpperInvariant();
            if (direction is not ("F" or "B") || !IsInteger(parts[2].Trim()) || !IsInteger(parts[3].Trim()))
                return ReferenceShape.NotAReference;
            backward = direction == "B";
            if (!int.TryParse(parts[2].Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out position) ||
                !int.TryParse(parts[3].Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out count))
                return ReferenceShape.BadNumber;
        }
        if (!int.TryParse(parts[0].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out number) || number > MaxFieldNumber)
            return ReferenceShape.BadNumber;
        return parts.Length == 1 ? ReferenceShape.Whole : ReferenceShape.Part;
    }

    private static bool IsDigits(string s) => s.Length > 0 && s.All(char.IsAsciiDigit);

    private static bool IsInteger(string s) => IsDigits(s.StartsWith('-') || s.StartsWith('+') ? s[1..] : s);
}
