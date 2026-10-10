namespace LabelScope.Core.Rendering;

/// <summary>What kind of thing a field on a rendered label is.</summary>
public enum FieldKind
{
    /// <summary>Text from ^FD (with ^A, ^CF, ^FB), or the note that more fields exist than are listed.</summary>
    Text,

    /// <summary>A barcode or 2D symbol from a ^B command.</summary>
    Barcode,

    /// <summary>A box or bar from ^GB.</summary>
    Box,

    /// <summary>A circle from ^GC.</summary>
    Circle,

    /// <summary>A diagonal line from ^GD.</summary>
    Line,

    /// <summary>A bitmap sent in the label itself (^GF) or recalled and magnified from memory (^XG).</summary>
    Graphic,

    /// <summary>A stored image placed at its own size (^IM) or loaded as the label background (^IL).</summary>
    Image,
}

/// <summary>One field drawn on a rendered label, so the window can list it and find it again when the label is clicked.</summary>
/// <param name="Kind">What was drawn.</param>
/// <param name="Summary">Short plain-language name in the current language, for example <c>Text "Maria Gonzalez"</c> or <c>Barcode Code 128</c>.</param>
/// <param name="Data">The field data as drawn (after ^FH escapes), or the name of the stored graphic; empty for shapes and ^GF.</param>
/// <param name="X">Left edge in label dots, after rotation and ^PO.</param>
/// <param name="Y">Top edge in label dots, after rotation and ^PO.</param>
/// <param name="Width">Width in label dots, after rotation (a field turned with R or B swaps its sides).</param>
/// <param name="Height">Height in label dots, after rotation.</param>
/// <param name="Line">1-based ZPL line of the field's origin command (^FO or ^FT), or of the drawing command when the field has none.</param>
/// <param name="Detail">How the field was drawn in plain language, for example <c>font 0, 56 dots</c> or <c>14 x 216, thickness 14</c>.</param>
/// <param name="Problem">Why the field is not completely on the label (partly or fully off it, or not drawn at all); null when it was drawn completely.</param>
public sealed record LabelField(FieldKind Kind, string Summary, string Data, int X, int Y, int Width, int Height,
                                int Line, string Detail, string? Problem);
