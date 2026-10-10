using LabelScope.Core.Fonts;

namespace LabelScope.Core.Rendering;

/// <summary>Font choice for text fields: ^A, ^CF and the fallback for a font letter the printer does not have.</summary>
internal sealed partial class LabelPainter
{
    // Font letter named by ^A for the field being built (null = the ^CF font). Reset by ^FS.
    private char? _fieldFont;

    // Line of the ^FD being drawn, so font warnings point at the text they are about.
    private int _dataLine = 1;

    // Letters already warned about in this label: one "unknown font" warning per letter is enough. A painter lives
    // for one label only, so the set starts empty for every label.
    private readonly HashSet<char> _warnedFonts = [];

    /// <summary>
    /// The font for the field being drawn. When ^A gives any size, a missing height or width follows the font (0);
    /// when ^A gives none, ^CF's height and width apply, as the guide describes for both commands.
    /// </summary>
    /// <remarks>
    /// A new font object is built for every field and disposed after it. SKFont is not thread-safe and two labels
    /// can render at once, so no font object is ever shared between renders; only the read-only bundled typefaces are.
    /// </remarks>
    private ZplFont MakeFont(int line)
    {
        var letter = char.ToUpperInvariant(_fieldFont ?? _defaultFont);
        int height, width;
        if (_fontHeight is not null || _fontWidth is not null)
        {
            height = _fontHeight ?? 0;
            width = _fontWidth ?? 0;
        }
        else
        {
            height = _defaultHeight;
            width = _defaultWidth;
        }
        return ResolveFont(letter, height, width, line);
    }

    /// <summary>The built-in font <paramref name="letter"/>, or font A with one warning for a letter the printer does not have.</summary>
    private ZplFont ResolveFont(char letter, int height, int width, int line)
    {
        var font = ZplFontFactory.TryBuiltIn(letter, height, width, _dpi, out var note);
        if (font is null)
        {
            if (_warnedFonts.Add(letter))
                _warnings.Add(new(line, $"Font {letter} is not one of the fonts built into Zebra printers (0, A to H, P to V) and was not assigned with ^CW, so LabelScope used font A. The ZPL guide says a printer falls back to font A for an unknown font."));
            font = ZplFontFactory.TryBuiltIn('A', height, width, _dpi, out note)!;
        }
        // The font model says what it limited; the painter adds what the user can do about it.
        if (note is not null)
            _warnings.Add(new(line, $"{note} Use a smaller height or width in ^A or ^CF to print this text as the label intends."));
        return font;
    }
}
