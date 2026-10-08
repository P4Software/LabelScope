using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using LabelScope.Core;
using LabelScope.Core.Listening;
using LabelScope.Core.Printing;
using LabelScope.Core.Rendering;
using LabelScope.Core.Settings;
using Microsoft.Win32;
using Serilog;

namespace LabelScope.App;

/// <summary>The single window: history, label picture and raw ZPL side by side.</summary>
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
            foreach (var message in load.Messages) Log.Information("Settings: {Message}", message);
            foreach (var message in load.Messages) AddStartupNote(message);

            StartListener();
            CreateInstaller();
            await RefreshPrinterStatusAsync();
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
            RenderResult result;
            _renderGate.Wait();
            try { result = _renderer.Render(received.Zpl, options); }
            finally { _renderGate.Release(); }
            Log.Information("Label received from {Source}: {Labels} image(s), {Warnings} warning(s), complete={Complete}",
                received.Source, result.Labels.Count, result.Warnings.Count, received.Complete);

            var entries = result.Labels
                .Select(l => new LabelEntry(received.ReceivedAt, received.Source, received.Zpl, received.Complete, l, result.Warnings))
                .ToList();
            string? note = null;

            if (entries.Count == 0)
            {
                // Never drop a job silently: show the raw text and the warnings so the user can see what arrived.
                // Without any ^XA the data holds no label at all; with one, the label exists but could not be drawn.
                var hasStart = received.Zpl.Contains("^XA", StringComparison.OrdinalIgnoreCase);
                entries.Add(new LabelEntry(received.ReceivedAt, received.Source, received.Zpl, received.Complete,
                    PlaceholderLabel.Create(), result.Warnings, hasStart ? "Label not drawn" : "No label found"));
                note = hasStart
                    ? "Data arrived but no label picture could be drawn from it. The raw text and the reasons are shown on the right."
                    : "Data arrived but it contained no label (^XA ... ^XZ). The raw text is shown on the right.";
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
                InfoText.Text = "";
                WarningList.ItemsSource = null;
                return;
            }

            _currentImage = entry.CreateImage();
            LabelImage.Source = _currentImage;
            InfoText.Text = entry.Info;
            ZplBox.Text = entry.Zpl;
            LineNumbers.Text = string.Join("\n", Enumerable.Range(1, entry.Zpl.Split('\n').Length));
            WarningList.ItemsSource = entry.Warnings;
            OnZoomChanged(sender, e);
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

    /// <summary>
    /// The ZPL text box has its own inner scroller that swallows the wheel; forward it to the outer
    /// viewer that really scrolls the text together with the line numbers.
    /// </summary>
    private void OnZplMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        ZplScroll.ScrollToVerticalOffset(ZplScroll.VerticalOffset - e.Delta);
        e.Handled = true;
    }

    /// <summary>Clicking a warning selects the offending line in the raw ZPL and scrolls to it.</summary>
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
            _installer = new PrinterInstaller(new PowerShellRunner(), _settings.PrinterName, _settings.ListenPort);
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
