using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Imaging;
using LabelScope.App.Localization;
using LabelScope.Core;
using LabelScope.Core.Localization;
using System.Reflection;
using LabelScope.Core.Fonts;
using LabelScope.Core.Listening;
using LabelScope.Core.Memory;
using LabelScope.Core.Updating;
using LabelScope.Core.Printing;
using LabelScope.Core.Rendering;
using LabelScope.Core.Settings;
using Microsoft.Win32;
using Serilog;

namespace LabelScope.App;

/// <summary>The single window: print jobs on the left, the label in the centre, its ZPL, fields and log on the right.</summary>
public partial class MainWindow : Window
{
    private readonly string _settingsPath = Path.Combine(AppContext.BaseDirectory, "settings.json");
    private readonly ObservableCollection<LabelEntry> _history = new();
    // One printer memory for the whole session: a graphic downloaded in one job is used by labels in later jobs,
    // as on a real printer. Nothing is saved when LabelScope closes.
    private readonly PrinterMemory _memory = new();

    // Rebuilt once in OnLoaded with the fonts from the FontsFolder setting, before the listener starts.
    private ZplRenderer _renderer;

    // Kept in a field so OnClosing can unsubscribe it; _memoryUpdateScheduled coalesces a burst of Changed events
    // (a download with hundreds of graphics) into one UI update.
    private EventHandler? _memoryChanged;
    private int _memoryUpdateScheduled;
    private AppSettings _settings = new();
    private ZplListener? _listener;
    private PrinterInstaller? _installer;

    // Only the picture of the selected entry is decoded; it is dropped as soon as the selection changes.
    private BitmapImage? _currentImage;

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

    // Zoom: "fit" follows the window size; otherwise _zoom is the screen size of one label dot (1 = 100 %).
    private bool _fit = true;
    private double _zoom = 1;
    private static readonly double[] ZoomSteps = [0.1, 0.25, 0.33, 0.5, 0.67, 0.75, 1, 1.5, 2, 3, 4];

    // Largest file "Open ZPL file" accepts: the same limit as one job sent over the network.
    private const long MaxOpenFileBytes = 16L * 1024 * 1024;

    /// <summary>One row of the Log tab: a warning with its line caption already in the window language.</summary>
    private sealed record WarningRow(int Line, string LineText, string Message);

    // Back-pressure design. Rendering is the expensive step and runs on the socket threads, so at most two
    // labels are rendered at the same time (a socket thread simply waits its turn; TCP slows the sender down).
    // Finished results then wait in a queue for the UI thread. That queue is bounded: if senders are faster
    // than the window can show labels, the oldest waiting results are dropped from the display (they stay
    // in the log) and the user is told once, instead of the dispatcher queue growing without limit.
    private readonly SemaphoreSlim _renderGate = new(2);
    private readonly ConcurrentQueue<PendingResult> _pendingForUi = new();
    private const int MaxPendingForUi = 20;
    private int _drainScheduled; // 1 while a drain is already queued on the dispatcher

    /// <summary>Entries of one received job that are waiting to be shown, plus the status note to show with them (or null).</summary>
    private sealed record PendingResult(List<LabelEntry> Entries, string? Note);

    /// <summary>Creates the window; real startup work happens in <see cref="OnLoaded"/>.</summary>
    public MainWindow()
    {
        InitializeComponent();
        _renderer = new ZplRenderer(_memory);
        // Changed is raised on whichever thread stored the object (a socket thread); the text belongs to the UI thread.
        // BeginInvoke never blocks the renderer, and a failure here must not reach the render thread.
        _memoryChanged = OnMemoryChanged;
        _memory.Changed += _memoryChanged;
        // The grid cell size depends on how large the picture is shown, which changes with zoom and window size.
        LabelImage.SizeChanged += (_, _) => UpdateGrid();
        HistoryList.ItemsSource = _history;
        // The "received today" count follows every insert, trim and clear.
        _history.CollectionChanged += (_, _) => UpdateReceivedToday();
        ZplView.LineClicked += OnZplLineClicked;
        Activated += OnActivated;
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
            ShowSelected();
            foreach (var message in load.Messages) Log.Information("Settings: {Message}", message);
            foreach (var message in load.Messages) AddStartupNote(message);

            // The font list is read once, here, and the same library and memory serve every job. The renderer is
            // replaced before the listener starts, so no job can ever see the old one.
            var fontMessages = new List<string>();
            var fonts = FontLibrary.FromFolder(_settings.FontsFolder, AppContext.BaseDirectory, fontMessages);
            foreach (var message in fontMessages) Log.Information("Fonts: {Message}", message);
            foreach (var message in fontMessages) AddStartupNote(message);
            if (fonts.Count > 0) Log.Information("Fonts: {Count} font file(s) found in the FontsFolder", fonts.Count);
            _renderer = new ZplRenderer(_memory, fonts);
            UpdateMemoryText();
            UpdateSizePicker();

            StartListener();
            CreateInstaller();
            await RefreshPrinterStatusAsync();
            if (_settings.CheckForUpdates) await CheckForUpdatesAsync(userAsked: false);
        }
        catch (Exception ex)
        {
            // async void: an exception here would otherwise reach the global handler without context.
            Log.Error(ex, "Startup failed");
            AddStartupNote("LabelScope could not finish starting. " + ex.Message);
        }
    }

    private void ConfigureLogging()
    {
        try
        {
            var folder = ResolveLogFolder();
            Directory.CreateDirectory(folder);
            Log.Logger = new LoggerConfiguration()
                .WriteTo.File(Path.Combine(folder, "labelscope-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14)
                .CreateLogger();
            Log.Information("LabelScope started");
        }
        catch (Exception ex)
        {
            // A log folder that cannot be created must not stop the program; it just runs without a log.
            AddStartupNote($"LabelScope could not create its log folder ({ex.Message}). It keeps working without a log file.");
        }
    }

    private string ResolveLogFolder() =>
        Path.IsPathRooted(_settings.LogFolder) ? _settings.LogFolder : Path.Combine(AppContext.BaseDirectory, _settings.LogFolder);

    private void StartListener()
    {
        try
        {
            _listener = new ZplListener(IPAddress.Parse(_settings.ListenAddress), _settings.ListenPort);
            _listener.LabelReceived += OnLabelReceived;
            _listener.ProblemReported += OnProblemReported;
            _listener.Start();
            _listening = true;
            UpdateStatusLine();
            if (_settings.ListenAddress == "0.0.0.0")
                AddStartupNote("LabelScope accepts labels from other computers on your network (ListenAddress 0.0.0.0). " +
                               "Windows may ask you to allow LabelScope through the firewall; answering that needs an administrator.");
        }
        catch (ListenerStartException ex)
        {
            _listening = false;
            UpdateStatusLine();
            Log.Warning(ex, "Listener could not start");
            // Not a crash: the window stays open so the message can be read.
            MessageBox.Show(ex.Message, "LabelScope", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// Runs on a socket thread: render there, then hand the finished entries to the UI thread.
    /// Core swallows exceptions from subscribers without a trace, so everything is caught and logged here.
    /// </summary>
    private void OnLabelReceived(ReceivedLabel received)
    {
        try
        {
            if (_closing) return;
            // One read of the field: Printer setup may replace the settings object while this socket thread runs.
            var settings = _settings;
            var options = new RenderOptions(settings.DefaultDpi, settings.LabelWidthMm, settings.LabelHeightMm);
            // Format first and draw THE FORMATTED TEXT: warnings carry line numbers, and they must point at the
            // lines the user sees. Formatting does not change the picture (ZPL ignores line breaks).
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
            Log.Information("Label received from {Source}: {Labels} image(s), {Warnings} warning(s), complete={Complete}",
                received.Source, result.Labels.Count, result.Warnings.Count, received.Complete);

            var entries = result.Labels
                .Select(l => new LabelEntry(received.ReceivedAt, received.Source, formatted, received.Zpl, received.Complete, l, result.Warnings))
                .ToList();
            string? note = null;

            if (entries.Count == 0)
            {
                // Never drop a job silently: show the text and the warnings so the user can see what arrived.
                // Without any ^XA the data holds no label; a download (~DG, ~DY) is then a "stored in memory" job,
                // which is a success and not a mistake. With a ^XA the label exists but could not be drawn.
                var hasStart = formatted.Contains("^XA", StringComparison.OrdinalIgnoreCase);
                var storedOnly = !hasStart && result.MemoryNotes.Count > 0;
                var (text, title) = storedOnly ? (PlaceholderLabel.StoredText, PlaceholderLabel.StoredTitle)
                    : hasStart ? (PlaceholderLabel.NotDrawnText, PlaceholderLabel.NotDrawnTitle)
                    : (PlaceholderLabel.Text, PlaceholderLabel.NotFoundTitle);
                entries.Add(new LabelEntry(received.ReceivedAt, received.Source, formatted, received.Zpl, received.Complete,
                    PlaceholderLabel.Create(text), result.Warnings, title));
                note = storedOnly ? StatusText.ForMemoryNotes(result.MemoryNotes)
                    : hasStart ? "Data arrived but no label picture could be drawn from it. The ZPL text and the reasons are shown on the right."
                    : StatusText.ForNoLabel(result.Warnings.Count > 0);
            }
            else if (!received.Complete)
            {
                // Only claimed when a picture really was made.
                note = "A label arrived incomplete (no ^XZ at the end). It is shown as far as it arrived."
                    + (StatusText.ForMemoryNotes(result.MemoryNotes) is { } stored ? " " + stored : "");
            }
            else
            {
                // A label job that also stored or deleted objects (for example ^IS) says so.
                note = StatusText.ForMemoryNotes(result.MemoryNotes);
            }

            // BeginInvoke, never Invoke: a closing window must not be able to block the socket thread.
            if (Dispatcher.HasShutdownStarted) return;
            QueueForUi(new PendingResult(entries, note));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not process a received label");
            ReportOnUi("A label arrived but LabelScope could not show it. Send it again; details are in the log file.");
        }
    }

    /// <summary>
    /// Runs on a socket thread: parks a finished result for the UI thread and drops the oldest waiting
    /// results when more than <see cref="MaxPendingForUi"/> are waiting.
    /// </summary>
    private void QueueForUi(PendingResult result)
    {
        _pendingForUi.Enqueue(result);

        var skipped = 0;
        while (_pendingForUi.Count > MaxPendingForUi && _pendingForUi.TryDequeue(out var dropped))
            skipped += Math.Max(1, dropped.Entries.Count);
        if (skipped > 0)
        {
            Log.Warning("Skipped showing {Skipped} label(s) because they arrived faster than they can be displayed", skipped);
            ReportOnUi($"Skipped showing {skipped} labels because they arrived faster than they can be displayed. They are listed in the log file.");
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
        while (_pendingForUi.TryDequeue(out var pending))
            AddEntries(pending.Entries, pending.Note);
    }

    /// <summary>Runs on the UI thread: adds the entries to the history and selects the newest.</summary>
    private void AddEntries(List<LabelEntry> entries, string? note)
    {
        try
        {
            if (_closing) return;
            // Follow the newest label only when the user is already looking at the newest one (or at nothing);
            // otherwise leave their selection alone. The ListBox keeps tracking the selected item through Insert(0).
            // A file the user just opened or pasted is always shown: they asked to see it.
            var followNewest = HistoryList.SelectedIndex <= 0
                || entries.Any(x => x.IsLocal);
            // Insert in reverse so label 1 of a multi-label job ends up above label 2.
            for (var i = entries.Count - 1; i >= 0; i--) _history.Insert(0, entries[i]);
            TrimHistory();
            if (entries.Count > 0 && followNewest) HistoryList.SelectedIndex = 0;
            if (note is not null) ShowMessage(note);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not add a label to the history");
            ShowMessage("A label arrived but LabelScope could not show it. Details are in the log file.");
        }
    }

    /// <summary>
    /// Drops the oldest entries until both limits hold: HistoryLimit entries at most, and 256 MB of kept data
    /// at most (see <see cref="HistoryBudget"/>). The history is newest first, so the oldest are at the end.
    /// </summary>
    private void TrimHistory()
    {
        var keep = HistoryBudget.EntriesToKeep(_history.Select(h => h.ApproximateBytes).ToList(), _settings.HistoryLimit);
        while (_history.Count > keep) _history.RemoveAt(_history.Count - 1);
    }

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

    /// <summary>Shows a status-bar note from any thread, without ever blocking or touching a closing window.</summary>
    private void ReportOnUi(string message)
    {
        if (_closing || Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (!_closing) ShowMessage(message);
        });
    }

    // ---- showing the selected label ---------------------------------------------------------

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => ShowSelected();

    /// <summary>
    /// Shows the selected job everywhere: picture and captions, ZPL tab, Log tab, status bar and window title.
    /// Also called after a language switch, because every one of those texts is in the window language.
    /// </summary>
    private void ShowSelected()
    {
        try
        {
            var entry = HistoryList.SelectedItem as LabelEntry;
            // Only decode again when the job changed; a language switch keeps the picture already shown.
            if (!ReferenceEquals(LabelImage.Tag, entry))
            {
                // Drop the previous picture first so at most one decoded bitmap is alive.
                _currentImage = null;
                LabelImage.Source = null;
                LabelImage.Tag = entry;
                ZplView.Text = entry?.Zpl ?? "";
                if (entry is not null)
                {
                    _currentImage = entry.CreateImage();
                    LabelImage.Source = _currentImage;
                }
                SelectedText.Text = UiText.Get("Ui_SelectedNone");
            }

            CopyZplButton.IsEnabled = entry is not null;
            CopyOriginalButton.IsEnabled = entry is not null;
            LabelFrame.Visibility = entry is null ? Visibility.Collapsed : Visibility.Visible;
            EmptyCanvasText.Visibility = entry is null ? Visibility.Visible : Visibility.Collapsed;
            Title = entry is null ? "LabelScope" : "LabelScope — " + entry.JobName;

            if (entry is null)
            {
                TopCaption.Text = SideCaption.Text = "";
                WarningList.ItemsSource = null;
                LogEmptyText.Text = UiText.Get("Ui_LogNoLabel");
                LogEmptyText.Visibility = Visibility.Visible;
                SelectedText.Text = UiText.Get("Ui_SelectedNone");
                BarLabelText.Text = BarSizeText.Text = BarLinesText.Text = BarWarningsText.Text = ReceivedText.Text = "";
                UpdateGrid();
                UpdateZoomText();
                return;
            }

            TopCaption.Text = entry.WidthCaption;
            SideCaption.Text = entry.HeightCaption;
            // The longer explanation of where the size came from (^PW/^LL or not) stays one hover away.
            TopCaption.ToolTip = SideCaption.ToolTip = entry.Info;

            WarningList.ItemsSource = entry.Warnings
                .Select(w => new WarningRow(w.Line, UiText.Get("Ui_LogLine", w.Line), w.Message)).ToList();
            LogEmptyText.Text = UiText.Get("Ui_LogEmpty");
            LogEmptyText.Visibility = entry.Warnings.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            // A job with several labels gives several entries that share the very same received text.
            var siblings = _history.Where(h => ReferenceEquals(h.OriginalZpl, entry.OriginalZpl)).ToList();
            var page = siblings.IndexOf(entry) + 1;
            BarLabelText.Text = UiText.Get("Ui_BarLabel", Math.Max(1, page), Math.Max(1, siblings.Count));
            BarSizeText.Text = UiText.Get("Ui_BarSize", entry.WidthInches, entry.HeightInches, entry.DisplayDpi) + " · " +
                               UiText.Get("Ui_BarDots", entry.Label.WidthDots, entry.Label.HeightDots);
            BarLinesText.Text = UiText.Get("Ui_BarLines", entry.LineCount);
            BarWarningsText.Text = entry.Warnings.Count == 1 ? UiText.Get("Ui_BarWarningOne")
                : UiText.Get("Ui_BarWarningMany", entry.Warnings.Count);
            BarWarningsText.Style = (Style)FindResource(entry.Warnings.Count == 0 ? "StatusTextSuccess" : "StatusTextError");
            // "Received from This computer at …", but "Pasted at …" for a job that did not arrive over the network.
            ReceivedText.Text = UiText.Get(entry.IsLocal ? "Ui_ReceivedLocal" : "Ui_ReceivedFrom", entry.SourceText, entry.TimeText);

            OnZoomChanged(this, new RoutedEventArgs());
            UpdateGrid();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not show the selected label");
            ShowMessage("This label could not be shown. Details are in the log file.");
        }
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
            ? LabelImage.Width / src.PixelWidth
            : _zoom;

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
            scale = Math.Clamp(Math.Min(availableWidth / src.PixelWidth, availableHeight / src.PixelHeight), 0.02, 8);
        }
        else
        {
            ImageScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            ImageScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            scale = _zoom;
        }

        LabelImage.Width = src.PixelWidth * scale;
        LabelImage.Height = src.PixelHeight * scale;
        // Shrinking with nearest-neighbour makes thin barcode bars vanish; smooth scaling keeps them visible.
        // Enlarging keeps sharp pixels so every dot can be counted.
        System.Windows.Media.RenderOptions.SetBitmapScalingMode(LabelImage,
            scale >= 1 ? System.Windows.Media.BitmapScalingMode.NearestNeighbor : System.Windows.Media.BitmapScalingMode.HighQuality);
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
    /// cell, sized from the print resolution and the current display scale (pixels shown per label dot).
    /// It is a separate element above the picture, so Save PNG (the stored PNG bytes) and Copy image (a bitmap
    /// decoded from those bytes) never contain it.
    /// </summary>
    private void UpdateGrid()
    {
        if (GridOverlay is null || GridBox is null || LabelImage is null) return; // events fire while the window is being built

        if (GridBox.IsChecked != true || LabelImage.Source is not BitmapSource src || src.PixelWidth == 0 || LabelImage.ActualWidth <= 0)
        {
            GridOverlay.Visibility = Visibility.Collapsed;
            return;
        }

        var cell = LabelGrid.CellDots(_settings.DefaultDpi) * (LabelImage.ActualWidth / src.PixelWidth);
        if (cell < 6)
        {
            GridOverlay.Visibility = Visibility.Collapsed; // lines closer than this are just a grey wash
            return;
        }

        // The tile is drawn in a 100 x 100 coordinate space and scaled to 'cell' pixels, so the pen thickness is
        // given in that space: 200 / cell gives about two pixels, of which half is clipped at the tile edge.
        // Types are qualified because the Core namespace also has a RenderOptions class.
        var pen = new System.Windows.Media.Pen(
            new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(90, 0, 120, 255)), 200.0 / cell);
        var lines = new System.Windows.Media.GeometryGroup();
        lines.Children.Add(new System.Windows.Media.LineGeometry(new Point(0, 0), new Point(100, 0)));
        lines.Children.Add(new System.Windows.Media.LineGeometry(new Point(0, 0), new Point(0, 100)));
        GridOverlay.Fill = new System.Windows.Media.DrawingBrush(new System.Windows.Media.GeometryDrawing(null, pen, lines))
        {
            TileMode = System.Windows.Media.TileMode.Tile,
            ViewboxUnits = System.Windows.Media.BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, 100, 100),
            ViewportUnits = System.Windows.Media.BrushMappingMode.Absolute,
            Viewport = new Rect(0, 0, cell, cell),
        };
        GridOverlay.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// A warning in the Log tab was clicked: show the ZPL tab with the line that caused it highlighted.
    /// </summary>
    private void OnWarningSelected(object sender, SelectionChangedEventArgs e)
    {
        if (WarningList.SelectedItem is not WarningRow warning) return;
        DetailTabs.SelectedItem = ZplTab;
        ZplView.HighlightLine(warning.Line);
        SelectedText.Text = UiText.Get("Ui_SelectedWarning", warning.Line, warning.Message.ReplaceLineEndings(" "));
        // Clear the selection (the handler returns on null) so clicking the same warning again works again.
        WarningList.SelectedItem = null;
    }

    /// <summary>A line of the ZPL was clicked: highlight it and name it in the footer.</summary>
    private void OnZplLineClicked(int line)
    {
        ZplView.HighlightLine(line);
        SelectedText.Text = UiText.Get("Ui_SelectedLine", line);
    }

    // ---- window texts, language and status line ------------------------------------------------------

    /// <summary>
    /// Sets every fixed text of the window in the current language. Called once at start and again after a language
    /// switch; texts that depend on the selected job are set by <see cref="ShowSelected"/>.
    /// </summary>
    private void ApplyTexts()
    {
        OpenFileButton.Content = UiText.Get("Ui_OpenFile");
        OpenFileButton.ToolTip = UiText.Get("Ui_OpenFileTip");
        PasteButton.Content = UiText.Get("Ui_PasteZpl");
        PasteButton.ToolTip = UiText.Get("Ui_PasteZplTip");
        CopyZplButton.Content = UiText.Get("Ui_CopyZpl");
        CopyZplButton.ToolTip = UiText.Get("Ui_CopyZplTip");
        SavePngButton.Content = UiText.Get("Ui_SavePng");
        ClearJobsButton.Content = UiText.Get("Ui_ClearJobs");
        ClearJobsButton.ToolTip = UiText.Get("Ui_ClearJobsTip");
        ZoomOutButton.ToolTip = UiText.Get("Ui_ZoomOut");
        ZoomInButton.ToolTip = UiText.Get("Ui_ZoomIn");
        FitButton.ToolTip = UiText.Get("Ui_FitTip");
        SizePicker.ToolTip = UiText.Get("Ui_SizePickerTip");
        LanguagePicker.ToolTip = UiText.Get("Ui_LanguageTip");
        PrinterSetupButton.Content = UiText.Get("Ui_PrinterSetup");
        MoreButton.ToolTip = UiText.Get("Ui_More");

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
        FieldsTab.Header = UiText.Get("Ui_TabFields");
        LogTab.Header = UiText.Get("Ui_TabLog");
        FieldsPlaceholderText.Text = UiText.Get("Ui_FieldsComingSoon");
        EmptyCanvasText.Text = UiText.Get("Ui_EmptyCanvas", _settings.PrinterName);

        UpdateStatusLine();
        UpdateReceivedToday();
        UpdateSizePicker();
        UpdateZoomText();
        UpdateMemoryText(); // the memory line comes from Core, which follows the same language
        MeasureFullToolbar();
    }

    // ---- toolbar fit -------------------------------------------------------------------------------

    // How many buttons of CompactOrder currently show only their icon (0 = every label shown).
    private int _compactCount = -1;

    /// <summary>
    /// The flat buttons that drop their text when the toolbar runs out of room, in the order they do so: the least
    /// used first, "Printer setup" last. "Open ZPL file" always keeps its text.
    /// </summary>
    private Button[] CompactOrder => [ClearJobsButton, SavePngButton, CopyZplButton, PasteButton, PrinterSetupButton];

    /// <summary>Re-checks the toolbar after its texts changed (start, language switch).</summary>
    private void MeasureFullToolbar()
    {
        _compactCount = -1; // forces the texts to be written again in the new language
        UpdateToolbarFit();
    }

    private void OnToolbarSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged) UpdateToolbarFit();
    }

    /// <summary>
    /// Drops button texts one at a time, in <see cref="CompactOrder"/>, until the toolbar fits the window, so a
    /// narrow window keeps as many labels as it can. Each step is measured with unlimited room, because laid-out
    /// sizes are clipped to the window and would always "fit".
    /// </summary>
    private void UpdateToolbarFit()
    {
        if (ToolbarBar is null) return;
        var available = ToolbarBar.ActualWidth - ToolbarBar.Padding.Left - ToolbarBar.Padding.Right - 16; // 16: gap
        var buttons = CompactOrder;
        var count = 0;
        if (available > 0)
        {
            var unlimited = new Size(double.PositiveInfinity, double.PositiveInfinity);
            for (; count <= buttons.Length; count++)
            {
                SetCompactCount(count);
                // A changed button text only reaches the panels' sizes after a layout pass; without it the
                // measure below would return the size from before the change.
                ToolbarBar.UpdateLayout();
                ToolbarLeft.Measure(unlimited);
                ToolbarRight.Measure(unlimited);
                if (ToolbarLeft.DesiredSize.Width + ToolbarRight.DesiredSize.Width <= available) break;
            }
            count = Math.Min(count, buttons.Length);
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
            var text = button == PasteButton ? UiText.Get("Ui_PasteZpl")
                : button == CopyZplButton ? UiText.Get("Ui_CopyZpl")
                : button == SavePngButton ? UiText.Get("Ui_SavePng")
                : button == ClearJobsButton ? UiText.Get("Ui_ClearJobs")
                : UiText.Get("Ui_PrinterSetup");
            var compact = i < count;
            button.Content = compact ? null : text;
            // Without its text the button still needs a name on hover; buttons with their own tip keep it.
            if (button == SavePngButton || button == PrinterSetupButton) button.ToolTip = compact ? text : null;
        }
    }

    /// <summary>Selects the picker entry of the current language without running the switch itself.</summary>
    private void SelectLanguageInPicker()
    {
        var code = Text.Culture.TwoLetterISOLanguageName == "es" ? "es" : "en";
        _changingLanguagePicker = true;
        try { LanguagePicker.SelectedItem = LanguagePicker.Items.OfType<ComboBoxItem>().First(i => (string)i.Tag == code); }
        finally { _changingLanguagePicker = false; }
    }

    // True while code (not the user) sets the language picker, so no switch runs.
    private bool _changingLanguagePicker;

    /// <summary>
    /// English / Español was chosen: switch the language of the window and of Core's messages, then redraw every text.
    /// Warnings of jobs already in the list keep the language they were made in until those jobs are rendered again.
    /// </summary>
    private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_changingLanguagePicker || LanguagePicker.SelectedItem is not ComboBoxItem { Tag: string code }) return;
        if (Text.Culture.TwoLetterISOLanguageName == code) return;
        SwitchLanguage(new System.Globalization.CultureInfo(code));
    }

    /// <summary>
    /// The culture for the Language setting: "en" or "es" as chosen, and for "" the same rule Core uses by default
    /// (Spanish when the Windows display language is Spanish, otherwise English).
    /// </summary>
    private static System.Globalization.CultureInfo CultureForSetting(string? language) =>
        new(language is "en" or "es"
            ? language
            : System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "es" ? "es" : "en");

    /// <summary>
    /// Changes the language of the window and of Core's messages, then redraws every text. Used by the toolbar
    /// language picker and after Printer setup is saved.
    /// </summary>
    private void SwitchLanguage(System.Globalization.CultureInfo culture)
    {
        try
        {
            Text.Culture = culture;
            ApplyTexts();
            // The job cards compute their texts on each read; refreshing the list makes them read again.
            HistoryList.Items.Refresh();
            ShowSelected();
            // The footer named a line in the old language; start fresh rather than leave mixed languages.
            ZplView.HighlightLine(null);
            SelectedText.Text = UiText.Get("Ui_SelectedNone");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not switch the language");
            ShowMessage("The language could not be changed. Details are in the log file.");
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
        var brush = (System.Windows.Media.Brush)FindResource(ok ? "SuccessBrush" : "ErrorBrush");
        StatusLineText.Foreground = brush;
        StatusIcon.Foreground = brush;
        StatusIcon.Text = (string)FindResource(ok ? "IconCheck" : "IconError");
    }

    /// <summary>"N received today" in the Print jobs header.</summary>
    private void UpdateReceivedToday()
    {
        if (ReceivedTodayText is null) return;
        var today = DateTime.Today;
        // Counts jobs, not labels: the labels of one job share the very same received text.
        var jobs = _history.Where(h => h.At.LocalDateTime.Date == today)
                           .Select(h => h.OriginalZpl).Distinct(ReferenceEqualityComparer.Instance).Count();
        ReceivedTodayText.Text = UiText.Get("Ui_ReceivedToday", jobs);
    }

    /// <summary>
    /// The size picker shows the label loaded in the printer (LabelWidthMm × LabelHeightMm, in inches) at the
    /// DefaultDpi setting. Read-only: the size is chosen in Printer setup.
    /// </summary>
    private void UpdateSizePicker()
    {
        if (SizePicker is null) return;
        var text = UiText.Get("Ui_SizePicker",
            LabelSizes.Format(_settings.LabelWidthMm / LabelSizes.MmPerInch),
            LabelSizes.Format(_settings.LabelHeightMm / LabelSizes.MmPerInch), _settings.DefaultDpi);
        SizePicker.ItemsSource = new[] { text };
        SizePicker.SelectedIndex = 0;
    }

    // ---- open and paste --------------------------------------------------------------------------------

    /// <summary>Shows a ZPL file from disk exactly as if it had been printed to LabelScope.</summary>
    private void OnOpenFile(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = UiText.Get("Ui_OpenFileFilter") };
        if (dialog.ShowDialog(this) != true) return;
        string text;
        try
        {
            // Checked before reading so a huge file is never loaded into memory.
            if (new FileInfo(dialog.FileName).Length > MaxOpenFileBytes)
            {
                ShowMessage(UiText.Get("Ui_FileTooLarge"));
                return;
            }
            text = DecodeLikePrinter(File.ReadAllBytes(dialog.FileName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "Could not read {Path}", dialog.FileName);
            ShowMessage(UiText.Get("Ui_FileReadFailed", ex.Message));
            return;
        }
        if (string.IsNullOrWhiteSpace(text))
        {
            ShowMessage(UiText.Get("Ui_FileEmpty"));
            return;
        }
        RenderLocalJob(text, LabelEntry.FileSource);
    }

    /// <summary>
    /// Turns file bytes into text the way the printer listener does (strict UTF-8, otherwise Windows-1252), so a
    /// file opened here and the same file printed to LabelScope give the same label, accented text included.
    /// </summary>
    private static string DecodeLikePrinter(byte[] bytes)
    {
        // A UTF-8 byte-order mark is not part of the ZPL.
        var start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        try
        {
            return new System.Text.UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes, start, bytes.Length - start);
        }
        catch (System.Text.DecoderFallbackException)
        {
            // Not valid UTF-8: older programs send Windows-1252, as the listener assumes too.
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
            return System.Text.Encoding.GetEncoding(1252).GetString(bytes, start, bytes.Length - start);
        }
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
        RenderLocalJob(text, LabelEntry.PastedSource);
    }

    /// <summary>
    /// Renders text that did not come over the network through the very same path as a printed job (render gate,
    /// formatting, placeholder rules, history). That path waits for the gate, so it runs off the UI thread.
    /// </summary>
    private void RenderLocalJob(string zpl, string source)
    {
        // A text without ^XZ at its end is treated like a connection that stopped early, as for printed jobs.
        var complete = zpl.LastIndexOf("^XZ", StringComparison.OrdinalIgnoreCase) >= 0;
        var job = new ReceivedLabel(zpl, DateTimeOffset.Now, source, complete);
        _ = Task.Run(() => OnLabelReceived(job));
    }

    // ---- toolbar menus -----------------------------------------------------------------------------

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
    /// Takes the settings saved by Printer setup into use: language, printer (name and status), and the label size
    /// and density for every label that arrives from now on. Labels already in the list keep how they were drawn
    /// until they are drawn again.
    /// </summary>
    private async void ApplySettings(AppSettings saved)
    {
        try
        {
            var printerChanged = !string.Equals(saved.PrinterName, _settings.PrinterName, StringComparison.Ordinal);
            // The toolbar picker changes the language without saving it. Only a Language setting that was changed
            // in Printer setup switches the window, so saving a new label size never undoes the picker's choice.
            var languageChanged = !string.Equals(saved.Language, _settings.Language, StringComparison.OrdinalIgnoreCase);
            // The dialog saves on top of a fresh read of settings.json, so a hand edit of a key that is only read at
            // start can arrive here. Those keep their running values until the next start: the listener still
            // listens on the old address and port (the printer must keep pointing at it), the log still goes to the
            // old folder, and the fonts were loaded from the old folder.
            saved.ListenAddress = _settings.ListenAddress;
            saved.ListenPort = _settings.ListenPort;
            saved.LogFolder = _settings.LogFolder;
            saved.FontsFolder = _settings.FontsFolder;
            // A single reference swap: the socket thread reads the field once per job (see OnLabelReceived).
            _settings = saved;
            Log.Information("Printer setup saved: printer {Printer}, label {Width} x {Height} mm, {Dpi} dpi, language '{Language}'",
                saved.PrinterName, saved.LabelWidthMm, saved.LabelHeightMm, saved.DefaultDpi, saved.Language);

            var culture = CultureForSetting(saved.Language);
            if (languageChanged && culture.Name != Text.Culture.Name)
            {
                SwitchLanguage(culture);
                SelectLanguageInPicker();
            }
            else
            {
                ApplyTexts(); // printer name, size picker and status line name values that may have changed
            }
            UpdateGrid(); // the grid cell size follows the density

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
            Log.Error(ex, "Applying the Printer setup settings failed");
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
            MessageBox.Show(ex.Message, "LabelScope", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            var ask = MessageBox.Show(
                $"LabelScope will add a Windows printer named \"{_settings.PrinterName}\" that sends labels to this program.\n\n" +
                "Windows will ask for permission once. Continue?",
                "Install printer", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (ask != MessageBoxResult.Yes) return;
            await RunPrinterJobAsync(_installer.InstallAsync, "Install printer");
        }
        catch (Exception ex)
        {
            // async void: nothing may escape.
            Log.Error(ex, "Install printer handler failed");
            ShowMessage("The printer action did not finish. Details are in the log file.");
        }
    }

    private async void OnRemovePrinter(object sender, RoutedEventArgs e)
    {
        if (_installer is null || _printerBusy) return;
        try
        {
            var ask = MessageBox.Show($"Remove the Windows printer \"{_settings.PrinterName}\"?", "Remove printer",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (ask != MessageBoxResult.Yes) return;
            await RunPrinterJobAsync(_installer.RemoveAsync, "Remove printer");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Remove printer handler failed");
            ShowMessage("The printer action did not finish. Details are in the log file.");
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
            MessageBox.Show(result.Message, "LabelScope", MessageBoxButton.OK,
                result.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            // The installer is documented not to throw; this only protects against an unexpected bug.
            Log.Error(ex, "{What} failed unexpectedly", what);
            ShowMessage("The printer action did not finish. Details are in the log file.");
        }
        finally
        {
            _printerBusy = false;
            UpdatePrinterButtons();
        }
        await RefreshPrinterStatusAsync();
    }

    // ---- toolbar actions ---------------------------------------------------------------------

    private void OnSavePng(object sender, RoutedEventArgs e)
    {
        if (HistoryList.SelectedItem is not LabelEntry entry)
        {
            ShowMessage("There is no label to save yet. Send one to LabelScope first.");
            return;
        }
        var dialog = new SaveFileDialog { Filter = "PNG image|*.png", FileName = $"label-{entry.At:yyyyMMdd-HHmmss}.png" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllBytes(dialog.FileName, entry.Label.PngBytes);
            ShowMessage($"Saved {dialog.FileName}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "Could not save {Path}", dialog.FileName);
            ShowMessage($"The image could not be saved to {dialog.FileName}. Choose another folder and try again.");
        }
    }

    private void OnCopyImage(object sender, RoutedEventArgs e)
    {
        if (_currentImage is null)
        {
            ShowMessage("There is no label to copy yet. Send one to LabelScope first.");
            return;
        }
        try
        {
            Clipboard.SetImage(_currentImage);
            ShowMessage("The label image was copied. Paste it into any program.");
        }
        catch (Exception ex)
        {
            // The clipboard can be locked by another program for a moment.
            Log.Warning(ex, "Could not copy the image");
            ShowMessage("The image could not be copied because Windows or another program is using the clipboard. Try again.");
        }
    }

    private void OnCopyZpl(object sender, RoutedEventArgs e) => CopyZpl(original: false);

    private void OnCopyOriginalZpl(object sender, RoutedEventArgs e) => CopyZpl(original: true);

    /// <summary>
    /// Copies the formatted ZPL (or the text exactly as received) to the clipboard. The clipboard is shared with
    /// every other program and can be busy, so a failure is retried once and then explained; nothing escapes.
    /// </summary>
    private void CopyZpl(bool original)
    {
        try
        {
            if (HistoryList.SelectedItem is not LabelEntry entry)
            {
                ShowMessage("There is no ZPL to copy yet. Send a label to LabelScope first.");
                return;
            }
            var text = original ? entry.OriginalZpl : entry.Zpl;
            if (text.Length == 0)
            {
                ShowMessage("This label contains no ZPL text to copy.");
                return;
            }

            // Windows programs expect CRLF. Only a bare LF is converted, so the original text is not altered
            // where it already used CRLF (or a lone CR) and no "\r\r\n" is produced.
            var clipboardText = BareLineFeed.Replace(text, "\r\n");

            if (!TrySetClipboardText(clipboardText))
            {
                ShowMessage("LabelScope could not use the clipboard because another program is using it. Try again.");
                return;
            }
            ShowMessage(original ? "Original ZPL copied." : $"ZPL copied ({text.Count(c => c == '\n') + 1} lines).");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not copy the ZPL");
            ShowMessage("The ZPL could not be copied. Details are in the log file.");
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

    private void OnClearHistory(object sender, RoutedEventArgs e) => _history.Clear();

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
        var answer = MessageBox.Show(this,
            // No numbers in the question: a download can arrive while the dialog is open, so any count could be stale.
            "Delete everything LabelScope keeps in its printer memory (downloaded graphics and fonts, and font letters set with ^CW)?\n\n" +
            "Labels that use these graphics, fonts or font letters will show a warning until the download or ^CW is sent again.",
            "Clear printer memory", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;
        _memory.Clear();
        Log.Information("Printer memory cleared by the user");
        ShowMessage("LabelScope's printer memory was cleared.");
    }

    /// <summary>Shows what printer memory holds; runs on the UI thread.</summary>
    private void UpdateMemoryText()
    {
        var summary = _memory.Summary;
        MemoryText.Text = StatusText.ForMemoryLine(summary);
        MemoryText.ToolTip = "Printer memory: " + summary.Describe() +
            ". Graphics and fonts sent to LabelScope with ~DG, ~DY or ^IS, and font letters set with ^CW, are kept here " +
            "until LabelScope closes or you press \"Clear printer memory\".";
    }

    private void OnOpenSettings(object sender, RoutedEventArgs e) => Open(_settingsPath);

    private void OnOpenLog(object sender, RoutedEventArgs e) => Open(ResolveLogFolder());

    private void Open(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not open {Path}", path);
            ShowMessage($"Windows could not open {path}. Open it yourself in File Explorer.");
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
        var text = System.Reflection.Assembly.GetExecutingAssembly()
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
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
                Say($"A new version of LabelScope is available ({update.Version.ToString(3)}). Press \"Update to {update.Version.ToString(3)}\" in the toolbar to install it.");
                Log.Information("Update available: {Version}", update.Version);
            }
            else if (result.Problem is not null)
            {
                Log.Information("Update check: {Problem}", result.Problem);
                if (userAsked) Say(result.Problem);
            }
            else if (userAsked)
            {
                Say($"You have the newest version ({InstalledVersion().ToString(3)}).");
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Update check failed");
            if (userAsked) Say("LabelScope could not check for updates: " + ex.Message);
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
        var answer = MessageBox.Show(this,
            $"Install LabelScope {update.Version.ToString(3)} now?\n\nLabelScope will close, update itself and open again. Your settings are kept. " +
            "Labels in the list on the left will be cleared.",
            "Update LabelScope", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        UpdateButton.IsEnabled = false;
        try
        {
            var progress = new Progress<int>(p => ShowMessage($"Downloading the new version... {p}%"));
            var file = await UpdateInstaller.DownloadAsync(update, progress, CancellationToken.None);
            ShowMessage("Installing the new version. LabelScope will open again in a moment.");
            Log.Information("Starting update installer for {Version}", update.Version);
            UpdateInstaller.Launch(file);
            Close(); // the installer waits for LabelScope to end, then replaces the files
        }
        catch (InvalidOperationException ex)
        {
            Log.Warning("Update failed: {Message}", ex.Message);
            ShowMessage(ex.Message);
            UpdateButton.IsEnabled = true;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // Raised when Windows (or antivirus) refuses to start the downloaded installer.
            Log.Warning(ex, "Installer could not be started");
            ShowMessage("Windows did not allow the installer to start (" + ex.Message + "). Download LabelScope-Setup.exe from the GitHub releases page and run it yourself.");
            UpdateButton.IsEnabled = true;
        }
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // Order matters: stop new events reaching us first, then stop the listener, then close the log.
        // finally: the log must be flushed even if disposing the listener fails.
        _closing = true;
        if (_memoryChanged is not null) _memory.Changed -= _memoryChanged;
        try
        {
            if (_listener is not null)
            {
                _listener.LabelReceived -= OnLabelReceived;
                _listener.ProblemReported -= OnProblemReported;
                _listener.Dispose();
            }
            Log.Information("LabelScope stopped");
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }
}
