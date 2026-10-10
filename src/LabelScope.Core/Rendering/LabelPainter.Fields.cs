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

    // Recorded fields, judged against the label edges only in ToResult: a label cut to its drawing is not its final
    // size while the fields are drawn.
    private readonly List<PendingField> _fields = [];

    // Every field met in this label, recorded or not; past MaxFieldRecords only this number grows.
    private int _fieldCount;

    // Line of the first field that was not recorded, for the "too many" note.
    private int _firstUnlistedLine;

    // Line of the ^FO or ^FT of the field being built; 0 when the field has none. Unlike the position it does not
    // survive ^FS: a later field without its own origin is reported at its drawing command, not at an old ^FO.
    private int _originLine;

    // Size the fields are judged against on a side that was cut to the drawing: the sheet before the cut. Nothing
    // inked lies past the cut, so a field whose empty margin (a text descent, say) reaches past it is not "off" the label.
    private int? _judgeWidth, _judgeHeight;

    // True when ^PO I turned the finished picture after the cut, so the recorded boxes must be turned the same way.
    private bool _turnedAfterCut;

    /// <summary>A field as drawn, before its problem is judged against the final label size.</summary>
    /// <param name="Box">Device-space box in dots before any turn after the cut.</param>
    /// <param name="Judge">The part that must be on the label (the ink of a graphic, else the box); empty for a field not drawn.</param>
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
    /// The finished field list: each field judged against the final label size, turned with the label when ^PO I
    /// turned it after the cut, and, past the cap, one note that says how many fields there were.
    /// </summary>
    private List<LabelField> FinishFields()
    {
        int width = _bitmap.Width, height = _bitmap.Height;
        var judgeWidth = _judgeWidth ?? width;
        var judgeHeight = _judgeHeight ?? height;
        var result = new List<LabelField>(_fields.Count + 1);
        foreach (var f in _fields)
        {
            var problem = f.Problem ?? OffLabelProblem(f.Judge, judgeWidth, judgeHeight);
            // Whole dots that cover the box: a field from x 10.4 to 20.2 occupies dots 10 to 20.
            int x = (int)Math.Floor(f.Box.Left), y = (int)Math.Floor(f.Box.Top);
            int w = (int)Math.Ceiling(f.Box.Right) - x, h = (int)Math.Ceiling(f.Box.Bottom) - y;
            // The picture was turned half a circle after the cut, so every box turns with it.
            if (_turnedAfterCut) (x, y) = (width - x - w, height - y - h);
            result.Add(new LabelField(f.Kind, f.Summary, f.Data, x, y, w, h, f.Line, f.Detail, problem));
        }
        if (_fieldCount > MaxFieldRecords)
            result.Add(new LabelField(FieldKind.Text, Text.Get("Fields_TooMany", _fieldCount), "", 0, 0, 0, 0,
                _firstUnlistedLine, "", null));
        return result;
    }

    /// <summary>Null when <paramref name="judge"/> lies on the label; otherwise whether part or all of it is outside.</summary>
    private static string? OffLabelProblem(SKRect judge, int width, int height)
    {
        if (judge.Right <= EdgeSlack || judge.Bottom <= EdgeSlack || judge.Left >= width - EdgeSlack || judge.Top >= height - EdgeSlack)
            return Text.Get("Field_OffLabel");
        if (judge.Left < -EdgeSlack || judge.Top < -EdgeSlack || judge.Right > width + EdgeSlack || judge.Bottom > height + EdgeSlack)
            return Text.Get("Field_PartlyOffLabel");
        return null;
    }
}
