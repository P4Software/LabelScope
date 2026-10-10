using LabelScope.Core.Fonts;
using LabelScope.Core.Memory;
using SkiaSharp;

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

    // Size notes (for example "enlarged up to 10 times") already given in this label. Several oversized fields
    // usually share one cause, so the same note is shown once per label, like the unknown-font warning. Kept apart
    // from _warnedFonts so that set stays keyed by font letter.
    private readonly HashSet<string> _warnedFontNotes = [];

    // ^A@: the font file named last in this label (the guide: it stays active for later ^A@ without a name). The
    // painter lives for one label, so the name resets per label, as a printer forgets it when the format ends.
    private ObjectName? _lastFontFile;

    // Font files already warned about in this label, by display name: ZebraDesigner repeats one resident font name
    // on every field, and one warning per file says it all; a warning per field would bury every other message.
    private readonly HashSet<string> _warnedFiles = [];

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

    /// <summary>
    /// The font for <paramref name="letter"/>: a font file named by ^A@ or given to the letter by ^CW (printer memory
    /// first, then the FontsFolder), otherwise the built-in font, otherwise font A with one warning per letter.
    /// </summary>
    private ZplFont ResolveFont(char letter, int height, int width, int line)
    {
        var file = letter == '@' ? _lastFontFile : _context.Memory.FontFor(letter);
        if (file is { } f)
        {
            var (face, problem) = FindTypeface(f);
            // Sizes for a TrueType font follow the scalable rules: height is the size, width stretches it.
            if (face is not null)
            {
                var scalable = ZplFontFactory.Scalable(face, height, width, out var sizeNote);
                AddSizeNote(sizeNote, line);
                return scalable;
            }

            // A TrueType or OpenType name LabelScope cannot supply (usually a font resident in the printer, as
            // ZebraDesigner names them) is drawn with the bundled scalable font, the closest stand-in, so the label still shows.
            if (problem != FontProblem.None || f.Extension is "TTF" or "OTF" or "TTE")
            {
                WarnFileOnce(f, line, problem switch
                {
                    FontProblem.Unreadable =>
                        $"The font {f.Display} could not be read as a TrueType or OpenType font, so LabelScope drew the text with its built-in scalable font. " +
                        "Replace it with a working font file (in the FontsFolder, then restart LabelScope, or by sending the download again), then send the label again.",
                    FontProblem.Unopenable =>
                        $"The font file for {f.Display} in the FontsFolder could not be opened (another program may be using it, or it was moved), so LabelScope drew the text with its built-in scalable font. " +
                        "Check that the file is in the FontsFolder and not in use, then send the label again.",
                    FontProblem.OverBudget =>
                        $"The font {f.Display} was not loaded because LabelScope already holds {PrinterMemory.FormatBytes(_context.Fonts.LoadedBytesLimit)} of fonts from the FontsFolder, " +
                        "so LabelScope drew the text with its built-in scalable font. Use fewer large fonts in your labels; restarting LabelScope frees the fonts it holds.",
                    _ => f.Extension == "TTE"
                        ? $"The font {f.Display} is a TrueType extension file (.TTE), which LabelScope cannot read, so LabelScope drew the text with its built-in scalable font. " +
                          "Use the .TTF font file instead."
                        : $"The font {f.Display} is not in LabelScope's printer memory or in the FontsFolder, so LabelScope drew the text with its built-in scalable font. " +
                          "Labels made in ZebraDesigner often name fonts that are stored inside the printer, which LabelScope cannot have. " +
                          $"To use the real font, copy {f.Name}.{f.Extension} into the folder named by FontsFolder in settings.json and restart LabelScope, " +
                          "or send the job that downloads it, then send the label again.",
                });
                var standIn = ZplFontFactory.Scalable(BundledFonts.Scalable, height, width, out var standInNote);
                AddSizeNote(standInNote, line);
                return standIn;
            }

            // Other files (.FNT bitmap fonts): ^A@ falls back to the ^CF font, as the guide says ("if invalid or
            // missing, the ^CF font"); a ^CW letter to its own built-in font, or to font A. Built-in fonts only, so a
            // ^CF letter that ^CW points at another missing file cannot send the lookup round in a circle.
            var stand = letter == '@' ? char.ToUpperInvariant(_defaultFont) : letter;
            var font = ZplFontFactory.TryBuiltIn(stand, height, width, _dpi, out var standNote);
            var why = letter == '@' ? "the ^CF font, as the ZPL guide says for a missing font" : $"the built-in font {stand}";
            if (font is null)
            {
                stand = 'A';
                why = "font A, the printer's fallback for an unknown font";
                font = ZplFontFactory.TryBuiltIn('A', height, width, _dpi, out standNote)!;
            }
            // One warning explains the stand-in; the letter's own "unknown font" warning would only repeat it.
            WarnFileOnce(f, line,
                $"The font {f.Display} is not in LabelScope's printer memory or in the FontsFolder, and LabelScope reads only TrueType and OpenType fonts (.TTF, .OTF), " +
                $"so it used font {stand} instead ({why}). Name a .TTF font or a built-in font letter to choose the font yourself.");
            AddSizeNote(standNote, line);
            return font;
        }

        // ^A@ before any font name was given in this label: the ^CF font, quietly, as the guide describes.
        if (letter == '@') letter = _defaultFont == '@' ? 'A' : char.ToUpperInvariant(_defaultFont);

        var builtIn = ZplFontFactory.TryBuiltIn(letter, height, width, _dpi, out var note);
        if (builtIn is null)
        {
            if (_warnedFonts.Add(letter))
                _warnings.Add(new(line, $"Font {letter} is not one of the fonts built into Zebra printers (0, A to H, P to V) and was not assigned with ^CW, so LabelScope used font A. The ZPL guide says a printer falls back to font A for an unknown font."));
            builtIn = ZplFontFactory.TryBuiltIn('A', height, width, _dpi, out note)!;
        }
        AddSizeNote(note, line);
        return builtIn;
    }

    /// <summary>The font model says what it limited; the painter adds what the user can do about it, once per label.</summary>
    private void AddSizeNote(string? note, int line)
    {
        if (note is not null && _warnedFontNotes.Add(note))
            _warnings.Add(new(line, $"{note} Use a smaller height or width in ^A or ^CF to print this text as the label intends."));
    }

    /// <summary>
    /// A font file from printer memory (exact drive, as a printer looks it up) or else the FontsFolder (file name
    /// only; the drive means nothing on a PC). <c>Problem</c> says why no font came back; <see cref="FontProblem.None"/>
    /// then means the file is in neither place.
    /// </summary>
    private (SKTypeface? Face, FontProblem Problem) FindTypeface(ObjectName file)
    {
        var stored = _context.Memory.Find(file, out _);
        if (stored is StoredFont { Typeface: { } storedFace }) return (storedFace, FontProblem.None);

        var face = _context.Fonts.Find($"{file.Name}.{file.Extension}", out var problem);
        if (face is not null) return (face, FontProblem.None);
        // A download that Skia cannot read is the more useful thing to report when the folder has no such file.
        return (null, problem == FontProblem.None && stored is StoredFont ? FontProblem.Unreadable : problem);
    }

    /// <summary>Adds <paramref name="message"/> the first time <paramref name="file"/> fails in this label.</summary>
    private void WarnFileOnce(ObjectName file, int line, string message)
    {
        if (_warnedFiles.Add(file.Display)) _warnings.Add(new(line, message));
    }
}
