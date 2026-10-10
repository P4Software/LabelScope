using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using LabelScope.App.Localization;
using LabelScope.App.ViewModels;
using LabelScope.Core;
using LabelScope.Core.Fonts;
using LabelScope.Core.Jobs;
using LabelScope.Core.Listening;
using LabelScope.Core.Localization;
using LabelScope.Core.Memory;
using LabelScope.Core.Printing;
using LabelScope.Core.Rendering;
using LabelScope.Core.Settings;
using LabelScope.Core.Updating;
using Microsoft.Win32;
using Serilog;
using Media = System.Windows.Media;

namespace LabelScope.App;

/// <summary>The single window: print jobs on the left, the label in the centre, its ZPL, fields and log on the right.</summary>
public partial class MainWindow : Window
{
    private readonly string _settingsPath = System.IO.Path.Combine(AppContext.BaseDirectory, "settings.json");

    // One card per job (one send, one opened file, one paste), newest first.
    private readonly ObservableCollection<JobViewModel> _jobs = new();

    // Puts the labels of one TCP connection back together into one job.
    private readonly JobAssembler _assembler = new();

    // One printer memory for the whole session: a graphic downloaded in one job is used by labels in later jobs,
    // as on a real printer. Nothing is saved when LabelScope closes.
    private readonly PrinterMemory _memory = new();

    // Rebuilt once in OnLoaded with the fonts from the FontsFolder setting, before the listener starts.
    private ZplRenderer _renderer;
    private FontLibrary _fonts = FontLibrary.Empty;

    // Kept in a field so OnClosing can unsubscribe it; _memoryUpdateScheduled coalesces a burst of Changed events
    // (a download with hundreds of graphics) into one UI update.
    private EventHandler? _memoryChanged;
    private int _memoryUpdateScheduled;
    private AppSettings _settings = new();
    private ZplListener? _listener;
    private PrinterInstaller? _installer;

    // The job list kept between runs ("Keep jobs"). One store for the whole session: after Load has seen a file from a
    // newer LabelScope, the same instance refuses to overwrite it.
    private readonly JobStore _store = new(JobStore.DefaultPath);
    private KeptJobsState _keptJobs = KeptJobsState.NotRead;

    /// <summary>Where the session stands with jobs.json; saving is only allowed in <see cref="KeptJobsState.Ready"/>.</summary>
    private enum KeptJobsState
    {
        /// <summary>The file has not been read this session (Keep jobs was off at start).</summary>
        NotRead,

        /// <summary>
        /// The file is being read and its jobs drawn and added. A save now would write a list without them, so none
        /// happens: not from the timer, not on close (the file then stays exactly as it was). Also the state for good
        /// when loading failed unexpectedly, so a broken load never overwrites the file.
        /// </summary>
        Loading,

        /// <summary>Read (and, at start, its jobs are in the list): saves may write the file.</summary>
        Ready,
    }
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private string? _lastSaveProblem;

    // What is on screen: the job, its page, the decoded picture of that page only, and the selected field.
    private JobViewModel? _shownJob;
    private LabelJob? _shownJobInstance;    // the job object whose ZPL is in the ZPL tab (replaced when a label is added)
    private string _shownZpl = "";          // the formatted ZPL in the ZPL tab
    private int _page;
    private LabelPageViewModel? _shownPage;
    private BitmapImage? _currentImage;
    private FieldRowViewModel? _selectedField;
    private bool _settingFieldList;         // true while code (not the user) selects a row of the Fields tab

    // Set when the window starts closing so a late label from the socket thread is ignored.
    private volatile bool _closing;

    // True while an install/remove runs, so a second click cannot start a second one.
    private bool _printerBusy;

    // Start-up notes (settings, log folder, printer settings) are kept so a later one never hides an earlier one.
    private readonly List<string> _startupNotes = new();
    private string _actionMessage = "";

    // Last time the printer status was checked; used to throttle the re-check on window activation.
    private DateTime _lastStatusCheck = DateTime.MinValue;

    // What the status line in the Print jobs card says. Kept as state (not text) so a language switch can rebuild it.
    private bool? _listening;              // null while starting
    private PrinterStatus? _printerStatus; // null until the first check
    private bool _printerSettingsBroken;   // the printer name or port in settings.json cannot be used

    // "N received today": a counter of arrivals, not of cards, so clearing the list or a trim does not lower it.
    private int _receivedToday;
    private readonly HashSet<long> _countedSends = new(); // connections already counted; cleared at midnight
    private DateTime _receivedTodayDate = DateTime.Today;

    // Zoom: "fit" follows the window size; otherwise _zoom is the screen size of one label dot (1 = 100 %).
    private bool _fit = true;
    private double _zoom = 1;
    private static readonly double[] ZoomSteps = [0.1, 0.25, 0.33, 0.5, 0.67, 0.75, 1, 1.5, 2, 3, 4];

    // Largest file "Open ZPL file" accepts: the same limit as one job sent over the network.
    private const long MaxOpenFileBytes = 16L * 1024 * 1024;

    // Problem outlines drawn on one page at most; a label with thousands of fields off the label would otherwise
    // create thousands of shapes on every zoom step. The Fields tab still lists every one.
    private const int MaxProblemOutlines = 500;

    // Back-pressure design. Rendering is the expensive step and runs on the socket threads, so at most two
    // labels are rendered at the same time (a socket thread simply waits its turn; TCP slows the sender down).
    // Finished results then wait in a queue for the UI thread. That queue is bounded: if senders are faster
    // than the window can show labels, the oldest waiting results are dropped from the display (they stay
    // in the log) and the user is told once, instead of the dispatcher queue growing without limit.
    private readonly SemaphoreSlim _renderGate = new(2);
    private readonly List<PendingResult> _pendingForUi = new(); // guarded by itself
    private const int MaxPendingForUi = 20;
    private int _drainScheduled; // 1 while a drain is already queued on the dispatcher

    // Increased by every "draw all jobs again"; a pass that sees a newer number stops and leaves the work to it.
    private int _renderGeneration;

    /// <summary>A job (new, or an existing one with one more label) waiting to be shown, plus the note to show with it.</summary>
    /// <param name="Part">1 for the first job of a send, 2 and up for its continuation jobs.</param>
    private sealed record PendingResult(LabelJob Job, long ConnectionId, string? Note, int Part = 1);

    /// <summary>Creates the window; real startup work happens in <see cref="OnLoaded"/>.</summary>
    public MainWindow()
    {
        InitializeComponent();
        _renderer = new ZplRenderer(_memory);
        // Changed is raised on whichever thread stored the object (a socket thread); the text belongs to the UI thread.
        // BeginInvoke never blocks the renderer, and a failure here must not reach the render thread.
        _memoryChanged = OnMemoryChanged;
        _memory.Changed += _memoryChanged;
        // The grid cell size and the outlines depend on how large the picture is shown (zoom and window size).
        LabelImage.SizeChanged += (_, _) =>
        {
            UpdateGrid();
            DrawOutlines();
        };
        HistoryList.ItemsSource = _jobs;
        ZplView.LineClicked += OnZplLineClicked;
        Activated += OnActivated;
        _saveTimer.Tick += OnSaveTimer;
        SelectLanguageInPicker();
        ApplyTexts();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var load = new SettingsStore().LoadOrCreate(_settingsPath);
            _settings = load.Settings;
            ConfigureLogging();
            // The language chosen in Printer setup wins over the Windows language; "" keeps the Windows rule.
            Text.Culture = CultureForSetting(_settings.Language);
            SelectLanguageInPicker();
            // Only the starting state comes from settings.json; later clicks are never written back, because
            // rewriting the file would destroy the comments the user may have added.
            GridBox.IsChecked = _settings.ShowGrid;
            StackedBox.IsChecked = _settings.StackedLayout;
            ApplyView();
            ApplyTexts(); // again: some texts name the printer from settings.json
            ShowJob();
            foreach (var message in load.Messages) Log.Information("Settings: {Message}", message);
            foreach (var message in load.Messages) AddStartupNote(message);

            // The font list is read once, here, and the same library and memory serve every job. The renderer is
            // replaced before the listener starts, so no job can ever see the old one.
            var fontMessages = new List<string>();
            _fonts = FontLibrary.FromFolder(_settings.FontsFolder, AppContext.BaseDirectory, fontMessages);
            foreach (var message in fontMessages) Log.Information("Fonts: {Message}", message);
            foreach (var message in fontMessages) AddStartupNote(message);
            if (_fonts.Count > 0) Log.Information("Fonts: {Count} font file(s) found in the FontsFolder", _fonts.Count);
            _renderer = new ZplRenderer(_memory, _fonts);
            UpdateMemoryText();
            UpdateSizePicker();

            StartListener();
            // Kept jobs come back while the listener already runs: they are older than anything that arrives now,
            // so they are added below the new ones.
            if (_settings.KeepJobs) await LoadKeptJobsAsync();
            CreateInstaller();
            await RefreshPrinterStatusAsync();
            if (_settings.CheckForUpdates) await CheckForUpdatesAsync(userAsked: false);
        }
        catch (Exception ex)
        {
            // async void: an exception here would otherwise reach the global handler without context.
            Log.Error(ex, "Startup failed");
            AddStartupNote(UiText.Get("Ui_StartupFailed"));
        }
    }

    private void ConfigureLogging()
    {
        var folder = ResolveLogFolder();
        try
        {
            Directory.CreateDirectory(folder);
            Log.Logger = new LoggerConfiguration()
                .WriteTo.File(System.IO.Path.Combine(folder, "labelscope-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14)
                .CreateLogger();
            Log.Information("LabelScope started");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // A log folder that cannot be created must not stop the program; it just runs without a log.
            AddStartupNote(UiText.Get("Ui_LogFolderFailed", folder));
        }
    }

    private string ResolveLogFolder() =>
        System.IO.Path.IsPathRooted(_settings.LogFolder) ? _settings.LogFolder : System.IO.Path.Combine(AppContext.BaseDirectory, _settings.LogFolder);

    private void StartListener()
    {
        try
        {
            _listener = new ZplListener(IPAddress.Parse(_settings.ListenAddress), _settings.ListenPort);
            _listener.LabelReceived += OnLabelReceived;
            _listener.ProblemReported += OnProblemReported;
            _listener.ConnectionClosed += OnConnectionClosed;
            _listener.Start();
            _listening = true;
            UpdateStatusLine();
            if (_settings.ListenAddress == "0.0.0.0") AddStartupNote(UiText.Get("Ui_ListenAllNote"));
        }
        catch (ListenerStartException ex)
        {
            _listening = false;
            UpdateStatusLine();
            Log.Warning(ex, "Listener could not start");
            // Not a crash: the window stays open so the message can be read. The message comes from Core, localized.
            MessageBox.Show(this, ex.Message, "LabelScope", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ---- receiving ----------------------------------------------------------------------------------

    /// <summary>The render options for <paramref name="settings"/>: density and the label loaded in the printer.</summary>
    private static RenderOptions OptionsFor(AppSettings settings) =>
        new(settings.DefaultDpi, settings.LabelWidthMm, settings.LabelHeightMm);

    /// <summary>Runs on a socket thread: one ^XA..^XZ label of a send arrived.</summary>
    private void OnLabelReceived(ReceivedLabel received) => ProcessIncoming(received, JobOrigin.Printed, null);

    /// <summary>
    /// Runs off the UI thread: draws a label (or a whole opened or pasted text), adds it to the job of its connection
    /// or makes a new job, and hands the job to the UI thread. Core swallows exceptions from listener subscribers
    /// without a trace, so everything is caught and logged here.
    /// </summary>
    /// <param name="received">What arrived.</param>
    /// <param name="origin">How it arrived.</param>
    /// <param name="name">A name of its own (the file name of an opened file), or null to name the job from its ZPL.</param>
    private void ProcessIncoming(ReceivedLabel received, JobOrigin origin, string? name)
    {
        try
        {
            if (_closing) return;
            // One read of the field: Printer setup may replace the settings object while this thread runs. How the bytes
            // were read goes with the label, so a ^CI28 label whose data was not UTF-8 says so.
            var options = OptionsFor(_settings) with { TextReadAsWindows1252 = received.ReadAsWindows1252 };
            // Format first and draw THE FORMATTED TEXT: warnings and fields carry line numbers, and they must point
            // at the lines the user sees. Formatting does not change the picture (ZPL ignores line breaks).
            // Formatting copies the whole text, so it waits for the same gate as drawing: a flood of very large
            // labels must not be able to hold many extra copies in memory at once.
            string formatted;
            RenderResult result;
            _renderGate.Wait();
            try
            {
                formatted = ZplFormatter.Format(received.Zpl);
                result = _renderer.Render(formatted, options);
            }
            finally { _renderGate.Release(); }
            Log.Information("Label received from {Source}: {Labels} image(s), {Warnings} warning(s), complete={Complete}, connection {Connection}",
                received.Source, result.Labels.Count, result.Warnings.Count, received.Complete, received.ConnectionId);

            var job = _assembler.TryAppend(received.ConnectionId, received.Zpl, formatted, result, received.Complete,
                received.ReadAsWindows1252);
            // An appended label belongs to the part being filled; carried along so a card that has to be made again
            // (its earlier versions were trimmed or skipped) still says which part it is.
            var part = job is null ? 1 : _assembler.CurrentPart(received.ConnectionId);
            if (job is null)
            {
                part = _assembler.NextPart(received.ConnectionId);
                // A continuation of a very large send keeps the name of the send's first job.
                if (name is null && part > 1) name = _assembler.CurrentJob(received.ConnectionId)?.Name;
                job = new LabelJob
                {
                    Name = name ?? "",
                    RemoteAddress = origin == JobOrigin.Printed ? received.Source : null,
                    Origin = origin,
                    ReceivedAt = received.ReceivedAt,
                    Zpl = received.Zpl,
                    Complete = received.Complete,
                    ReadAsWindows1252 = received.ReadAsWindows1252,
                    Result = result,
                };
                _assembler.Remember(received.ConnectionId, job, formatted, part);
                if (JobNamer.NeedsLookup(origin, job.RemoteAddress)) _ = ResolveHostAsync(job);
            }

            if (Dispatcher.HasShutdownStarted) return;
            QueueForUi(new PendingResult(job, received.ConnectionId, NoteFor(received, result), part));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not process a received label");
            ReportOnUi(UiText.Get("Ui_LabelFailed"));
        }
    }

    /// <summary>
    /// The message-strip note for one arrival, or null: what a text without a label did, a label cut off, or what a
    /// label job stored in printer memory. The card says the rest.
    /// </summary>
    private static string? NoteFor(ReceivedLabel received, RenderResult result)
    {
        if (result.Labels.Count == 0)
        {
            // Never drop a job silently; the card and the placeholder picture show what arrived.
            var hasStart = received.Zpl.Contains("^XA", StringComparison.OrdinalIgnoreCase);
            if (!hasStart && result.MemoryNotes.Count > 0) return StatusText.ForMemoryNotes(result.MemoryNotes);
            return hasStart ? UiText.Get("Ui_NotDrawnNote") : StatusText.ForNoLabel(result.Warnings.Count > 0);
        }
        if (!received.Complete)
            return UiText.Get("Ui_IncompleteNote") + (StatusText.ForMemoryNotes(result.MemoryNotes) is { } stored ? " " + stored : "");
        return StatusText.ForMemoryNotes(result.MemoryNotes);
    }

    /// <summary>Looks up the sender's host name off the UI thread, then redraws the card with it.</summary>
    private async Task ResolveHostAsync(LabelJob job)
    {
        try
        {
            await job.ResolveHostAsync().ConfigureAwait(false);
            if (_closing || Dispatcher.HasShutdownStarted) return;
            _ = Dispatcher.BeginInvoke(async () =>
            {
                try
                {
                    if (_jobs.FirstOrDefault(j => j.Job.Id == job.Id) is not { } vm) return;
                    // A later label of the same send may have replaced the job object before the answer came; the
                    // resolver keeps the answer, so asking the newer object again returns at once.
                    if (vm.Job.ResolvedHost is null) await vm.Job.ResolveHostAsync();
                    vm.Refresh();
                    if (vm == _shownJob) ShowPage();
                }
                catch (Exception ex) { Log.Warning(ex, "Could not show the sender's host name"); }
            });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Host name lookup failed");
        }
    }

    /// <summary>
    /// Runs on a socket thread: parks a finished result for the UI thread and drops the oldest waiting
    /// results when more than <see cref="MaxPendingForUi"/> are waiting.
    /// </summary>
    private void QueueForUi(PendingResult result)
    {
        var skipped = 0;
        lock (_pendingForUi)
        {
            // A later version of the same job (one more label of the same send) makes the waiting one pointless; it
            // takes its place (so jobs keep their arrival order) rather than counting as skipped. A note is kept.
            var index = _pendingForUi.FindIndex(p => p.Job.Id == result.Job.Id);
            if (index >= 0)
                _pendingForUi[index] = result with { Note = result.Note ?? _pendingForUi[index].Note, Part = _pendingForUi[index].Part };
            else _pendingForUi.Add(result);
            while (_pendingForUi.Count > MaxPendingForUi)
            {
                _pendingForUi.RemoveAt(0);
                skipped++;
            }
        }
        if (skipped > 0)
        {
            Log.Warning("Skipped showing {Skipped} job(s) because they arrived faster than they can be displayed", skipped);
            ReportOnUi(UiText.Get("Ui_SkippedLabels", skipped));
        }

        // Only one drain is queued at a time, however many results arrive meanwhile.
        if (Interlocked.Exchange(ref _drainScheduled, 1) == 0)
            Dispatcher.BeginInvoke(DrainPendingResults);
    }

    /// <summary>Runs on the UI thread: shows everything that is waiting.</summary>
    private void DrainPendingResults()
    {
        // Reset first: a result that arrives while we work schedules the next drain.
        Interlocked.Exchange(ref _drainScheduled, 0);
        List<PendingResult> waiting;
        lock (_pendingForUi)
        {
            waiting = [.. _pendingForUi];
            _pendingForUi.Clear();
        }
        foreach (var pending in waiting) AddOrUpdateJob(pending);
    }

    /// <summary>
    /// Runs on the UI thread: a new job gets a card at the top; a job that got one more label (same send) updates
    /// its card in place. A file the user opened or pasted is always shown, because they asked to see it; a printed
    /// job is shown when "Show the newest job as it arrives" is on (or nothing is shown yet).
    /// </summary>
    private void AddOrUpdateJob(PendingResult pending)
    {
        try
        {
            if (_closing) return;
            var existing = _jobs.FirstOrDefault(j => j.Job.Id == pending.Job.Id);
            if (existing is not null)
            {
                existing.Replace(pending.Job);
                // The job grew (one more label): the memory budget may now require dropping old cards.
                TrimHistory();
                if (existing == _shownJob) ShowJob(keepPage: true);
            }
            else
            {
                var vm = new JobViewModel(pending.Job, pending.ConnectionId, pending.Part);
                var follow = _settings.ShowNewestJob || pending.Job.Origin != JobOrigin.Printed || HistoryList.SelectedItem is null;
                // The ListBox keeps tracking the selected card through Insert(0), so "off" leaves the view alone.
                _jobs.Insert(0, vm);
                // One send is one arrival, whatever number of cards it makes (parts, or a card made again).
                if (pending.ConnectionId == 0 || _countedSends.Add(pending.ConnectionId)) CountArrival(pending.Job.ReceivedAt);
                TrimHistory();
                if (follow) HistoryList.SelectedItem = vm;
            }
            if (pending.Note is not null) ShowMessage(pending.Note);
            ScheduleSave();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not add a job to the list");
            ShowMessage(UiText.Get("Ui_LabelFailed"));
        }
    }

    /// <summary>Counts one arrival for "N received today"; the count starts again at midnight.</summary>
    private void CountArrival(DateTimeOffset at)
    {
        if (_receivedTodayDate != DateTime.Today)
        {
            _receivedTodayDate = DateTime.Today;
            _receivedToday = 0;
            _countedSends.Clear();
        }
        if (at.LocalDateTime.Date == DateTime.Today) _receivedToday++;
        UpdateReceivedToday();
    }

    /// <summary>
    /// Drops the oldest jobs until both limits hold: HistoryLimit jobs at most, and 256 MB of kept data at most
    /// (pictures, field records and ZPL; see <see cref="HistoryBudget"/>). The list is newest first.
    /// </summary>
    private void TrimHistory()
    {
        var keep = HistoryBudget.EntriesToKeep(_jobs.Select(j => j.ApproximateBytes).ToList(), _settings.HistoryLimit);
        while (_jobs.Count > keep)
        {
            var dropped = _jobs[^1];
            _jobs.RemoveAt(_jobs.Count - 1);
            // The assembler must not keep a dropped job's pictures alive outside this budget.
            if (dropped.ConnectionId != 0) _assembler.Forget(dropped.ConnectionId, dropped.Job.Id);
        }
    }

    /// <summary>
    /// Runs on a socket thread when a send has ended (after its last label was processed): its job is complete, so
    /// the assembler lets go of it; the card keeps it as long as the list does.
    /// </summary>
    private void OnConnectionClosed(long connectionId) => _assembler.Forget(connectionId);

    /// <summary>Runs on a socket thread when the listener had to drop a connection.</summary>
    private void OnProblemReported(string message)
    {
        try
        {
            Log.Warning("Listener problem: {Message}", message);
            ReportOnUi(message);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not report a listener problem");
        }
    }

    /// <summary>Shows a message-strip note from any thread, without ever blocking or touching a closing window.</summary>
    private void ReportOnUi(string message)
    {
        if (_closing || Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (!_closing) ShowMessage(message);
        });
    }

    // ---- draw every job again (language, label size, density) --------------------------------------------

    /// <summary>
    /// Draws every job in the list again from its ZPL with the current label size, density and language, so its
    /// picture, warnings, Log, field texts and card follow a change. Runs off the UI thread through the same render
    /// gate as arriving labels; the window stays usable and the cards change when the pass is done.
    /// </summary>
    /// <remarks>
    /// The jobs are drawn oldest first into a fresh printer memory, not the live one: replaying their downloads and
    /// deletes into the live memory would repeat them out of order (a replayed ^ID would delete a graphic that a later
    /// job downloaded). A label whose graphic came with a job that is no longer in the list then warns about it.
    /// </remarks>
    private async Task RerenderAllJobsAsync()
    {
        var generation = Interlocked.Increment(ref _renderGeneration);
        var options = OptionsFor(_settings);
        var fonts = _fonts;
        var jobs = _jobs.Select(j => j.Job).Reverse().ToList(); // oldest first
        if (jobs.Count == 0) return;
        try
        {
            var finished = await Task.Run(() =>
            {
                var renderer = new ZplRenderer(new PrinterMemory(), fonts);
                foreach (var job in jobs)
                {
                    if (_closing || Volatile.Read(ref _renderGeneration) != generation) return false;
                    RenderResult result;
                    _renderGate.Wait();
                    try { result = renderer.Render(ZplFormatter.Format(job.Zpl), options with { TextReadAsWindows1252 = job.ReadAsWindows1252 }); }
                    finally { _renderGate.Release(); }
                    // A newer pass started meanwhile: its result wins, this one is dropped.
                    if (Volatile.Read(ref _renderGeneration) != generation) return false;
                    job.Result = result;
                }
                return true;
            });
            if (!finished || _closing) return;
            foreach (var vm in _jobs) vm.Refresh();
            TrimHistory(); // pictures of another size take another amount of memory
            // The selection as it is now, not as it was when the pass started: the person may have clicked meanwhile.
            ShowJob(keepPage: true, keepField: FieldKey.Of(_selectedField, _page));
            Log.Information("Drew {Count} job(s) again", jobs.Count);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Drawing the jobs again failed");
            ShowMessage(UiText.Get("Ui_RerenderFailed"));
        }
    }

    // ---- keep jobs between runs --------------------------------------------------------------------

    /// <summary>
    /// Brings back the jobs kept by an earlier run (newest first, up to HistoryLimit), drawn again with today's
    /// settings into a fresh printer memory, oldest first: nothing of the old printer memory comes back.
    /// </summary>
    private async Task LoadKeptJobsAsync()
    {
        try
        {
            _keptJobs = KeptJobsState.Loading;
            var (saved, message) = await Task.Run(_store.Load);
            if (message is not null)
            {
                Log.Warning("Kept jobs: {Message}", message);
                AddStartupNote(message);
            }
            if (saved.Count == 0)
            {
                _keptJobs = KeptJobsState.Ready;
                ScheduleSave(); // jobs that arrived during the read are written now
                return;
            }

            var limit = Math.Max(1, _settings.HistoryLimit);
            var keep = saved.OrderByDescending(s => s.ReceivedAt).Take(limit).Reverse().ToList();
            var fonts = _fonts;
            var jobs = await Task.Run(() =>
            {
                var renderer = new ZplRenderer(new PrinterMemory(), fonts);
                var list = new List<LabelJob>();
                foreach (var s in keep)
                {
                    if (_closing) break;
                    // The settings of this moment, per job: the label size or language may change while a long list
                    // is drawn, and the jobs must come out as the window now draws them.
                    var options = OptionsFor(_settings);
                    RenderResult result;
                    _renderGate.Wait();
                    try { result = renderer.Render(ZplFormatter.Format(s.Zpl), options with { TextReadAsWindows1252 = s.ReadAsWindows1252 }); }
                    finally { _renderGate.Release(); }
                    list.Add(LabelJob.FromSaved(s, result));
                }
                return list;
            });
            if (_closing) return; // still Loading: the close does not save, so the file stays as it was

            // Oldest first in 'jobs'; the list is newest first and anything that arrived meanwhile is newer.
            for (var i = jobs.Count - 1; i >= 0; i--)
            {
                // Not counted in "received today": that counts this session's sends.
                _jobs.Add(new JobViewModel(jobs[i]));
            }
            TrimHistory();
            if (HistoryList.SelectedItem is null && _jobs.Count > 0) HistoryList.SelectedIndex = 0;
            Log.Information("Kept jobs: {Count} job(s) loaded", jobs.Count);
            // Only now may the list be written: it holds the kept jobs and whatever arrived meanwhile.
            _keptJobs = KeptJobsState.Ready;
            ScheduleSave();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Loading the kept jobs failed");
            AddStartupNote(UiText.Get("Ui_KeptJobsFailed"));
        }
    }

    /// <summary>Saves the job list 2 seconds after the last change, when "Keep jobs" is on; a burst saves once.</summary>
    private void ScheduleSave()
    {
        if (!_settings.KeepJobs || _closing) return;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private async void OnSaveTimer(object? sender, EventArgs e)
    {
        _saveTimer.Stop();
        try { await SaveJobsAsync(); }
        catch (Exception ex) { Log.Error(ex, "Saving the job list failed"); }
    }

    /// <summary>
    /// Writes the job list off the UI thread. Saves never overlap. A failure is shown once (not on every new job)
    /// until a save works again.
    /// </summary>
    private async Task SaveJobsAsync()
    {
        if (!_settings.KeepJobs) return;
        if (!await EnsureStoreLoadedAsync()) return;
        var jobs = _jobs.Select(j => j.Job).ToList();
        var limit = _settings.HistoryLimit;
        // The lock is taken and released on the worker thread: the save on close waits for it on the UI thread, so
        // a release that needed the UI thread could never happen while that wait runs.
        var result = await Task.Run(() =>
        {
            _saveLock.Wait();
            try { return _store.Save(jobs, limit); }
            finally { _saveLock.Release(); }
        });
        if (result.Success)
        {
            _lastSaveProblem = null;
        }
        else if (result.Message != _lastSaveProblem)
        {
            _lastSaveProblem = result.Message;
            Log.Warning("Kept jobs: {Message}", result.Message);
            ShowMessage(result.Message);
        }
    }

    /// <summary>
    /// Reads the job file once before the first save of the session (when "Keep jobs" was turned on after start),
    /// so a file written by a newer LabelScope is recognised and never overwritten. Its jobs are not shown: only a
    /// start brings kept jobs back.
    /// </summary>
    /// <returns>True when saving may go ahead; false while the file is still being read (or a load failed).</returns>
    private async Task<bool> EnsureStoreLoadedAsync()
    {
        if (_keptJobs == KeptJobsState.Ready) return true;
        if (_keptJobs == KeptJobsState.Loading) return false;
        _keptJobs = KeptJobsState.Loading;
        var (_, message) = await Task.Run(_store.Load);
        if (message is not null)
        {
            Log.Warning("Kept jobs: {Message}", message);
            ShowMessage(message);
        }
        _keptJobs = KeptJobsState.Ready;
        return true;
    }

    // ---- showing the selected job ---------------------------------------------------------------------

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => ShowJob();

    /// <summary>
    /// Shows the selected job everywhere: picture and captions, ZPL, Fields and Log tabs, status bar and window title.
    /// </summary>
    /// <param name="keepPage">Stay on the same page (a label was added to the job, or it was drawn again).</param>
    /// <param name="keepField">The field to select again after a re-render, or null to keep the selection when the
    /// same page is still shown.</param>
    private void ShowJob(bool keepPage = false, FieldKey? keepField = null)
    {
        try
        {
            var vm = HistoryList.SelectedItem as JobViewModel;
            if (vm != _shownJob)
            {
                _shownJob = vm;
                _page = 0;
                _selectedField = null;
            }
            else if (!keepPage)
            {
                _page = 0;
            }
            ShowPage(keepField);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not show the selected job");
            ShowMessage(UiText.Get("Ui_ShowFailed"));
        }
    }

    /// <summary>
    /// Shows page <see cref="_page"/> of the shown job. Only what changed is rebuilt: the ZPL text when the job's text
    /// changed, the picture and the field rows when the page or its drawing changed. Texts are always rewritten, so
    /// this is also what a language switch calls.
    /// </summary>
    private void ShowPage(FieldKey? keepField = null)
    {
        var vm = _shownJob;
        CopyZplButton.IsEnabled = vm is not null;
        CopyOriginalButton.IsEnabled = vm is not null;
        LabelFrame.Visibility = vm is null ? Visibility.Collapsed : Visibility.Visible;
        EmptyCanvasText.Visibility = vm is null ? Visibility.Visible : Visibility.Collapsed;
        Title = vm is null ? "LabelScope" : "LabelScope — " + vm.Title;

        if (vm is null)
        {
            _shownJobInstance = null;
            _shownPage = null;
            _shownZpl = "";
            _selectedField = null;
            _currentImage = null;
            LabelImage.Source = null;
            ZplView.Text = "";
            TopCaption.Text = SideCaption.Text = "";
            FieldsList.ItemsSource = null;
            LogList.ItemsSource = null;
            FieldsTab.Header = UiText.Get("Ui_TabFields");
            FieldsEmptyText.Text = LogEmptyText.Text = UiText.Get("Ui_NoJobSelected");
            FieldsEmptyText.Visibility = LogEmptyText.Visibility = Visibility.Visible;
            SelectedText.Text = UiText.Get("Ui_SelectedNone");
            BarLabelText.Text = BarSizeText.Text = BarLinesText.Text = BarErrorsText.Text = ReceivedText.Text = "";
            PrevPageButton.Visibility = NextPageButton.Visibility = Visibility.Collapsed;
            OutlineLayer.Children.Clear();
            UpdateGrid();
            UpdateZoomText();
            return;
        }

        var job = vm.Job;
        var pages = vm.Pages;
        _page = Math.Clamp(_page, 0, pages.Count - 1);
        var page = pages[_page];

        // The ZPL tab holds the whole job; it is only replaced when the job's text changed (a new label arrived).
        if (!ReferenceEquals(job, _shownJobInstance))
        {
            _shownJobInstance = job;
            _shownZpl = ZplFormatter.Format(job.Zpl);
            ZplView.Text = _shownZpl;
        }

        // A new page object means another page or a new drawing of it: decode its picture and rebuild the field rows.
        var pageChanged = !ReferenceEquals(page, _shownPage);
        if (pageChanged)
        {
            // A new drawing of the same page (re-render, language switch, a label added) keeps the selected field;
            // another job or page has already cleared it.
            var oldKey = keepField ?? FieldKey.Of(_selectedField, _page);
            _shownPage = page;
            // Drop the previous picture first so at most one decoded bitmap is alive.
            _currentImage = null;
            LabelImage.Source = null;
            _currentImage = page.CreateImage();
            LabelImage.Source = _currentImage;
            FieldsList.ItemsSource = page.Fields;
            _selectedField = oldKey is { } key && key.Page == _page ? key.Find(page.Fields) : null;
        }

        TopCaption.Text = page.WidthCaption;
        SideCaption.Text = page.HeightCaption;
        TopCaption.ToolTip = SideCaption.ToolTip = page.SizeTip.Length > 0 ? page.SizeTip : null;

        FieldsTab.Header = UiText.Get("Ui_TabFieldsCount", page.Fields.Count);
        FieldsEmptyText.Text = UiText.Get(page.IsPlaceholder ? "Ui_FieldsNoLabel" : "Ui_FieldsEmpty");
        FieldsEmptyText.Visibility = page.Fields.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        LogList.ItemsSource = vm.LogRows;
        LogEmptyText.Visibility = Visibility.Collapsed; // a job always logs at least its arrival

        // Status bar: page, size, lines and fields, errors (green at 0), and where the job came from.
        var many = pages.Count > 1;
        PrevPageButton.Visibility = NextPageButton.Visibility = many ? Visibility.Visible : Visibility.Collapsed;
        PrevPageButton.IsEnabled = _page > 0;
        NextPageButton.IsEnabled = _page < pages.Count - 1;
        BarLabelText.Text = UiText.Get("Ui_BarLabel", _page + 1, pages.Count);
        BarSizeText.Text = UiText.Get("Ui_BarSize", page.WidthInches, page.HeightInches, page.DisplayDpi) + " · " +
                           UiText.Get("Ui_BarDots", page.Label.WidthDots, page.Label.HeightDots);
        BarLinesText.Text = UiText.Get(page.Fields.Count == 1 ? "Ui_BarLinesFieldsOne" : "Ui_BarLinesFields", job.LineCount, page.Fields.Count);
        var errors = vm.ErrorCount;
        BarErrorsText.Text = errors == 1 ? UiText.Get("Ui_BarErrorOne") : UiText.Get("Ui_BarErrorMany", errors);
        BarErrorsText.Style = (Style)FindResource(errors == 0 ? "StatusTextSuccess" : "StatusTextError");
        ReceivedText.Text = ReceivedLine(job);

        if (pageChanged) OnZoomChanged(this, new RoutedEventArgs());
        UpdateGrid();
        ApplyFieldSelection(scroll: pageChanged);
    }

    /// <summary>
    /// "Received from WMS at 14:31:40", "Received from this computer at …", "Opened from file at …" or "Pasted at …":
    /// a whole sentence per case, so a source never appears with a capital letter in the middle of one.
    /// </summary>
    private static string ReceivedLine(LabelJob job)
    {
        var time = job.ReceivedAt.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        return job.Origin switch
        {
            JobOrigin.OpenedFromFile => UiText.Get("Ui_ReceivedFile", time),
            JobOrigin.Pasted => UiText.Get("Ui_ReceivedPasted", time),
            _ when IsThisComputer(job.RemoteAddress) => UiText.Get("Ui_ReceivedThisComputer", time),
            _ => UiText.Get("Ui_ReceivedFrom", job.DisplaySource, time),
        };
    }

    /// <summary>True when the sender is this computer: no address, or a loopback address (IPv4 in IPv6 form too).</summary>
    private static bool IsThisComputer(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return true;
        if (!IPAddress.TryParse(address.Trim(), out var ip)) return false;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        return IPAddress.IsLoopback(ip);
    }

    private void OnPrevPage(object sender, RoutedEventArgs e) => GoToPage(_page - 1);

    private void OnNextPage(object sender, RoutedEventArgs e) => GoToPage(_page + 1);

    /// <summary>Shows another label of the shown job; the field selection does not carry over to another label.</summary>
    private void GoToPage(int page)
    {
        if (_shownJob is null || page < 0 || page >= _shownJob.Pages.Count || page == _page) return;
        _page = page;
        _selectedField = null;
        ZplView.HighlightLine(null);
        ShowPage();
    }

    // ---- selecting a field ---------------------------------------------------------------------------------

    /// <summary>
    /// Names a field across a re-render: the page, its line, its kind, and which of the fields with that line and
    /// kind it is. A re-render makes new field objects, so the old object cannot be looked up by reference.
    /// </summary>
    private sealed record FieldKey(int Page, int Line, FieldKind Kind, int Index)
    {
        /// <summary>The key of the selected row on <paramref name="page"/>, or null when nothing is selected.</summary>
        public static FieldKey? Of(FieldRowViewModel? row, int page) =>
            row is null ? null : new FieldKey(page, row.Field.Line, row.Field.Kind, row.Index);

        /// <summary>The matching row in <paramref name="rows"/>, or null.</summary>
        public FieldRowViewModel? Find(IReadOnlyList<FieldRowViewModel> rows)
        {
            // Same index first (the usual case: the same ZPL draws the same fields in the same order) ...
            if (Index < rows.Count && rows[Index].Field.Line == Line && rows[Index].Field.Kind == Kind) return rows[Index];
            // ... otherwise the first field drawn from the same line with the same kind.
            return rows.FirstOrDefault(r => r.Field.Line == Line && r.Field.Kind == Kind);
        }
    }

    /// <summary>
    /// Selects <paramref name="row"/> (or nothing) everywhere at once: outline on the label, Fields row, highlighted
    /// ZPL line and the footer.
    /// </summary>
    private void SelectField(FieldRowViewModel? row)
    {
        _selectedField = row;
        ApplyFieldSelection(scroll: true);
    }

    /// <summary>Shows <see cref="_selectedField"/> in the Fields tab, the ZPL tab, the footer and the outline layer.</summary>
    private void ApplyFieldSelection(bool scroll)
    {
        var row = _selectedField;
        _settingFieldList = true;
        try
        {
            FieldsList.SelectedItem = row;
            if (row is not null && scroll) FieldsList.ScrollIntoView(row);
        }
        finally { _settingFieldList = false; }

        if (row is not null)
        {
            ZplView.HighlightLine(row.Field.Line);
            SelectedText.Text = row.FooterText;
        }
        else if (_selectedLine is { } line && _shownJob is not null)
        {
            ZplView.HighlightLine(line);
            SelectedText.Text = UiText.Get("Ui_SelectedLine", line);
        }
        else
        {
            ZplView.HighlightLine(null);
            SelectedText.Text = UiText.Get("Ui_SelectedNone");
        }
        DrawOutlines();
    }

    // A ZPL line chosen that drew no field (^XA, ^PW, a comment); shown highlighted until another selection.
    private int? _selectedLine;

    /// <summary>Esc: nothing selected any more.</summary>
    private void ClearSelection()
    {
        _selectedLine = null;
        SelectField(null);
    }

    /// <summary>
    /// A ZPL line was chosen (clicked in the ZPL tab, or a Log entry): select the first field drawn from it, on this
    /// label or on another label of the job; a line that drew nothing is only highlighted.
    /// </summary>
    private void SelectLine(int line)
    {
        if (_shownJob is null) return;
        var pages = _shownJob.Pages;
        // The page on screen first, then the others in order.
        foreach (var index in Enumerable.Range(0, pages.Count).OrderBy(i => i == _page ? -1 : i))
        {
            var rows = pages[index].Fields;
            if (FieldHitTest.FirstOnLine(pages[index].Label.Fields, line) is not { } field) continue;
            if (index != _page)
            {
                _page = index;
                _selectedField = null;
                ShowPage();
            }
            _selectedLine = null;
            SelectField(rows.First(r => ReferenceEquals(r.Field, field)));
            return;
        }
        _selectedLine = line;
        SelectField(null);
    }

    /// <summary>A click on the label: select the smallest field under the mouse, or nothing when it hit no field.</summary>
    private void OnLabelClicked(object sender, MouseButtonEventArgs e)
    {
        if (_shownPage is not { IsPlaceholder: false } page || LabelImage.Source is not BitmapSource src
            || src.PixelWidth == 0 || LabelImage.ActualWidth <= 0) return;
        // Screen pixels per label dot, from the label's size in dots (not the picture's pixels, which need not be
        // one per dot).
        var (dotsWide, dotsHigh) = ShownDots(src);
        var scaleX = LabelImage.ActualWidth / dotsWide;
        var scaleY = LabelImage.ActualHeight / dotsHigh;
        var p = e.GetPosition(LabelImage);
        var x = (int)Math.Floor(p.X / scaleX);
        var y = (int)Math.Floor(p.Y / scaleY);
        // About three screen pixels of slack, so a thin line shown small can still be hit.
        var slop = (int)Math.Ceiling(3 / Math.Min(scaleX, scaleY));
        var field = FieldHitTest.Find(page.Label.Fields, x, y, slop);
        _selectedLine = null;
        SelectField(field is null ? null : page.Fields.First(r => ReferenceEquals(r.Field, field)));
        e.Handled = true;
    }

    /// <summary>A row of the Fields tab was chosen (mouse or arrow keys): select that field everywhere.</summary>
    private void OnFieldRowSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_settingFieldList) return;
        _selectedLine = null;
        _selectedField = FieldsList.SelectedItem as FieldRowViewModel;
        ApplyFieldSelection(scroll: false);
    }

    /// <summary>A line of the ZPL was clicked.</summary>
    private void OnZplLineClicked(int line) => SelectLine(line);

    /// <summary>Esc clears the selection, unless it is closing an open drop-down list.</summary>
    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || SizePicker.IsDropDownOpen || LanguagePicker.IsDropDownOpen) return;
        if (_selectedField is null && _selectedLine is null) return;
        ClearSelection();
        e.Handled = true;
    }

    /// <summary>
    /// Draws the outlines over the label: a thin red box around every field that is not completely on the label
    /// (a barcode that will not scan is obvious at a glance), and the dashed blue box of the selected field.
    /// </summary>
    private void DrawOutlines()
    {
        OutlineLayer.Children.Clear();
        if (_shownPage is not { } page || LabelImage.Source is not BitmapSource src || src.PixelWidth == 0
            || LabelImage.ActualWidth <= 0) return;
        var (dotsWide, dotsHigh) = ShownDots(src);
        var scaleX = LabelImage.ActualWidth / dotsWide;
        var scaleY = LabelImage.ActualHeight / dotsHigh;

        var drawn = 0;
        foreach (var f in page.Label.Fields)
        {
            if (f.Problem is null) continue;
            if (++drawn > MaxProblemOutlines) break;
            AddOutline(f, scaleX, scaleY, (Media.Brush)FindResource("ErrorBrush"), dashed: false);
        }
        if (_selectedField is { } row) AddOutline(row.Field, scaleX, scaleY, (Media.Brush)FindResource("PrimaryBrush"), dashed: true);
    }

    private void AddOutline(LabelField f, double scaleX, double scaleY, Media.Brush brush, bool dashed)
    {
        if (f.Width <= 0 || f.Height <= 0) return; // not drawn: nothing on the label to outline
        // Outside the field, so the outline never covers the ink it marks; the selection sits further out than a
        // problem outline so both stay visible on a field that has both.
        var gap = dashed ? 4.0 : 2.0;
        var rect = new Rectangle
        {
            Width = f.Width * scaleX + 2 * gap,
            Height = f.Height * scaleY + 2 * gap,
            Stroke = brush,
            StrokeThickness = dashed ? 1.5 : 1.25,
            SnapsToDevicePixels = true,
        };
        if (dashed) rect.StrokeDashArray = [3, 2];
        Canvas.SetLeft(rect, f.X * scaleX - gap);
        Canvas.SetTop(rect, f.Y * scaleY - gap);
        OutlineLayer.Children.Add(rect);
    }

    // ---- zoom --------------------------------------------------------------------------------------

    /// <summary>"Fit NN%" was pressed: fit the whole label into the canvas again.</summary>
    private void OnFit(object sender, RoutedEventArgs e)
    {
        _fit = true;
        OnZoomChanged(sender, e);
    }

    /// <summary>"–": the next smaller zoom step below what is shown now (fit or not).</summary>
    private void OnZoomOut(object sender, RoutedEventArgs e)
    {
        var current = CurrentScale();
        _zoom = ZoomSteps.Where(z => z < current - 0.001).DefaultIfEmpty(ZoomSteps[0]).Max();
        _fit = false;
        OnZoomChanged(sender, e);
    }

    /// <summary>"+": the next larger zoom step above what is shown now (fit or not).</summary>
    private void OnZoomIn(object sender, RoutedEventArgs e)
    {
        var current = CurrentScale();
        _zoom = ZoomSteps.Where(z => z > current + 0.001).DefaultIfEmpty(ZoomSteps[^1]).Min();
        _fit = false;
        OnZoomChanged(sender, e);
    }

    /// <summary>Screen size of one label dot as shown now; the manual zoom when no label is shown.</summary>
    private double CurrentScale() =>
        LabelImage.Source is BitmapSource { PixelWidth: > 0 } src && LabelImage.Width > 0 && !double.IsNaN(LabelImage.Width)
            ? LabelImage.Width / ShownDots(src).Width
            : _zoom;

    /// <summary>
    /// The size in label dots of the picture on screen: the label's own size, which zoom, hit test, outlines and the
    /// grid are measured in. The picture's pixels are only used when no label is known.
    /// </summary>
    private (int Width, int Height) ShownDots(BitmapSource src) =>
        _shownPage is { Label: { WidthDots: > 0, HeightDots: > 0 } label }
            ? (label.WidthDots, label.HeightDots)
            : (Math.Max(1, src.PixelWidth), Math.Max(1, src.PixelHeight));

    /// <summary>
    /// Sizes the picture: in fit mode so that the label and its captions fill the canvas, otherwise at the chosen
    /// zoom with scroll bars. The size is set explicitly (not with Stretch) because the captions around the label
    /// need room that a stretched image would take.
    /// </summary>
    private void OnZoomChanged(object sender, RoutedEventArgs e)
    {
        if (LabelImage is null || ImageScroll is null || FitButton is null) return; // fires during InitializeComponent
        if (LabelImage.Source is not BitmapSource src || src.PixelWidth == 0 || src.PixelHeight == 0)
        {
            UpdateZoomText();
            return;
        }

        double scale;
        if (_fit)
        {
            // Scroll bars off: the stage is centred in the visible area and never scrolls in fit mode.
            ImageScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            ImageScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            // Room taken around the picture: the stage margin (24 each side), a caption (about 18 + 8) and the frame.
            var availableWidth = ImageScroll.ActualWidth - 48 - 30;
            var availableHeight = ImageScroll.ActualHeight - 48 - 30;
            if (availableWidth <= 0 || availableHeight <= 0) return; // not laid out yet; SizeChanged calls again
            var (w, h) = ShownDots(src);
            scale = Math.Clamp(Math.Min(availableWidth / w, availableHeight / h), 0.02, 8);
        }
        else
        {
            ImageScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            ImageScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            scale = _zoom;
        }

        var (dotsWide, dotsHigh) = ShownDots(src);
        LabelImage.Width = dotsWide * scale;
        LabelImage.Height = dotsHigh * scale;
        // Shrinking with nearest-neighbour makes thin barcode bars vanish; smooth scaling keeps them visible.
        // Enlarging keeps sharp pixels so every dot can be counted.
        Media.RenderOptions.SetBitmapScalingMode(LabelImage,
            scale >= 1 ? Media.BitmapScalingMode.NearestNeighbor : Media.BitmapScalingMode.HighQuality);
        UpdateZoomText();
    }

    /// <summary>Shows "Fit NN%" in fit mode, otherwise "NN%".</summary>
    private void UpdateZoomText()
    {
        if (FitButton is null) return;
        var percent = (int)Math.Round(CurrentScale() * 100);
        FitButton.Content = _fit ? UiText.Get("Ui_Fit", percent) : UiText.Get("Ui_Zoom", percent);
    }

    // ---- view options ------------------------------------------------------------------------------

    /// <summary>A view option in the "…" menu was ticked (or set from settings): apply both options.</summary>
    private void OnViewOptionChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return; // the options are set from settings.json in OnLoaded, which calls ApplyView itself
        ApplyView();
    }

    /// <summary>The layout last applied (null until the first call); lets a grid toggle skip the layout rebuild.</summary>
    private bool? _appliedStacked;

    private void ApplyView()
    {
        // ApplyLayout rebuilds the columns or rows, which throws away a splitter drag between the picture and the
        // ZPL. Ticking the grid option must not cost the user that, so the layout is only rebuilt when the stacked
        // choice really changed.
        var stacked = StackedBox.IsChecked == true;
        if (_appliedStacked != stacked)
        {
            ApplyLayout(stacked);
            _appliedStacked = stacked;
        }
        UpdateGrid();
    }

    /// <summary>
    /// Arranges the canvas card, splitter and ZPL card side by side (default) or on top of each other. The same three
    /// elements are moved between a column layout and a row layout, so nothing is created twice and the selected
    /// label, zoom and scroll positions are kept.
    /// </summary>
    private void ApplyLayout(bool stacked)
    {
        PreviewGrid.ColumnDefinitions.Clear();
        PreviewGrid.RowDefinitions.Clear();

        if (stacked)
        {
            PreviewGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 160 });
            PreviewGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(16) });
            PreviewGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 160 });
            Place(PicturePane, row: 0, column: 0);
            Place(PreviewSplitter, row: 1, column: 0);
            Place(ZplPane, row: 2, column: 0);
            PreviewSplitter.Width = double.NaN;
            PreviewSplitter.Height = 16;
            PreviewSplitter.HorizontalAlignment = HorizontalAlignment.Stretch;
            PreviewSplitter.VerticalAlignment = VerticalAlignment.Center;
            PreviewSplitter.ResizeDirection = GridResizeDirection.Rows;
        }
        else
        {
            // The ZPL card keeps the mockup's fixed width; the canvas takes whatever is left.
            PreviewGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 240 });
            PreviewGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            PreviewGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(440), MinWidth = 280 });
            Place(PicturePane, row: 0, column: 0);
            Place(PreviewSplitter, row: 0, column: 1);
            Place(ZplPane, row: 0, column: 2);
            PreviewSplitter.Height = double.NaN;
            PreviewSplitter.Width = 16;
            PreviewSplitter.HorizontalAlignment = HorizontalAlignment.Stretch;
            PreviewSplitter.VerticalAlignment = VerticalAlignment.Stretch;
            PreviewSplitter.ResizeDirection = GridResizeDirection.Columns;
        }
        OnZoomChanged(this, new RoutedEventArgs()); // "fit" depends on the space the picture now has
    }

    private static void Place(UIElement element, int row, int column)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
    }

    /// <summary>
    /// Shows or hides the 10 mm measuring grid over the picture. The grid is a tiled drawing whose tile is one
    /// cell, sized from the print resolution of the shown label and the current display scale (pixels per label
    /// dot). It is a separate element above the picture, so Save PNG and Copy image never contain it.
    /// </summary>
    private void UpdateGrid()
    {
        if (GridOverlay is null || GridBox is null || LabelImage is null) return; // events fire while the window is being built

        if (GridBox.IsChecked != true || LabelImage.Source is not BitmapSource src || src.PixelWidth == 0 || LabelImage.ActualWidth <= 0)
        {
            GridOverlay.Visibility = Visibility.Collapsed;
            return;
        }

        // The label's own resolution: a label drawn before a density change keeps the dots it was drawn with.
        var dpi = _shownPage?.DisplayDpi ?? _settings.DefaultDpi;
        var cell = LabelGrid.CellDots(dpi) * (LabelImage.ActualWidth / ShownDots(src).Width);
        if (cell < 6)
        {
            GridOverlay.Visibility = Visibility.Collapsed; // lines closer than this are just a grey wash
            return;
        }

        // The tile is drawn in a 100 x 100 coordinate space and scaled to 'cell' pixels, so the pen thickness is
        // given in that space: 200 / cell gives about two pixels, of which half is clipped at the tile edge.
        var pen = new Media.Pen(new Media.SolidColorBrush(Media.Color.FromArgb(90, 0, 120, 255)), 200.0 / cell);
        var lines = new Media.GeometryGroup();
        lines.Children.Add(new Media.LineGeometry(new Point(0, 0), new Point(100, 0)));
        lines.Children.Add(new Media.LineGeometry(new Point(0, 0), new Point(0, 100)));
        GridOverlay.Fill = new Media.DrawingBrush(new Media.GeometryDrawing(null, pen, lines))
        {
            TileMode = Media.TileMode.Tile,
            ViewboxUnits = Media.BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, 100, 100),
            ViewportUnits = Media.BrushMappingMode.Absolute,
            Viewport = new Rect(0, 0, cell, cell),
        };
        GridOverlay.Visibility = Visibility.Visible;
    }

    // ---- Log tab --------------------------------------------------------------------------------------

    /// <summary>The "Line N" link of a Log row: show that line and its field.</summary>
    private void OnLogLineLink(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is LogRow row) JumpToLogRow(row);
    }

    /// <summary>Enter on a Log row jumps to its line; the arrow keys only move between rows.</summary>
    private void OnLogKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || LogList.SelectedItem is not LogRow row) return;
        JumpToLogRow(row);
        e.Handled = true;
    }

    /// <summary>A double-click on a Log row jumps to its line.</summary>
    private void OnLogDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (LogList.SelectedItem is LogRow row) JumpToLogRow(row);
    }

    /// <summary>Shows the ZPL tab with the row's line highlighted and its field selected on the label.</summary>
    private void JumpToLogRow(LogRow row)
    {
        if (row.Line is not { } line) return;
        DetailTabs.SelectedItem = ZplTab;
        SelectLine(line);
    }

    // ---- window texts, language and status line ------------------------------------------------------

    /// <summary>
    /// Sets every fixed text of the window in the current language. Called once at start and again after a language
    /// switch; texts that depend on the shown job are set by <see cref="ShowPage"/>.
    /// </summary>
    private void ApplyTexts()
    {
        OpenFileButton.Content = UiText.Get("Ui_OpenFile");
        OpenFileButton.ToolTip = UiText.Get("Ui_OpenFileTip");
        PasteButton.ToolTip = UiText.Get("Ui_PasteZplTip");
        CopyZplButton.ToolTip = UiText.Get("Ui_CopyZplTip");
        ClearJobsButton.ToolTip = UiText.Get("Ui_ClearJobsTip");
        ZoomOutButton.ToolTip = UiText.Get("Ui_ZoomOut");
        ZoomInButton.ToolTip = UiText.Get("Ui_ZoomIn");
        FitButton.ToolTip = UiText.Get("Ui_FitTip");
        SizePicker.ToolTip = UiText.Get("Ui_SizePickerTip");
        LanguagePicker.ToolTip = UiText.Get("Ui_LanguageTip");
        // The one translated entry of the language list; the same words as in Printer setup.
        LanguageWindowsItem.Content = SetupText.Get("Setup_LanguageWindows");
        MoreButton.ToolTip = UiText.Get("Ui_More");
        PrevPageButton.ToolTip = UiText.Get("Ui_PrevLabel");
        NextPageButton.ToolTip = UiText.Get("Ui_NextLabel");
        DismissMessageButton.ToolTip = UiText.Get("Ui_DismissMessage");

        // Buttons that show only an icon (always, or when the toolbar is narrow) still need a name for screen
        // readers; the name is the text the button shows when there is room.
        SetName(OpenFileButton, "Ui_OpenFile");
        SetName(PasteButton, "Ui_PasteZpl");
        SetName(CopyZplButton, "Ui_CopyZpl");
        SetName(SavePngButton, "Ui_SavePng");
        SetName(ClearJobsButton, "Ui_ClearJobs");
        SetName(PrinterSetupButton, "Ui_PrinterSetup");
        SetName(ZoomOutButton, "Ui_ZoomOut");
        SetName(ZoomInButton, "Ui_ZoomIn");
        SetName(MoreButton, "Ui_More");
        SetName(PrevPageButton, "Ui_PrevLabel");
        SetName(NextPageButton, "Ui_NextLabel");
        SetName(DismissMessageButton, "Ui_DismissMessage");
        System.Windows.Automation.AutomationProperties.SetName(SizePicker, UiText.Get("Ui_SizePickerName"));
        System.Windows.Automation.AutomationProperties.SetName(LanguagePicker, UiText.Get("Ui_LanguageTip"));
        System.Windows.Automation.AutomationProperties.SetName(FieldsList, UiText.Get("Ui_TabFields"));
        System.Windows.Automation.AutomationProperties.SetName(LogList, UiText.Get("Ui_TabLog"));
        System.Windows.Automation.AutomationProperties.SetName(HistoryList, UiText.Get("Ui_PrintJobs"));

        InstallItem.Header = UiText.Get("Ui_InstallPrinter");
        InstallItem.ToolTip = UiText.Get("Ui_InstallPrinterTip");
        RemoveItem.Header = UiText.Get("Ui_RemovePrinter");
        CopyImageItem.Header = UiText.Get("Ui_CopyImage");
        CopyOriginalButton.Header = UiText.Get("Ui_CopyOriginal");
        CopyOriginalButton.ToolTip = UiText.Get("Ui_CopyOriginalTip");
        ClearMemoryButton.Header = UiText.Get("Ui_ClearMemory");
        ClearMemoryButton.ToolTip = UiText.Get("Ui_ClearMemoryTip");
        GridBox.Header = UiText.Get("Ui_LightGrid");
        StackedBox.Header = UiText.Get("Ui_StackedLayout");
        OpenLogItem.Header = UiText.Get("Ui_OpenLog");
        CheckUpdateButton.Header = UiText.Get("Ui_CheckUpdates");
        if (_update is { } update) UpdateButton.Content = UiText.Get("Ui_UpdateTo", update.Version.ToString(3));

        JobsTitleText.Text = UiText.Get("Ui_PrintJobs");
        ZplTab.Header = UiText.Get("Ui_TabZpl");
        LogTab.Header = UiText.Get("Ui_TabLog");
        EmptyCanvasText.Text = UiText.Get("Ui_EmptyCanvas", _settings.PrinterName);

        UpdateStatusLine();
        UpdateReceivedToday();
        UpdateSizePicker();
        UpdateZoomText();
        UpdateMemoryText(); // the memory line comes from Core, which follows the same language
        // The picker entry depends on the Language setting as well as the culture: a Printer setup save from
        // "English" to "Windows language" on English Windows changes no culture, but must change the entry. It is
        // selected before measuring because "Windows language" is wider than "English".
        SelectLanguageInPicker();
        MeasureFullToolbar();
    }

    private static void SetName(DependencyObject element, string key) =>
        System.Windows.Automation.AutomationProperties.SetName(element, UiText.Get(key));

    // ---- toolbar fit -------------------------------------------------------------------------------

    // How many buttons of CompactOrder currently show only their icon (-1 = not set yet).
    private int _compactCount = -1;

    // Measured once per set of texts (start, language switch): the width of both toolbar halves with every text, and
    // what each button gives back when it drops its text. A resize then only does arithmetic, never a layout pass.
    private double _fullToolbarWidth;
    private double[] _textSavings = [];

    /// <summary>
    /// The flat buttons that drop their text when the toolbar runs out of room, in the order they do so: the least
    /// used first, "Printer setup" last. "Open ZPL file" always keeps its text.
    /// </summary>
    private Button[] CompactOrder => [ClearJobsButton, SavePngButton, CopyZplButton, PasteButton, PrinterSetupButton];

    /// <summary>The text a button of <see cref="CompactOrder"/> shows when there is room.</summary>
    private string FullText(Button button) =>
        button == PasteButton ? UiText.Get("Ui_PasteZpl")
        : button == CopyZplButton ? UiText.Get("Ui_CopyZpl")
        : button == SavePngButton ? UiText.Get("Ui_SavePng")
        : button == ClearJobsButton ? UiText.Get("Ui_ClearJobs")
        : UiText.Get("Ui_PrinterSetup");

    /// <summary>
    /// Measures the toolbar after its texts changed (start, language switch): once with every text and once with
    /// only icons. Two layout passes per language switch, none per resize.
    /// </summary>
    private void MeasureFullToolbar()
    {
        if (ToolbarBar is null) return;
        var buttons = CompactOrder;
        var unlimited = new Size(double.PositiveInfinity, double.PositiveInfinity);

        _compactCount = -1;
        SetCompactCount(0);
        // A changed button text only reaches the panels' sizes after a layout pass.
        ToolbarBar.UpdateLayout();
        ToolbarLeft.Measure(unlimited);
        ToolbarRight.Measure(unlimited);
        _fullToolbarWidth = ToolbarLeft.DesiredSize.Width + ToolbarRight.DesiredSize.Width;
        var full = buttons.Select(b => b.DesiredSize.Width).ToArray();

        SetCompactCount(buttons.Length);
        ToolbarBar.UpdateLayout();
        _textSavings = buttons.Select((b, i) =>
        {
            b.Measure(unlimited);
            return Math.Max(0, full[i] - b.DesiredSize.Width);
        }).ToArray();

        _compactCount = -1;
        UpdateToolbarFit();
    }

    private void OnToolbarSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged) UpdateToolbarFit();
    }

    /// <summary>
    /// Drops button texts one at a time, in <see cref="CompactOrder"/>, until the toolbar fits the window, so a
    /// narrow window keeps as many labels as it can. Uses the widths measured by <see cref="MeasureFullToolbar"/>.
    /// </summary>
    private void UpdateToolbarFit()
    {
        if (ToolbarBar is null || _textSavings.Length == 0) return;
        var available = ToolbarBar.ActualWidth - ToolbarBar.Padding.Left - ToolbarBar.Padding.Right - 16; // 16: gap
        var count = 0;
        if (available > 0)
        {
            var width = _fullToolbarWidth;
            while (count < _textSavings.Length && width > available) width -= _textSavings[count++];
        }
        SetCompactCount(count);
    }

    /// <summary>Shows the first <paramref name="count"/> buttons of <see cref="CompactOrder"/> as icons only.</summary>
    private void SetCompactCount(int count)
    {
        if (count == _compactCount) return;
        _compactCount = count;
        var buttons = CompactOrder;
        for (var i = 0; i < buttons.Length; i++)
        {
            var button = buttons[i];
            var text = FullText(button);
            var compact = i < count;
            button.Content = compact ? null : text;
            // Without its text the button still needs a name on hover; buttons with their own tip keep it.
            if (button == SavePngButton || button == PrinterSetupButton) button.ToolTip = compact ? text : null;
        }
    }

    // ---- language -------------------------------------------------------------------------------------

    /// <summary>
    /// Selects the picker entry of the current language without running the switch itself. "Windows language" is
    /// shown while the Language setting is "" and the window really speaks the Windows language; otherwise the entry
    /// of the language in use (for example after a choice that could not be saved).
    /// </summary>
    private void SelectLanguageInPicker()
    {
        var setting = Text.NormalizeLanguage(_settings.Language) ?? "";
        // Culture names are compared, not two-letter codes: "pt-BR" has the two-letter code "pt".
        var code = setting == "" && Text.CultureForLanguage("").Name == Text.Culture.Name
            ? ""
            : Text.LanguageOf(Text.Culture);
        _changingLanguagePicker = true;
        try { LanguagePicker.SelectedItem = LanguagePicker.Items.OfType<ComboBoxItem>().First(i => (string)i.Tag == code); }
        finally { _changingLanguagePicker = false; }
    }

    // True while code (not the user) sets the language picker, so no switch runs.
    private bool _changingLanguagePicker;

    /// <summary>
    /// A language (or "Windows language") was chosen: save it as the Language setting (so the next start uses it
    /// too), switch the window and Core's messages, and draw every job again so its warnings and field texts change
    /// language.
    /// </summary>
    private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_changingLanguagePicker || !IsLoaded || LanguagePicker.SelectedItem is not ComboBoxItem { Tag: string code }) return;
        var culture = Text.CultureForLanguage(code);
        if (culture.Name == Text.Culture.Name && string.Equals(_settings.Language, code, StringComparison.OrdinalIgnoreCase)) return;
        if (!SaveSettingsChange(s => s.Language = code))
        {
            // Not saved (the message says why), but the person asked for this language now.
            SwitchLanguage(culture);
            _ = RerenderAllJobsAsync();
        }
    }

    /// <summary>
    /// The culture for the Language setting: the language chosen, and for "" the same Windows rule Core uses by
    /// default (Spanish, Portuguese or French Windows gives that language, anything else English).
    /// </summary>
    private static CultureInfo CultureForSetting(string? language) => Text.CultureForLanguage(language);

    /// <summary>
    /// Changes the language of the window and of Core's messages, then rewrites every text. The jobs' own warnings
    /// and field texts change when they are drawn again (the caller starts that).
    /// </summary>
    private void SwitchLanguage(CultureInfo culture)
    {
        try
        {
            Text.Culture = culture;
            SelectLanguageInPicker();
            ApplyTexts();
            // The job cards build their texts on each read; asking them to read again redraws them.
            foreach (var vm in _jobs) vm.Refresh();
            ShowPage();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not switch the language");
            ShowMessage(UiText.Get("Ui_LanguageFailed"));
        }
    }

    /// <summary>
    /// The line under "Print jobs": green "Ready" when labels can arrive and the Windows printer exists, otherwise
    /// the problem in red, so the user sees at once why nothing arrives.
    /// </summary>
    private void UpdateStatusLine()
    {
        if (StatusLineText is null) return;
        var address = $"{_settings.ListenAddress}:{_settings.ListenPort}";
        string text;
        var ok = false;
        if (_listening is null) text = UiText.Get("Ui_StatusStarting");
        else if (_listening == false) text = UiText.Get("Ui_StatusNotListening");
        else if (_printerSettingsBroken) text = UiText.Get("Ui_StatusPrinterSettings", address);
        else
        {
            switch (_printerStatus)
            {
                case PrinterStatus.Installed:
                    text = UiText.Get("Ui_StatusReady", _settings.PrinterName);
                    ok = true;
                    break;
                case PrinterStatus.NotInstalled:
                    text = UiText.Get("Ui_StatusNoPrinter", _settings.PrinterName);
                    break;
                case PrinterStatus.NameTakenByOther:
                    text = UiText.Get("Ui_StatusNameTaken", _settings.PrinterName);
                    break;
                case null:
                    // Listening, printer check still running: nothing is wrong yet.
                    text = UiText.Get("Ui_StatusStarting");
                    ok = true;
                    break;
                default:
                    text = UiText.Get("Ui_StatusPrinterUnknown", address);
                    break;
            }
        }

        StatusLineText.Text = text;
        StatusLineText.ToolTip = address;
        var brush = (Media.Brush)FindResource(ok ? "SuccessBrush" : "ErrorBrush");
        StatusLineText.Foreground = brush;
        StatusIcon.Foreground = brush;
        StatusIcon.Text = (string)FindResource(ok ? "IconCheck" : "IconError");
    }

    /// <summary>"N received today" in the Print jobs header.</summary>
    private void UpdateReceivedToday()
    {
        if (ReceivedTodayText is null) return;
        if (_receivedTodayDate != DateTime.Today)
        {
            _receivedTodayDate = DateTime.Today;
            _receivedToday = 0;
        }
        ReceivedTodayText.Text = UiText.Get(_receivedToday == 1 ? "Ui_ReceivedTodayOne" : "Ui_ReceivedToday", _receivedToday);
    }

    // ---- label size picker -----------------------------------------------------------------------------

    /// <summary>What an entry of the size picker does.</summary>
    private sealed record SizeChoice(LabelSize? Size, int? Dpi, bool Custom);

    // True while code (not the user) fills or selects the size picker, so no change runs.
    private bool _changingSizePicker;

    /// <summary>The densities offered, as in Printer setup.</summary>
    private static readonly int[] OfferedDpis = [203, 300, 600];

    /// <summary>
    /// Fills the size picker: every preset size at the current density ("4 × 6 in · 203 dpi"), the other densities
    /// for the current size, and "Custom…" (Printer setup). The entry of the current label is selected; a size that
    /// is not a preset gets its own entry at the top.
    /// </summary>
    private void UpdateSizePicker()
    {
        if (SizePicker is null) return;
        _changingSizePicker = true;
        try
        {
            var dpi = _settings.DefaultDpi;
            var widthIn = _settings.LabelWidthMm / LabelSizes.MmPerInch;
            var heightIn = _settings.LabelHeightMm / LabelSizes.MmPerInch;
            var current = LabelSizes.Find(_settings.LabelWidthMm, _settings.LabelHeightMm);
            var items = new List<ComboBoxItem>();
            ComboBoxItem? selected = null;
            if (current is null)
            {
                selected = Item(UiText.Get("Ui_SizePicker", LabelSizes.Format(widthIn), LabelSizes.Format(heightIn), dpi),
                    new SizeChoice(null, null, false));
                items.Add(selected);
            }
            foreach (var preset in LabelSizes.Presets)
            {
                var item = Item(UiText.Get("Ui_SizePicker", LabelSizes.Format(preset.WidthIn), LabelSizes.Format(preset.HeightIn), dpi),
                    new SizeChoice(preset, null, false));
                if (preset == current) selected = item;
                items.Add(item);
            }
            foreach (var other in OfferedDpis.Where(d => d != dpi))
            {
                var item = Item(UiText.Get("Ui_SizePickerDpi", LabelSizes.Format(widthIn), LabelSizes.Format(heightIn), other),
                    new SizeChoice(null, other, false));
                item.BorderBrush = (Media.Brush)FindResource("CardBorderBrush");
                items.Add(item);
            }
            items.Add(Item(UiText.Get("Ui_SizePickerCustom"), new SizeChoice(null, null, true)));
            SizePicker.ItemsSource = items;
            SizePicker.SelectedItem = selected;
        }
        finally { _changingSizePicker = false; }

        static ComboBoxItem Item(string text, SizeChoice choice) => new() { Content = text, Tag = choice };
    }

    /// <summary>
    /// An entry of the size picker was chosen: save the size or density (as Printer setup would) and draw every job
    /// again; "Custom…" opens Printer setup.
    /// </summary>
    private void OnSizePicked(object sender, SelectionChangedEventArgs e)
    {
        if (_changingSizePicker || !IsLoaded || SizePicker.SelectedItem is not ComboBoxItem { Tag: SizeChoice choice }) return;
        if (choice.Custom)
        {
            UpdateSizePicker(); // back to the current label while the screen is open
            OnPrinterSetup(sender, e);
            return;
        }
        if (choice.Size is { } size)
        {
            if (LabelSizes.Find(_settings.LabelWidthMm, _settings.LabelHeightMm) == size) return;
            // Rounded as Printer setup does, so the file shows 57.15 rather than 57.150000000000006.
            SaveSettingsChange(s =>
            {
                s.LabelWidthMm = Math.Round(size.WidthMm, 3);
                s.LabelHeightMm = Math.Round(size.HeightMm, 3);
            });
        }
        else if (choice.Dpi is { } dpi)
        {
            SaveSettingsChange(s => s.DefaultDpi = dpi);
        }
    }

    /// <summary>
    /// Saves one change made in the toolbar the same way Printer setup saves: settings.json is read again, only this
    /// change is put on top (other keys keep what the file holds now), the file is written, and the result is taken
    /// into use. On failure the message says why and nothing changes.
    /// </summary>
    /// <returns>True when the change was saved and applied.</returns>
    private bool SaveSettingsChange(Action<AppSettings> change)
    {
        AppSettings settings;
        try
        {
            var fresh = new SettingsStore().LoadOrCreate(_settingsPath);
            foreach (var message in fresh.Messages) Log.Information("Toolbar: settings: {Message}", message);
            settings = fresh.Settings;
        }
        catch (Exception ex)
        {
            // LoadOrCreate reports problems as messages; this only guards against the unexpected.
            Log.Warning(ex, "Toolbar: settings.json could not be read again before saving");
            settings = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(System.Text.Json.JsonSerializer.Serialize(_settings))!;
        }
        change(settings);
        var result = new SettingsStore().Save(_settingsPath, settings);
        Log.Information("Toolbar: save {Success} {Message}", result.Success, result.Message);
        if (!result.Success)
        {
            ShowMessage(result.Message);
            UpdateSizePicker();
            SelectLanguageInPicker();
            return false;
        }
        ApplySettings(settings);
        return true;
    }

    // ---- open and paste --------------------------------------------------------------------------------

    /// <summary>
    /// Shows a ZPL file from disk exactly as if it had been printed to LabelScope, named after the file. The file is
    /// read off the UI thread, so a slow network drive never freezes the window.
    /// </summary>
    private async void OnOpenFile(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new OpenFileDialog { Filter = UiText.Get("Ui_OpenFileFilter") };
            if (dialog.ShowDialog(this) != true) return;
            var path = dialog.FileName;
            string? text;
            var readAsWindows1252 = false;
            try
            {
                (text, readAsWindows1252) = await Task.Run(() =>
                {
                    // Checked before reading so a huge file is never loaded into memory.
                    if (new FileInfo(path).Length > MaxOpenFileBytes) return ((string?)null, false);
                    var decoded = DecodeLikePrinter(File.ReadAllBytes(path), out var windows1252);
                    return (decoded, windows1252);
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                Log.Warning(ex, "Could not read {Path}", path);
                ShowMessage(UiText.Get("Ui_FileReadFailed"));
                return;
            }
            if (text is null)
            {
                ShowMessage(UiText.Get("Ui_FileTooLarge"));
                return;
            }
            // The same rule as Paste: without ^XA the text holds no label (a download alone is printed, not opened).
            if (text.IndexOf("^XA", StringComparison.OrdinalIgnoreCase) < 0)
            {
                ShowMessage(UiText.Get("Ui_FileEmpty"));
                return;
            }
            RenderLocalJob(text, JobOrigin.OpenedFromFile, System.IO.Path.GetFileName(path), readAsWindows1252);
        }
        catch (Exception ex)
        {
            // async void: nothing may escape.
            Log.Error(ex, "Open ZPL file failed");
            ShowMessage(UiText.Get("Ui_FileReadFailed"));
        }
    }

    /// <summary>
    /// Turns file bytes into text the way the printer listener does (strict UTF-8, otherwise Windows-1252), so a
    /// file opened here and the same file printed to LabelScope give the same label, accented text and ^CI28 warning
    /// included.
    /// </summary>
    /// <param name="bytes">The file.</param>
    /// <param name="readAsWindows1252">True when the file was not valid UTF-8 and was read as Windows-1252.</param>
    private static string DecodeLikePrinter(byte[] bytes, out bool readAsWindows1252)
    {
        // A UTF-8 byte-order mark is not part of the ZPL.
        var start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        return ZplStreamSplitter.Decode(bytes, start, bytes.Length - start, out readAsWindows1252);
    }

    /// <summary>Shows the ZPL on the clipboard as a new job.</summary>
    private void OnPasteZpl(object sender, RoutedEventArgs e)
    {
        string text;
        try { text = Clipboard.ContainsText() ? Clipboard.GetText() : ""; }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            Log.Warning(ex, "Clipboard was busy while pasting");
            ShowMessage(UiText.Get("Ui_PasteFailed"));
            return;
        }
        if (text.IndexOf("^XA", StringComparison.OrdinalIgnoreCase) < 0)
        {
            ShowMessage(UiText.Get("Ui_PasteEmpty"));
            return;
        }
        RenderLocalJob(text, JobOrigin.Pasted, null);
    }

    /// <summary>
    /// Renders text that did not come over the network through the very same path as a printed job (render gate,
    /// formatting, placeholder rules, list). That path waits for the gate, so it runs off the UI thread.
    /// </summary>
    /// <param name="zpl">The text.</param>
    /// <param name="origin">Opened from a file or pasted.</param>
    /// <param name="name">The file name, or null to name the job from its ZPL.</param>
    /// <param name="readAsWindows1252">True when a file's bytes were read as Windows-1252; a paste is always text.</param>
    private void RenderLocalJob(string zpl, JobOrigin origin, string? name, bool readAsWindows1252 = false)
    {
        // A text without ^XZ at its end is treated like a connection that stopped early, as for printed jobs.
        var complete = zpl.LastIndexOf("^XZ", StringComparison.OrdinalIgnoreCase) >= 0;
        var job = new ReceivedLabel(zpl, DateTimeOffset.Now, "", complete, ReadAsWindows1252: readAsWindows1252);
        _ = Task.Run(() => ProcessIncoming(job, origin, name));
    }

    // ---- Printer setup and settings ---------------------------------------------------------------------

    /// <summary>
    /// "Printer setup": opens the Printer setup screen. Whatever it saved (on Save, or already on Reinstall printer
    /// even when the screen is then cancelled) is applied at once.
    /// </summary>
    private void OnPrinterSetup(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new PrinterSetupWindow(_settings, _settingsPath)
            {
                Owner = this,
                CheckForUpdates = () => CheckForUpdatesAsync(userAsked: true),
            };
            var openSettingsFile = false;
            dialog.OpenSettingsFileRequested += (_, _) => openSettingsFile = true;
            dialog.OpenLogFolderRequested += (_, _) => Open(ResolveLogFolder());
            dialog.ShowDialog();
            if (dialog.Result is { } saved) ApplySettings(saved);
            // Opened only after the screen is closed, so its Save cannot overwrite what the person types in the file.
            if (openSettingsFile) Open(_settingsPath);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Printer setup failed");
            ShowMessage(SetupText.Get("Setup_OpenFailed"));
        }
    }

    /// <summary>
    /// Takes saved settings into use (from Printer setup or a toolbar picker): language, printer (name and status),
    /// "Keep jobs", and the label size and density. When the language, size or density changed, every job in the list
    /// is drawn again so it shows what the printer would now print.
    /// </summary>
    private async void ApplySettings(AppSettings saved)
    {
        try
        {
            var old = _settings;
            var printerChanged = !string.Equals(saved.PrinterName, old.PrinterName, StringComparison.Ordinal);
            var languageChanged = !string.Equals(saved.Language, old.Language, StringComparison.OrdinalIgnoreCase);
            var labelChanged = saved.DefaultDpi != old.DefaultDpi
                || Math.Abs(saved.LabelWidthMm - old.LabelWidthMm) > 0.0005
                || Math.Abs(saved.LabelHeightMm - old.LabelHeightMm) > 0.0005;
            var keepJobsTurnedOn = saved.KeepJobs && !old.KeepJobs;
            // The dialog saves on top of a fresh read of settings.json, so a hand edit of a key that is only read at
            // start can arrive here. Those keep their running values until the next start: the listener still
            // listens on the old address and port (the printer must keep pointing at it), the log still goes to the
            // old folder, and the fonts were loaded from the old folder.
            saved.ListenAddress = old.ListenAddress;
            saved.ListenPort = old.ListenPort;
            saved.LogFolder = old.LogFolder;
            saved.FontsFolder = old.FontsFolder;
            // A single reference swap: the socket thread reads the field once per job (see ProcessIncoming).
            _settings = saved;
            Log.Information("Settings saved: printer {Printer}, label {Width} x {Height} mm, {Dpi} dpi, language '{Language}', keep jobs {Keep}",
                saved.PrinterName, saved.LabelWidthMm, saved.LabelHeightMm, saved.DefaultDpi, saved.Language, saved.KeepJobs);

            var culture = CultureForSetting(saved.Language);
            var switchLanguage = languageChanged && culture.Name != Text.Culture.Name;
            if (switchLanguage) SwitchLanguage(culture);
            else ApplyTexts(); // printer name, size picker and status line name values that may have changed
            UpdateGrid();
            if (switchLanguage || labelChanged) _ = RerenderAllJobsAsync();

            // Turning "Keep jobs" off leaves jobs.json as it is (it is simply no longer written); turning it on saves
            // the list now, after reading the file once so a newer version's file is never overwritten.
            if (keepJobsTurnedOn) ScheduleSave();

            // The installer is built for one name, so a new name needs a new one. The dialog checked the name
            // with the installer's own rules, so this cannot fail on the name; the flag is cleared because
            // CreateInstaller only ever sets it.
            if (printerChanged || _installer is null)
            {
                _printerSettingsBroken = false;
                _installer = null;
                _printerStatus = null;
                CreateInstaller();
                UpdatePrinterButtons();
            }
            await RefreshPrinterStatusAsync();
        }
        catch (Exception ex)
        {
            // async void: nothing may escape.
            Log.Error(ex, "Applying the saved settings failed");
            ShowMessage(SetupText.Get("Setup_ApplyFailed"));
        }
    }

    /// <summary>"…": the actions that have no room on the toolbar.</summary>
    private void OnMore(object sender, RoutedEventArgs e) => OpenMenuBelow(MoreButton);

    private static void OpenMenuBelow(Button button)
    {
        if (button.ContextMenu is not { } menu) return;
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    /// <summary>The cross on the message strip: hide the notes shown so far.</summary>
    private void OnDismissMessage(object sender, RoutedEventArgs e)
    {
        _startupNotes.Clear();
        _actionMessage = "";
        RefreshMessageText();
    }

    // ---- printer ------------------------------------------------------------------------------

    private void CreateInstaller()
    {
        try
        {
            _installer = new PrinterInstaller(new PowerShellRunner(), _settings.PrinterName, _settings.ListenPort, new WinspoolPrinterLookup());
        }
        catch (ArgumentException ex) // also covers ArgumentOutOfRangeException; the message is written for the user
        {
            Log.Warning(ex, "Printer settings are not usable");
            UpdatePrinterButtons(); // _installer is null, so both stay off
            _printerSettingsBroken = true;
            UpdateStatusLine();
            AddStartupNote(ex.Message);
            MessageBox.Show(this, ex.Message, "LabelScope", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// Both printer buttons are on unless the installer could not be built or a job is running.
    /// An unknown status does not switch them off: InstallAsync/RemoveAsync re-check it themselves and refuse safely.
    /// </summary>
    private void UpdatePrinterButtons()
    {
        var enabled = _installer is not null && !_printerBusy;
        InstallItem.IsEnabled = enabled;
        RemoveItem.IsEnabled = enabled;
    }

    /// <summary>Re-checks the printer when the user comes back to the window, so a transient failure heals itself.</summary>
    private async void OnActivated(object? sender, EventArgs e)
    {
        if (_installer is null || _printerBusy || _closing) return;
        if (DateTime.UtcNow - _lastStatusCheck < TimeSpan.FromSeconds(10)) return;
        await RefreshPrinterStatusAsync();
    }

    private async Task RefreshPrinterStatusAsync()
    {
        if (_installer is null) return;
        _lastStatusCheck = DateTime.UtcNow; // set first so overlapping activations cannot start a second check
        try
        {
            _printerStatus = await _installer.GetStatusAsync();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Printer status check failed");
            _printerStatus = PrinterStatus.Unknown;
        }
        UpdateStatusLine();
    }

    private async void OnInstallPrinter(object sender, RoutedEventArgs e)
    {
        if (_installer is null || _printerBusy) return;
        try
        {
            var ask = MessageBox.Show(this, UiText.Get("Ui_InstallAsk", _settings.PrinterName), UiText.Get("Ui_InstallPrinter"),
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (ask != MessageBoxResult.Yes) return;
            await RunPrinterJobAsync(_installer.InstallAsync, "Install printer");
        }
        catch (Exception ex)
        {
            // async void: nothing may escape.
            Log.Error(ex, "Install printer handler failed");
            ShowMessage(UiText.Get("Ui_PrinterActionFailed"));
        }
    }

    private async void OnRemovePrinter(object sender, RoutedEventArgs e)
    {
        if (_installer is null || _printerBusy) return;
        try
        {
            var ask = MessageBox.Show(this, UiText.Get("Ui_RemoveAsk", _settings.PrinterName), UiText.Get("Ui_RemovePrinter"),
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (ask != MessageBoxResult.Yes) return;
            await RunPrinterJobAsync(_installer.RemoveAsync, "Remove printer");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Remove printer handler failed");
            ShowMessage(UiText.Get("Ui_PrinterActionFailed"));
        }
    }

    /// <summary>Runs one install/remove with both buttons off, shows the message the installer returned, then re-checks the status.</summary>
    private async Task RunPrinterJobAsync(Func<CancellationToken, Task<OperationResult>> job, string what)
    {
        _printerBusy = true;
        UpdatePrinterButtons();
        try
        {
            var result = await job(CancellationToken.None);
            Log.Information("{What}: {Success} {Message}", what, result.Success, result.Message);
            MessageBox.Show(this, result.Message, "LabelScope", MessageBoxButton.OK,
                result.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            // The installer is documented not to throw; this only protects against an unexpected bug.
            Log.Error(ex, "{What} failed unexpectedly", what);
            ShowMessage(UiText.Get("Ui_PrinterActionFailed"));
        }
        finally
        {
            _printerBusy = false;
            UpdatePrinterButtons();
        }
        await RefreshPrinterStatusAsync();
    }

    // ---- toolbar actions ---------------------------------------------------------------------

    /// <summary>Saves the label on screen (the page shown of the selected job) as the PNG LabelScope drew.</summary>
    private void OnSavePng(object sender, RoutedEventArgs e)
    {
        if (_shownJob is null || _shownPage is not { } page)
        {
            ShowMessage(UiText.Get("Ui_NothingToSave"));
            return;
        }
        var suffix = _shownJob.Pages.Count > 1 ? $"-{page.Index + 1}" : "";
        var dialog = new SaveFileDialog
        {
            Filter = UiText.Get("Ui_PngFilter"),
            FileName = $"label-{_shownJob.Job.ReceivedAt.ToLocalTime():yyyyMMdd-HHmmss}{suffix}.png",
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllBytes(dialog.FileName, page.Label.PngBytes);
            ShowMessage(UiText.Get("Ui_PngSaved", dialog.FileName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Log.Warning(ex, "Could not save {Path}", dialog.FileName);
            ShowMessage(UiText.Get("Ui_PngSaveFailed", dialog.FileName));
        }
    }

    private void OnCopyImage(object sender, RoutedEventArgs e)
    {
        if (_currentImage is null)
        {
            ShowMessage(UiText.Get("Ui_NothingToCopyImage"));
            return;
        }
        try
        {
            // Labels are stored as 8-bit grey PNGs (fast to encode); some programs paste a grey clipboard bitmap as
            // black or not at all, so the copy is converted to ordinary 32-bit colour first.
            var colour = new FormatConvertedBitmap(_currentImage, System.Windows.Media.PixelFormats.Bgra32, null, 0);
            colour.Freeze();
            Clipboard.SetImage(colour);
            ShowMessage(UiText.Get("Ui_ImageCopied"));
        }
        catch (Exception ex)
        {
            // The clipboard can be locked by another program for a moment.
            Log.Warning(ex, "Could not copy the image");
            ShowMessage(UiText.Get("Ui_ClipboardBusy"));
        }
    }

    private void OnCopyZpl(object sender, RoutedEventArgs e) => CopyZpl(original: false);

    private void OnCopyOriginalZpl(object sender, RoutedEventArgs e) => CopyZpl(original: true);

    /// <summary>
    /// Copies the selected job's ZPL as shown in the ZPL tab (or exactly as received) to the clipboard. The clipboard
    /// is shared with every other program and can be busy, so a failure is retried once and then explained.
    /// </summary>
    private void CopyZpl(bool original)
    {
        try
        {
            if (_shownJob is null)
            {
                ShowMessage(UiText.Get("Ui_NothingToCopyZpl"));
                return;
            }
            var text = original ? _shownJob.Job.Zpl : _shownZpl;
            if (text.Length == 0)
            {
                ShowMessage(UiText.Get("Ui_NoZplText"));
                return;
            }

            // Windows programs expect CRLF. Only a bare LF is converted, so the original text is not altered
            // where it already used CRLF (or a lone CR) and no "\r\r\n" is produced.
            var clipboardText = BareLineFeed.Replace(text, "\r\n");

            if (!TrySetClipboardText(clipboardText))
            {
                ShowMessage(UiText.Get("Ui_ClipboardBusy"));
                return;
            }
            ShowMessage(original ? UiText.Get("Ui_OriginalCopied") : UiText.Get("Ui_ZplCopied", text.Count(c => c == '\n') + 1));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not copy the ZPL");
            ShowMessage(UiText.Get("Ui_CopyFailed"));
        }
    }

    private static readonly System.Text.RegularExpressions.Regex BareLineFeed = new(@"(?<!\r)\n");

    /// <summary>Sets the clipboard text; when another program holds the clipboard it waits a moment and tries once more.</summary>
    private static bool TrySetClipboardText(string text)
    {
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch (System.Runtime.InteropServices.COMException ex)
            {
                Log.Warning(ex, "Clipboard was busy (attempt {Attempt})", attempt);
                if (attempt == 1) Thread.Sleep(100);
            }
        }
        return false;
    }

    /// <summary>
    /// "Clear jobs": asks, then empties the list. Printer memory is not touched (Clear printer memory does that), and
    /// labels still arriving on an open connection start a new job.
    /// </summary>
    private void OnClearJobs(object sender, RoutedEventArgs e)
    {
        if (_jobs.Count == 0) return;
        var answer = MessageBox.Show(this, UiText.Get(_jobs.Count == 1 ? "Ui_ClearJobsAskOne" : "Ui_ClearJobsAsk", _jobs.Count), UiText.Get("Ui_ClearJobs"),
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;
        _assembler.Clear();
        _jobs.Clear();
        Log.Information("Job list cleared by the user");
        ScheduleSave();
    }

    /// <summary>Runs on any thread: schedules one UI update of the memory text, never throws, never blocks.</summary>
    private void OnMemoryChanged(object? sender, EventArgs e)
    {
        try
        {
            if (_closing || Dispatcher.HasShutdownStarted) return;
            if (Interlocked.Exchange(ref _memoryUpdateScheduled, 1) == 0)
                Dispatcher.BeginInvoke(() =>
                {
                    // Reset first so a change that arrives during the update schedules the next one.
                    Interlocked.Exchange(ref _memoryUpdateScheduled, 0);
                    if (!_closing) UpdateMemoryText();
                });
        }
        catch (Exception ex) { Log.Warning(ex, "Could not schedule the printer-memory text update"); }
    }

    /// <summary>Asks, then empties printer memory. Labels that need a cleared graphic will warn until it is sent again.</summary>
    private void OnClearMemory(object sender, RoutedEventArgs e)
    {
        // Always asks, even when memory looks empty: a download or ^CW can arrive at any moment, so an "already
        // empty" answer could be stale by the time it is read, and clearing an empty memory does no harm.
        // No numbers in the question for the same reason.
        var answer = MessageBox.Show(this, UiText.Get("Ui_ClearMemoryAsk"), UiText.Get("Ui_ClearMemory"),
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;
        _memory.Clear();
        Log.Information("Printer memory cleared by the user");
        ShowMessage(UiText.Get("Ui_MemoryCleared"));
    }

    /// <summary>Shows what printer memory holds; runs on the UI thread.</summary>
    private void UpdateMemoryText()
    {
        var summary = _memory.Summary;
        MemoryText.Text = StatusText.ForMemoryLine(summary);
        MemoryText.ToolTip = UiText.Get("Ui_MemoryTip", summary.Describe());
    }

    private void OnOpenLog(object sender, RoutedEventArgs e) => Open(ResolveLogFolder());

    private void Open(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not open {Path}", path);
            ShowMessage(UiText.Get("Ui_OpenPathFailed", path));
        }
    }

    /// <summary>Keeps a start-up note; later notes are added after earlier ones instead of replacing them.</summary>
    private void AddStartupNote(string text)
    {
        _startupNotes.Add(text);
        RefreshMessageText();
    }

    /// <summary>Shows the result of the latest action after the start-up notes.</summary>
    private void ShowMessage(string text)
    {
        _actionMessage = text;
        RefreshMessageText();
    }

    /// <summary>Shows the start-up notes and the latest action result in the message strip; hides it when empty.</summary>
    private void RefreshMessageText()
    {
        MessageText.Text = string.Join(" ", _startupNotes.Append(_actionMessage).Where(s => s.Length > 0));
        MessageBar.Visibility = MessageText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- Updates ---------------------------------------------------------------------------------------

    /// <summary>The newer release found by the last check; null when none.</summary>
    private UpdateInfo? _update;

    /// <summary>The version of this running program (the part before any "+commit" suffix).</summary>
    private static Version InstalledVersion()
    {
        var text = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        return UpdateChecker.TryParseVersion(text.Split('+')[0], out var v) ? v : new Version(0, 0);
    }

    private async void OnCheckForUpdates(object sender, RoutedEventArgs e) => await CheckForUpdatesAsync(userAsked: true);

    /// <summary>
    /// Looks for a newer version. At start-up (userAsked false) problems are only logged, because a PC without
    /// internet is normal for this tool; when the user pressed the button they get a plain answer either way.
    /// </summary>
    /// <returns>The answer shown in the message strip ("" when nothing was shown), so Printer setup can show it too.</returns>
    private async Task<string> CheckForUpdatesAsync(bool userAsked)
    {
        var answer = "";
        void Say(string text)
        {
            answer = text;
            ShowMessage(text);
        }
        try
        {
            CheckUpdateButton.IsEnabled = false;
            var result = await new UpdateChecker().CheckAsync(InstalledVersion());
            if (result.Update is { } update)
            {
                _update = update;
                UpdateButton.Content = UiText.Get("Ui_UpdateTo", update.Version.ToString(3));
                UpdateButton.Visibility = Visibility.Visible;
                Say(UiText.Get("Ui_UpdateAvailable", update.Version.ToString(3)));
                Log.Information("Update available: {Version}", update.Version);
                MeasureFullToolbar(); // a new button in the toolbar
            }
            else if (result.Problem is not null)
            {
                Log.Information("Update check: {Problem}", result.Problem);
                if (userAsked) Say(result.Problem);
            }
            else if (userAsked)
            {
                Say(UiText.Get("Ui_UpToDate", InstalledVersion().ToString(3)));
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Update check failed");
            if (userAsked) Say(UiText.Get("Ui_UpdateCheckFailed"));
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
        }
        return answer;
    }

    private async void OnUpdateNow(object sender, RoutedEventArgs e)
    {
        if (_update is not { } update) return;
        var answer = MessageBox.Show(this, UiText.Get("Ui_UpdateAsk", update.Version.ToString(3)), UiText.Get("Ui_UpdateTitle"),
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        UpdateButton.IsEnabled = false;
        try
        {
            var progress = new Progress<int>(p => ShowMessage(UiText.Get("Ui_Downloading", p)));
            var file = await UpdateInstaller.DownloadAsync(update, progress, CancellationToken.None);
            ShowMessage(UiText.Get("Ui_Installing"));
            Log.Information("Starting update installer for {Version}", update.Version);
            UpdateInstaller.Launch(file);
            Close(); // the installer waits for LabelScope to end, then replaces the files
        }
        catch (InvalidOperationException ex)
        {
            // UpdateInstaller's messages are written for the user in the window language.
            Log.Warning("Update failed: {Message}", ex.Message);
            ShowMessage(ex.Message);
            UpdateButton.IsEnabled = true;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // Raised when Windows (or antivirus) refuses to start the downloaded installer.
            Log.Warning(ex, "Installer could not be started");
            ShowMessage(UiText.Get("Ui_InstallerBlocked"));
            UpdateButton.IsEnabled = true;
        }
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // Order matters: stop new events reaching us first, then stop the listener, then save, then close the log.
        // finally: the log must be flushed even if disposing the listener fails.
        _closing = true;
        if (_memoryChanged is not null) _memory.Changed -= _memoryChanged;
        try
        {
            if (_listener is not null)
            {
                _listener.LabelReceived -= OnLabelReceived;
                _listener.ProblemReported -= OnProblemReported;
                _listener.ConnectionClosed -= OnConnectionClosed;
                _listener.Dispose();
            }
            SaveJobsOnClose();
            Log.Information("LabelScope stopped");
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    /// <summary>
    /// Writes the job list one last time when "Keep jobs" is on, waiting briefly for a save that is still running so
    /// the two never write at once. A failure is only logged: the window is closing.
    /// </summary>
    private void SaveJobsOnClose()
    {
        _saveTimer.Stop();
        if (!_settings.KeepJobs) return;
        // Closed while the kept jobs were still being read or drawn: the list does not hold them yet, so writing it
        // would lose them. The file stays exactly as it was.
        if (_keptJobs == KeptJobsState.Loading)
        {
            Log.Information("Kept jobs on close: not saved, the kept jobs were still loading");
            return;
        }
        if (!_saveLock.Wait(TimeSpan.FromSeconds(5))) return;
        try
        {
            if (_keptJobs == KeptJobsState.NotRead)
            {
                _store.Load(); // so a newer version's file is recognised and left alone
                _keptJobs = KeptJobsState.Ready;
            }
            var result = _store.Save(_jobs.Select(j => j.Job).ToList(), _settings.HistoryLimit);
            Log.Information("Kept jobs on close: {Success} {Message}", result.Success, result.Message);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Saving the job list on close failed");
        }
        finally { _saveLock.Release(); }
    }
}
