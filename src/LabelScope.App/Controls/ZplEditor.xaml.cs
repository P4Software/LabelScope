using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Xml;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using ICSharpCode.AvalonEdit.Rendering;

namespace LabelScope.App.Controls;

/// <summary>
/// Read-only ZPL viewer for the right-hand ZPL tab: ZPL colouring, line numbers, and one highlighted line that
/// marks the ZPL line of the field the user selected on the label or in the Fields tab.
/// </summary>
/// <remarks>
/// Wraps AvalonEdit's <see cref="ICSharpCode.AvalonEdit.TextEditor"/>. AvalonEdit's own line numbers are replaced
/// by <see cref="LineNumberGutter"/> so the orange highlight can run through the gutter as well, as in the mockup.
/// </remarks>
public partial class ZplEditor
{
    /// <summary>Name of the embedded colouring definition (LogicalName set in LabelScope.App.csproj).</summary>
    private const string HighlightingResourceName = "LabelScope.App.Resources.Zpl.xshd";

    /// <summary>Highlight colour of the selected field's line (the design's highlight orange #F9B54C).</summary>
    private static readonly Brush LineHighlightBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xF9, 0xB5, 0x4C)));

    /// <summary>
    /// The ZPL colouring, parsed once per process: every editor instance shares the same immutable definition.
    /// Lazy so a broken resource surfaces when the first editor is built, not in a static constructor (where it
    /// would become an opaque TypeInitializationException).
    /// </summary>
    private static readonly Lazy<IHighlightingDefinition> ZplHighlighting = new(LoadHighlighting);

    /// <summary>Identifies the <see cref="Text"/> dependency property, so the ZPL can be data-bound.</summary>
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(ZplEditor),
        new FrameworkPropertyMetadata(string.Empty, OnTextChanged));

    private readonly LineNumberGutter _gutter;
    private readonly HighlightedLineRenderer _lineRenderer;
    private int? _highlightedLine;

    /// <summary>Raised when the user clicks a line (in the code or in the line numbers); 1-based line number.</summary>
    public event Action<int>? LineClicked;

    /// <summary>Builds the editor with ZPL colouring, the line-number gutter and the line highlight.</summary>
    public ZplEditor()
    {
        InitializeComponent();

        Editor.SyntaxHighlighting = ZplHighlighting.Value;
        // Read-only view: a blinking caret would suggest the text can be edited. Selecting and copying still work.
        Editor.TextArea.Caret.CaretBrush = Brushes.Transparent;

        _gutter = new LineNumberGutter(() => _highlightedLine, LineHighlightBrush);
        Editor.TextArea.LeftMargins.Insert(0, _gutter);

        _lineRenderer = new HighlightedLineRenderer(() => _highlightedLine, LineHighlightBrush);
        Editor.TextArea.TextView.BackgroundRenderers.Add(_lineRenderer);

        // Preview event: AvalonEdit marks the normal mouse-down as handled (it starts a selection), and the
        // TextArea also contains the gutter, so this one handler covers clicks on code and on line numbers.
        Editor.TextArea.PreviewMouseLeftButtonDown += OnTextAreaMouseDown;
    }

    /// <summary>
    /// The ZPL shown. Setting it replaces the whole text, scrolls back to the top and clears the highlighted line,
    /// because a line number from the previous job means nothing in the new one.
    /// </summary>
    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>
    /// Paints the whole of <paramref name="line"/> (1-based) in the highlight orange and scrolls it into view.
    /// <c>null</c>, or a number outside the text, removes the highlight.
    /// </summary>
    public void HighlightLine(int? line)
    {
        var document = Editor.Document;
        _highlightedLine = line is int n && n >= 1 && n <= document.LineCount ? n : null;

        Editor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
        _gutter.InvalidateVisual();

        if (_highlightedLine is not int target)
            return;

        if (IsLoaded)
        {
            Editor.ScrollToLine(target);
        }
        else
        {
            // Before the first layout AvalonEdit has no viewport to scroll; do it once the control is shown.
            void ScrollWhenLoaded(object sender, RoutedEventArgs e)
            {
                Loaded -= ScrollWhenLoaded;
                if (_highlightedLine is int pending)
                    Editor.ScrollToLine(pending);
            }
            Loaded += ScrollWhenLoaded;
        }
    }

    /// <summary>Copies a new <see cref="Text"/> value into the AvalonEdit document.</summary>
    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var editor = (ZplEditor)d;
        editor._highlightedLine = null;
        editor.Editor.Text = e.NewValue as string ?? string.Empty;
        editor.Editor.ScrollToHome();
        editor._gutter.InvalidateMeasure();
    }

    /// <summary>Turns a click anywhere in the text area into the 1-based line it landed on.</summary>
    private void OnTextAreaMouseDown(object sender, MouseButtonEventArgs e)
    {
        var textView = Editor.TextArea.TextView;
        if (!textView.VisualLinesValid)
            return;

        // Gutter and code share the TextView's vertical coordinates, so one lookup serves both.
        var y = e.GetPosition(textView).Y + textView.VerticalOffset;
        var visualLine = textView.GetVisualLineFromVisualTop(y);
        if (visualLine is not null)
            LineClicked?.Invoke(visualLine.FirstDocumentLine.LineNumber);
    }

    /// <summary>Reads the embedded Zpl.xshd. A missing resource is a build error, so it fails loudly.</summary>
    private static IHighlightingDefinition LoadHighlighting()
    {
        using var stream = typeof(ZplEditor).Assembly.GetManifestResourceStream(HighlightingResourceName)
            ?? throw new InvalidOperationException(
                $"The ZPL colouring definition '{HighlightingResourceName}' is missing from the program. "
                + "Reinstall LabelScope.");
        using var reader = XmlReader.Create(stream);
        return HighlightingLoader.Load(reader, HighlightingManager.Instance);
    }

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    /// <summary>Paints the highlighted line's background across the full width of the code area.</summary>
    private sealed class HighlightedLineRenderer(Func<int?> highlightedLine, Brush brush) : IBackgroundRenderer
    {
        /// <inheritdoc />
        public KnownLayer Layer => KnownLayer.Background;

        /// <inheritdoc />
        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            if (highlightedLine() is not int line || !textView.VisualLinesValid)
                return;

            // Null when the line is scrolled out of view: nothing to paint.
            var visualLine = textView.GetVisualLine(line);
            if (visualLine is null)
                return;

            var top = visualLine.VisualTop - textView.VerticalOffset;
            // Wider than the view so the band has no right edge when scrolled horizontally; drawing is clipped.
            var width = Math.Max(textView.ActualWidth, ((System.Windows.Controls.Primitives.IScrollInfo)textView).ExtentWidth + textView.ActualWidth);
            drawingContext.DrawRectangle(brush, null, new Rect(0, top, width, visualLine.Height));
        }
    }

    /// <summary>
    /// Line numbers in the muted colour, right-aligned with room on both sides, and the highlighted line's
    /// background painted through the gutter so the orange band spans the whole row as in the mockup.
    /// </summary>
    private sealed class LineNumberGutter : AbstractMargin
    {
        private const double LeftPadding = 16;
        private const double RightPadding = 14;

        private static readonly Brush NumberBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x5B, 0x66, 0x75)));
        private static readonly Brush HighlightedNumberBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x1B, 0x24, 0x30)));

        private readonly Func<int?> _highlightedLine;
        private readonly Brush _highlightBrush;

        /// <summary>Creates the gutter; the highlighted line is read on every paint so it never goes stale.</summary>
        public LineNumberGutter(Func<int?> highlightedLine, Brush highlightBrush)
        {
            _highlightedLine = highlightedLine;
            _highlightBrush = highlightBrush;
        }

        /// <summary>Repaint whenever the editor lays out or scrolls its lines.</summary>
        protected override void OnTextViewChanged(TextView? oldTextView, TextView? newTextView)
        {
            if (oldTextView is not null)
                oldTextView.VisualLinesChanged -= OnVisualLinesChanged;
            base.OnTextViewChanged(oldTextView, newTextView);
            if (newTextView is not null)
                newTextView.VisualLinesChanged += OnVisualLinesChanged;
            InvalidateMeasure();
        }

        /// <summary>A new document can need a wider gutter (e.g. 99 to 100 lines).</summary>
        protected override void OnDocumentChanged(ICSharpCode.AvalonEdit.Document.TextDocument? oldDocument,
            ICSharpCode.AvalonEdit.Document.TextDocument? newDocument)
        {
            base.OnDocumentChanged(oldDocument, newDocument);
            InvalidateMeasure();
        }

        private void OnVisualLinesChanged(object? sender, EventArgs e) => InvalidateVisual();

        /// <summary>Wide enough for the largest line number, at least two digits so short jobs do not jitter.</summary>
        protected override Size MeasureOverride(Size availableSize)
        {
            var lineCount = Document?.LineCount ?? 1;
            var digits = Math.Max(2, lineCount.ToString(CultureInfo.InvariantCulture).Length);
            var sample = MakeText(new string('9', digits), NumberBrush);
            return new Size(LeftPadding + sample.WidthIncludingTrailingWhitespace + RightPadding, 0);
        }

        /// <summary>Draws the highlight band (if visible) and the number of every visible line.</summary>
        protected override void OnRender(DrawingContext drawingContext)
        {
            var textView = TextView;
            if (textView is null || !textView.VisualLinesValid)
                return;

            // Hit-testable background, so clicks between the numbers still reach the click handler.
            drawingContext.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));

            var highlighted = _highlightedLine();
            foreach (var visualLine in textView.VisualLines)
            {
                var lineNumber = visualLine.FirstDocumentLine.LineNumber;
                var isHighlighted = lineNumber == highlighted;
                if (isHighlighted)
                {
                    var top = visualLine.VisualTop - textView.VerticalOffset;
                    drawingContext.DrawRectangle(_highlightBrush, null,
                        new Rect(0, top, RenderSize.Width, visualLine.Height));
                }

                var text = MakeText(lineNumber.ToString(CultureInfo.InvariantCulture),
                    isHighlighted ? HighlightedNumberBrush : NumberBrush);
                var y = visualLine.GetTextLineVisualYPosition(visualLine.TextLines[0], VisualYPosition.TextTop)
                        - textView.VerticalOffset;
                drawingContext.DrawText(text, new Point(RenderSize.Width - RightPadding - text.Width, y));
            }
        }

        /// <summary>Formats a number in the editor's own font so numbers line up with the code rows.</summary>
        private FormattedText MakeText(string text, Brush brush)
        {
            var textView = TextView;
            var typeface = new Typeface(
                (FontFamily)(textView?.GetValue(TextBlock.FontFamilyProperty) ?? new FontFamily("Consolas")),
                FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            var size = (double)(textView?.GetValue(TextBlock.FontSizeProperty) ?? 13.0);
            return new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, size,
                brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        }
    }
}
