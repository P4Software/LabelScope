using System.ComponentModel;
using System.Globalization;
using LabelScope.App.Localization;
using LabelScope.Core;
using LabelScope.Core.Jobs;
using LabelScope.Core.Localization;
using LabelScope.Core.Rendering;

namespace LabelScope.App.ViewModels;

/// <summary>What the job shows when it drew no label at all; decided from facts, never from a translated title.</summary>
public enum PlaceholderKind
{
    /// <summary>The job drew at least one label.</summary>
    None,

    /// <summary>The data holds no label start (^XA) and stored nothing: no label was found.</summary>
    NoLabel,

    /// <summary>The data has a label start but no picture could be drawn from it.</summary>
    NotDrawn,

    /// <summary>The data only stored or deleted objects in printer memory (a ~DG download): a success, not a mistake.</summary>
    StoredOnly,
}

/// <summary>One problem of a job for the card, the status bar and the Log tab: a warning, or a field that is not
/// completely on the label.</summary>
/// <param name="Line">1-based ZPL line, or null when the problem is about the whole job.</param>
/// <param name="Message">Plain-language text in the language of the last render.</param>
public sealed record JobProblem(int? Line, string Message);

/// <summary>One row of the Log tab.</summary>
/// <param name="TimeText">When it happened, "14:31:40".</param>
/// <param name="Message">What happened.</param>
/// <param name="Line">The ZPL line it is about, or null.</param>
/// <param name="LineText">"Line 7" for the link, or "" when there is no line.</param>
/// <param name="IsProblem">True for a warning or a field problem; shown in red.</param>
public sealed record LogRow(string TimeText, string Message, int? Line, string LineText, bool IsProblem)
{
    /// <summary>True when the row names a ZPL line that can be jumped to.</summary>
    public bool HasLine => Line is not null;
}

/// <summary>
/// One card of the Print jobs list: everything that came in with one send (one TCP connection, one opened file or
/// one paste), possibly several labels. Every text is built when it is read, in the current language, so a language
/// switch only has to ask the card to read its texts again (<see cref="Refresh"/>).
/// </summary>
public sealed class JobViewModel : INotifyPropertyChanged
{
    // Built from one Result and culture; rebuilt when either changes (re-render, language switch).
    private sealed record Derived(RenderResult Result, CultureInfo Culture, IReadOnlyList<LabelPageViewModel> Pages,
                                  IReadOnlyList<JobProblem> Problems, long Bytes);

    private Derived? _derived;

    /// <summary>Wraps <paramref name="job"/>.</summary>
    /// <param name="job">The job.</param>
    /// <param name="connectionId">The connection it arrived over (0 for a file, paste or a job kept from an earlier run);
    /// later labels of the same connection are added to this job.</param>
    /// <param name="part">1 for the first job of a send; 2 and up when one send held more labels than one job takes.</param>
    public JobViewModel(LabelJob job, long connectionId = 0, int part = 1)
    {
        Job = job;
        ConnectionId = connectionId;
        Part = Math.Max(1, part);
    }

    /// <summary>Which part of its send this job is: 1, or 2 and up for a continuation of a very large send.</summary>
    public int Part { get; }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The job. Replaced when another label of the same send arrives (see <see cref="Replace"/>).</summary>
    public LabelJob Job { get; private set; }

    /// <summary>The connection the job arrived over; 0 when it did not come over the network in this session.</summary>
    public long ConnectionId { get; }

    /// <summary>Takes a newer version of the same job (one more label arrived on its connection) and redraws the card.</summary>
    public void Replace(LabelJob job)
    {
        Job = job;
        Refresh();
    }

    /// <summary>Makes the card read every text again: after a language switch, a re-render or a host name lookup.</summary>
    public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));

    /// <summary>
    /// Bold first line of the card: the job name ("Shipping label", the file name, or "Label"); a continuation of a very
    /// large send says which part it is ("Pallet label (part 2)"), so one print making several cards is understood.
    /// </summary>
    public string Title => Part == 1 ? Job.DisplayName : UiText.Get("Ui_JobPart", Job.DisplayName, Part);

    /// <summary>Arrival time, "14:31:40".</summary>
    public string TimeText => Job.ReceivedAt.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>Second line: "WMS · 1 label · 12 lines", plus a note when the send stopped early.</summary>
    public string Subtitle
    {
        get
        {
            var labels = Job.LabelCount;
            var text = UiText.Get("Ui_JobSubtitle", Job.DisplaySource,
                UiText.Get(labels == 1 ? "Ui_JobLabelsOne" : "Ui_JobLabelsMany", labels),
                UiText.Get(Job.LineCount == 1 ? "Ui_JobLinesOne" : "Ui_JobLinesMany", Job.LineCount));
            return Job.Complete ? text : text + " · " + UiText.Get("Ui_Incomplete");
        }
    }

    /// <summary>What the job shows when it drew no label; <see cref="PlaceholderKind.None"/> for a normal job.</summary>
    public PlaceholderKind Placeholder
    {
        get
        {
            var result = Job.Result;
            if (result.Labels.Count > 0) return PlaceholderKind.None;
            // Without any ^XA the data holds no label; a download (~DG, ~DY) is then a "stored in memory" job, which
            // is a success. With a ^XA the label exists but could not be drawn.
            var hasStart = Job.Zpl.Contains("^XA", StringComparison.OrdinalIgnoreCase);
            if (hasStart) return PlaceholderKind.NotDrawn;
            return result.MemoryNotes.Count > 0 ? PlaceholderKind.StoredOnly : PlaceholderKind.NoLabel;
        }
    }

    /// <summary>Problems of the job: every warning, then every field that is off the label without a warning on its line.</summary>
    public IReadOnlyList<JobProblem> Problems => Current().Problems;

    /// <summary>Number of problems; the card says "Rendered OK" when 0.</summary>
    public int ErrorCount => Problems.Count;

    /// <summary>True when the card's third line is red: problems, or a job that drew nothing it should have.</summary>
    public bool IsError => ErrorCount > 0 || Placeholder is PlaceholderKind.NoLabel or PlaceholderKind.NotDrawn;

    /// <summary>Third line: "Rendered OK", "1 error — first problem", or what a job without labels did.</summary>
    public string StatusText
    {
        get
        {
            var problems = Problems;
            if (problems.Count > 0)
            {
                var first = FieldRowViewModel.Shorten(problems[0].Message, 60);
                return problems.Count == 1 ? UiText.Get("Ui_ErrorOne", first) : UiText.Get("Ui_ErrorMany", problems.Count, first);
            }
            return Placeholder switch
            {
                PlaceholderKind.StoredOnly => PlaceholderLabel.StoredTitle,
                PlaceholderKind.NotDrawn => PlaceholderLabel.NotDrawnTitle,
                PlaceholderKind.NoLabel => PlaceholderLabel.NotFoundTitle,
                _ => UiText.Get("Ui_RenderedOk"),
            };
        }
    }

    /// <summary>The pages of the job: one per label, or the placeholder picture when it drew none.</summary>
    public IReadOnlyList<LabelPageViewModel> Pages => Current().Pages;

    /// <summary>
    /// The Log tab: Core's log of the job (received, memory notes, warnings), then each field problem that has no
    /// warning of its own, so every red outline on the label is explained there.
    /// </summary>
    public IReadOnlyList<LogRow> LogRows
    {
        get
        {
            var rows = new List<LogRow>();
            var warnings = Job.Result.Warnings.Count;
            foreach (var entry in Job.Log)
                rows.Add(Row(entry.At, entry.Message, entry.Line, entry.Line is not null));
            foreach (var p in Problems.Skip(warnings))
                rows.Add(Row(Job.ReceivedAt, p.Message, p.Line, true));
            return rows;
        }
    }

    private static LogRow Row(DateTimeOffset at, string message, int? line, bool problem) =>
        new(at.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture), message, line,
            line is { } l ? UiText.Get("Ui_LogLine", l) : "", problem);

    /// <summary>
    /// Approximate memory the job keeps, to bound the whole list: the PNGs, the recorded fields (their texts, roughly)
    /// and the ZPL. A .NET string uses two bytes per character.
    /// </summary>
    public long ApproximateBytes => Current().Bytes;

    /// <summary>Builds (once per result and language) the pages, the problem list and the size estimate.</summary>
    private Derived Current()
    {
        var result = Job.Result;
        var culture = Text.Culture;
        var d = _derived;
        if (d is not null && ReferenceEquals(d.Result, result) && Equals(d.Culture, culture)) return d;

        List<LabelPageViewModel> pages;
        if (result.Labels.Count > 0)
        {
            pages = result.Labels.Select((l, i) => new LabelPageViewModel(l, i, false)).ToList();
        }
        else
        {
            // Drawn here, in the current language, so the picture follows a language switch like every other text.
            var text = Placeholder switch
            {
                PlaceholderKind.StoredOnly => PlaceholderLabel.StoredText,
                PlaceholderKind.NotDrawn => PlaceholderLabel.NotDrawnText,
                _ => PlaceholderLabel.Text,
            };
            pages = [new LabelPageViewModel(PlaceholderLabel.Create(text), 0, true)];
        }

        var problems = result.Warnings.Select(w => new JobProblem(w.Line, w.Message)).ToList();
        var warnedLines = result.Warnings.Select(w => w.Line).ToHashSet();
        long bytes = HistoryBudget.SizeOf(0, Job.Zpl);
        foreach (var label in result.Labels)
        {
            bytes += label.PngBytes.Length;
            foreach (var f in label.Fields)
            {
                // About 64 bytes of record and boxes plus the strings it holds.
                bytes += 64 + 2L * (f.Summary.Length + f.Data.Length + f.Detail.Length + (f.Problem?.Length ?? 0));
                if (f.Problem is not null && !warnedLines.Contains(f.Line)) problems.Add(new JobProblem(f.Line, f.Problem));
            }
        }

        d = new Derived(result, culture, pages, problems, bytes);
        _derived = d;
        return d;
    }
}
