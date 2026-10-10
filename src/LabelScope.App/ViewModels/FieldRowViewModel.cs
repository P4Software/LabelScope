using LabelScope.App.Localization;
using LabelScope.Core.Localization;
using LabelScope.Core.Rendering;

namespace LabelScope.App.ViewModels;

/// <summary>
/// One row of the Fields tab: a field drawn on the label, with its position, how it was drawn, its ZPL line and,
/// when it is not completely on the label, why. Texts are read on each use so they follow a language switch.
/// </summary>
public sealed class FieldRowViewModel
{
    /// <summary>Creates the row for <paramref name="field"/>, the <paramref name="index"/>-th field of its label.</summary>
    public FieldRowViewModel(LabelField field, int index)
    {
        Field = field;
        Index = index;
    }

    /// <summary>The field as recorded by the renderer.</summary>
    public LabelField Field { get; }

    /// <summary>Position of the field in the label's field list (drawing order).</summary>
    public int Index { get; }

    /// <summary>What kind of field it is, in the window language ("Text", "Barcode", "Box", ...).</summary>
    public string KindText => UiText.Get("Ui_FieldKind" + Field.Kind);

    /// <summary>The renderer's short description, for example <c>Text "Maria Gonzalez"</c>.</summary>
    public string Summary => Field.Summary;

    /// <summary>Where the field is and how big: "40, 200 · 320 × 56".</summary>
    public string PositionText => UiText.Get("Ui_FieldPosition", Field.X, Field.Y, Field.Width, Field.Height);

    /// <summary>How the field was drawn, for example "font 0, 56 dots".</summary>
    public string Detail => Field.Detail;

    /// <summary>Second line of the row: "Text · 40, 200 · 320 × 56 · font 0, 56 dots".</summary>
    public string Facts => Field.Detail.Length == 0
        ? KindText + " · " + PositionText
        : KindText + " · " + PositionText + " · " + Field.Detail;

    /// <summary>"line 7": the ZPL line the field comes from.</summary>
    public string LineText => UiText.Get("Ui_FieldLine", Field.Line);

    /// <summary>Why the field is not completely on the label, or null when it is.</summary>
    public string? Problem => Field.Problem;

    /// <summary>True when the field has a problem; the row is then shown in red.</summary>
    public bool HasProblem => Field.Problem is not null;

    /// <summary>
    /// The footer under the tabs while this field is selected, as in the mockup:
    /// <c>Selected: text field "Maria Gonzalez" · x 40, y 200 · font 0, 56 dots · line 7</c>.
    /// </summary>
    public string FooterText => UiText.Get("Ui_SelectedField", Noun(), Field.X, Field.Y, Field.Detail, Field.Line);

    /// <summary>
    /// The field named in running text: a text field by its words ("text field “Maria Gonzalez”"), anything else by
    /// the renderer's summary with a lower-case first letter ("barcode Code 128").
    /// </summary>
    private string Noun()
    {
        if (Field.Kind == FieldKind.Text && Field.Data.Length > 0)
            return UiText.Get("Ui_SelectedTextField", Shorten(Field.Data, 40));
        var s = Field.Summary;
        return s.Length == 0 ? s : char.ToLower(s[0], Text.Culture) + s[1..];
    }

    /// <summary>One line, at most <paramref name="max"/> characters with an ellipsis.</summary>
    internal static string Shorten(string text, int max)
    {
        var line = text.ReplaceLineEndings(" ");
        return line.Length <= max ? line : line[..(max - 1)].TrimEnd() + "…";
    }
}
