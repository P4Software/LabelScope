using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Imaging;
using LabelScope.Core;
using System.Reflection;
using LabelScope.Core.Listening;
using LabelScope.Core.Updating;
using LabelScope.Core.Printing;
using LabelScope.Core.Rendering;
using LabelScope.Core.Settings;
using Microsoft.Win32;
using Serilog;

namespace LabelScope.App;

/// <summary>The single window: history, label picture and ZPL side by side.</summary>
public partial class MainWindow : Window
{
    private readonly string _settingsPath = Path.Combine(AppContext.BaseDirectory, "settings.json");
    private readonly ObservableCollection<LabelEntry> _history = new();
    private readonly ZplRenderer _renderer = new();
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
        // The grid cell size depends on how large the picture is shown, which changes with zoom and window size.
        LabelImage.SizeChanged += (_, _) => UpdateGrid();
        HistoryList.ItemsSource = _history;
        Activated += OnActivated;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var load = new SettingsStore().LoadOrCreate(_settingsPath);
            _settings = load.Settings;
            ConfigureLogging();
            // Only the starting state comes from settings.json; later clicks are never written back, because
            // rewriting the file would destroy the comments the user may have added.
            GridBox.IsChecked = _settings.ShowGrid;
            StackedBox.IsChecked = _settings.StackedLayout;
            ApplyView();
            foreach (var message in load.Messages) Log.Information("Settings: {Message}", message);
            foreach (var message in load.Messages) AddStartupNote(message);

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
            ListeningText.Text = $"Listening on {_settings.ListenAddress}:{_settings.ListenPort}";
            if (_settings.ListenAddress == "0.0.0.0")
                AddStartupNote("LabelScope accepts labels from other computers on your network (ListenAddress 0.0.0.0). " +
                               "Windows may ask you to allow LabelScope through the firewall; answering that needs an administrator.");
        }
        catch (ListenerStartException ex)
        {
            ListeningText.Text = "NOT listening";
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
            var options = new RenderOptions(_settings.DefaultDpi, _settings.DefaultLabelWidthMm, _settings.DefaultLabelHeightMm);
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
                // Without any ^XA the data holds no label at all; with one, the label exists but could not be drawn.
                var hasStart = formatted.Contains("^XA", StringComparison.OrdinalIgnoreCase);
                entries.Add(new LabelEntry(received.ReceivedAt, received.Source, formatted, received.Zpl, received.Complete,
                    PlaceholderLabel.Create(hasStart ? PlaceholderLabel.NotDrawnText : PlaceholderLabel.Text),
                    result.Warnings, hasStart ? PlaceholderLabel.NotDrawnTitle : PlaceholderLabel.NotFoundTitle));
                note = hasStart
                    ? "Data arrived but no label picture could be drawn from it. The ZPL text and the reasons are shown on the right."
                    : "Data arrived but it contained no label (^XA ... ^XZ). The ZPL text is shown on the right.";
            }
            else if (!received.Complete)
            {
                // Only claimed when a picture really was made.
                note = "A label arrived incomplete (no ^XZ at the end). It is shown as far as it arrived.";
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
            var followNewest = HistoryList.SelectedIndex <= 0;
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

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        try
        {
            // Drop the previous picture first so at most one decoded bitmap is alive.
            _currentImage = null;
            LabelImage.Source = null;

            if (HistoryList.SelectedItem is not LabelEntry entry)
            {
                ZplBox.Text = "";
                LineNumbers.Text = "";
                CopyZplButton.IsEnabled = false;
                CopyOriginalButton.IsEnabled = false;
                InfoText.Text = "";
                WarningList.ItemsSource = null;
                UpdateGrid();
                return;
            }

            _currentImage = entry.CreateImage();
            LabelImage.Source = _currentImage;
            InfoText.Text = entry.Info;
            ZplBox.Text = entry.Zpl;
            CopyZplButton.IsEnabled = true;
            CopyOriginalButton.IsEnabled = true;
            LineNumbers.Text = string.Join("\n", Enumerable.Range(1, entry.Zpl.Split('\n').Length));
            WarningList.ItemsSource = entry.Warnings;
            OnZoomChanged(sender, e);
            UpdateGrid();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not show the selected label");
            ShowMessage("This label could not be shown. Details are in the log file.");
        }
    }

    /// <summary>Moving the slider means the user wants a manual zoom, so "Fit to window" is switched off.</summary>
    private void OnZoomSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded) return; // fires while the window is being built
        if (FitBox.IsChecked == true) FitBox.IsChecked = false; // triggers OnZoomChanged through Unchecked
        else OnZoomChanged(sender, e);
    }

    private void OnZoomChanged(object sender, RoutedEventArgs e)
    {
        if (LabelImage is null || FitBox is null || ZoomSlider is null || ImageScroll is null) return; // fires during InitializeComponent
        var src = LabelImage.Source as BitmapSource;
        if (FitBox.IsChecked == true)
        {
            // Scrollbars off: a ScrollViewer with scrollbars measures its child without a size limit,
            // so the image would stay at native size instead of fitting the viewport.
            ImageScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            ImageScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            LabelImage.Stretch = System.Windows.Media.Stretch.Uniform;
            LabelImage.Width = double.NaN;
            LabelImage.Height = double.NaN;
            // Shrinking with nearest-neighbour makes thin barcode bars vanish; smooth scaling keeps them visible.
            var shrinking = src is not null && (src.PixelWidth > ImageScroll.ActualWidth || src.PixelHeight > ImageScroll.ActualHeight);
            System.Windows.Media.RenderOptions.SetBitmapScalingMode(LabelImage, shrinking ? System.Windows.Media.BitmapScalingMode.HighQuality : System.Windows.Media.BitmapScalingMode.NearestNeighbor);
        }
        else
        {
            ImageScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            ImageScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            if (src is null) return;
            LabelImage.Stretch = System.Windows.Media.Stretch.Fill;
            LabelImage.Width = src.PixelWidth * ZoomSlider.Value;
            LabelImage.Height = src.PixelHeight * ZoomSlider.Value;
            // Sharp pixels when enlarging, smooth when reducing.
            System.Windows.Media.RenderOptions.SetBitmapScalingMode(LabelImage, ZoomSlider.Value >= 1 ? System.Windows.Media.BitmapScalingMode.NearestNeighbor : System.Windows.Media.BitmapScalingMode.HighQuality);
        }
    }

    /// <summary>A view box was clicked (or set from settings): apply both options.</summary>
    private void OnViewOptionChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return; // the boxes are set from settings.json in OnLoaded, which calls ApplyView itself
        ApplyView();
    }

    private void ApplyView()
    {
        ApplyLayout(StackedBox.IsChecked == true);
        UpdateGrid();
    }

    /// <summary>
    /// Arranges picture, splitter and ZPL pane side by side (default) or on top of each other. The same three
    /// elements are moved between a column layout and a row layout, so nothing is created twice and the selected
    /// label, zoom and scroll positions are kept.
    /// </summary>
    private void ApplyLayout(bool stacked)
    {
        PreviewGrid.ColumnDefinitions.Clear();
        PreviewGrid.RowDefinitions.Clear();

        if (stacked)
        {
            PreviewGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 120 });
            PreviewGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(5) });
            PreviewGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 120 });
            Place(PicturePane, row: 0, column: 0);
            Place(PreviewSplitter, row: 1, column: 0);
            Place(ZplPane, row: 2, column: 0);
            PreviewSplitter.Width = double.NaN;
            PreviewSplitter.Height = 5;
            PreviewSplitter.HorizontalAlignment = HorizontalAlignment.Stretch;
            PreviewSplitter.VerticalAlignment = VerticalAlignment.Center;
            PreviewSplitter.ResizeDirection = GridResizeDirection.Rows;
        }
        else
        {
            PreviewGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 200 });
            PreviewGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5) });
            PreviewGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 200 });
            Place(PicturePane, row: 0, column: 0);
            Place(PreviewSplitter, row: 0, column: 1);
            Place(ZplPane, row: 0, column: 2);
            PreviewSplitter.Height = double.NaN;
            PreviewSplitter.Width = 5;
            PreviewSplitter.HorizontalAlignment = HorizontalAlignment.Stretch;
            PreviewSplitter.VerticalAlignment = VerticalAlignment.Stretch;
            PreviewSplitter.ResizeDirection = GridResizeDirection.Columns;
        }
        OnZoomChanged(this, new RoutedEventArgs()); // "fit to window" depends on the space the picture now has
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
    /// The ZPL text box has its own inner scroller that swallows the wheel; forward it to the outer
    /// viewer that really scrolls the text together with the line numbers.
    /// </summary>
    private void OnZplMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        ZplScroll.ScrollToVerticalOffset(ZplScroll.VerticalOffset - e.Delta);
        e.Handled = true;
    }

    /// <summary>Clicking a warning selects the offending line in the ZPL and scrolls to it.</summary>
    private void OnWarningSelected(object sender, SelectionChangedEventArgs e)
    {
        if (WarningList.SelectedItem is not RenderWarning warning) return;
        var index = Math.Clamp(warning.Line - 1, 0, Math.Max(0, ZplBox.LineCount - 1));
        var start = ZplBox.GetCharacterIndexFromLineIndex(index);
        var length = ZplBox.GetLineLength(index);
        // No Focus(): focus stays on the warning list. The inactive-selection highlight keeps the line visible.
        ZplBox.Select(start, length);
        ZplBox.ScrollToLine(index);
        // The text box does not scroll itself (the outer ScrollViewer does), so scroll that one by line height.
        var top = index * (ZplBox.ActualHeight / Math.Max(1, ZplBox.LineCount));
        ZplScroll.ScrollToVerticalOffset(Math.Max(0, top - 40));
        // Clear the selection (the handler returns on null) so clicking the same warning again scrolls again.
        WarningList.SelectedItem = null;
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
            PrinterText.Text = "Printer: settings need fixing";
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
        InstallButton.IsEnabled = enabled;
        RemoveButton.IsEnabled = enabled;
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
            var status = await _installer.GetStatusAsync();
            PrinterText.Text = status switch
            {
                PrinterStatus.Installed => $"Printer \"{_settings.PrinterName}\": installed",
                PrinterStatus.NotInstalled => "Printer: not installed (press \"Install printer\")",
                PrinterStatus.NameTakenByOther => $"Printer \"{_settings.PrinterName}\": name used by another printer",
                _ => "Printer: status could not be checked",
            };
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Printer status check failed");
            PrinterText.Text = "Printer: status could not be checked";
        }
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

    private void RefreshMessageText() =>
        MessageText.Text = string.Join(" ", _startupNotes.Append(_actionMessage).Where(s => s.Length > 0));

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
    private async Task CheckForUpdatesAsync(bool userAsked)
    {
        try
        {
            CheckUpdateButton.IsEnabled = false;
            var result = await new UpdateChecker().CheckAsync(InstalledVersion());
            if (result.Update is { } update)
            {
                _update = update;
                UpdateButton.Content = $"Update to {update.Version.ToString(3)}";
                UpdateButton.Visibility = Visibility.Visible;
                ShowMessage($"A new version of LabelScope is available ({update.Version.ToString(3)}). Press \"Update to {update.Version.ToString(3)}\" in the toolbar to install it.");
                Log.Information("Update available: {Version}", update.Version);
            }
            else if (result.Problem is not null)
            {
                Log.Information("Update check: {Problem}", result.Problem);
                if (userAsked) ShowMessage(result.Problem);
            }
            else if (userAsked)
            {
                ShowMessage($"You have the newest version ({InstalledVersion().ToString(3)}).");
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Update check failed");
            if (userAsked) ShowMessage("LabelScope could not check for updates: " + ex.Message);
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
        }
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
