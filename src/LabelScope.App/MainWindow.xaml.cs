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

    /// <summary>Creates the window; real startup work happens in <see cref="OnLoaded"/>.</summary>
    public MainWindow()
    {
        InitializeComponent();
        HistoryList.ItemsSource = _history;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var load = new SettingsStore().LoadOrCreate(_settingsPath);
            _settings = load.Settings;
            ConfigureLogging();
            foreach (var message in load.Messages) Log.Information("Settings: {Message}", message);
            if (load.Messages.Count > 0) ShowMessage(string.Join(" ", load.Messages));

            StartListener();
            CreateInstaller();
            await RefreshPrinterStatusAsync();
        }
        catch (Exception ex)
        {
            // async void: an exception here would otherwise reach the global handler without context.
            Log.Error(ex, "Startup failed");
            ShowMessage("LabelScope could not finish starting. " + ex.Message);
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
            ShowMessage($"LabelScope could not create its log folder ({ex.Message}). It keeps working without a log file.");
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
            var result = _renderer.Render(received.Zpl, options);
            Log.Information("Label received from {Source}: {Labels} image(s), {Warnings} warning(s), complete={Complete}",
                received.Source, result.Labels.Count, result.Warnings.Count, received.Complete);

            var entries = result.Labels
                .Select(l => new LabelEntry(received.ReceivedAt, received.Source, received.Zpl, received.Complete, l, result.Warnings))
                .ToList();

            // BeginInvoke, never Invoke: a closing window must not be able to block the socket thread.
            if (Dispatcher.HasShutdownStarted) return;
            Dispatcher.BeginInvoke(() => AddEntries(entries, received.Complete));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not process a received label");
            ReportOnUi("A label arrived but LabelScope could not show it. Send it again; details are in the log file.");
        }
    }

    /// <summary>Runs on the UI thread: adds the entries to the history and selects the newest.</summary>
    private void AddEntries(List<LabelEntry> entries, bool complete)
    {
        try
        {
            if (_closing) return;
            foreach (var entry in entries) _history.Insert(0, entry);
            while (_history.Count > _settings.HistoryLimit) _history.RemoveAt(_history.Count - 1);
            if (entries.Count > 0) HistoryList.SelectedIndex = 0; // show the newest label straight away
            if (!complete) ShowMessage("A label arrived incomplete (no ^XZ at the end). It is shown as far as it arrived.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not add a label to the history");
            ShowMessage("A label arrived but LabelScope could not show it. Details are in the log file.");
        }
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

    private void OnZoomChanged(object sender, RoutedEventArgs e)
    {
        if (LabelImage is null || FitBox is null || ZoomSlider is null) return; // fires during InitializeComponent
        if (FitBox.IsChecked == true)
        {
            LabelImage.Stretch = System.Windows.Media.Stretch.Uniform;
            LabelImage.Width = double.NaN;
            LabelImage.Height = double.NaN;
        }
        else if (LabelImage.Source is BitmapSource src)
        {
            LabelImage.Stretch = System.Windows.Media.Stretch.Fill;
            LabelImage.Width = src.PixelWidth * ZoomSlider.Value;
            LabelImage.Height = src.PixelHeight * ZoomSlider.Value;
        }
    }

    /// <summary>Clicking a warning selects the offending line in the raw ZPL and scrolls to it.</summary>
    private void OnWarningSelected(object sender, SelectionChangedEventArgs e)
    {
        if (WarningList.SelectedItem is not RenderWarning warning) return;
        var index = Math.Clamp(warning.Line - 1, 0, Math.Max(0, ZplBox.LineCount - 1));
        var start = ZplBox.GetCharacterIndexFromLineIndex(index);
        var length = ZplBox.GetLineLength(index);
        ZplBox.Focus();
        ZplBox.Select(start, length);
        ZplBox.ScrollToLine(index);
        // The text box does not scroll itself (the outer ScrollViewer does), so scroll that one by line height.
        var top = index * (ZplBox.ActualHeight / Math.Max(1, ZplBox.LineCount));
        ZplScroll.ScrollToVerticalOffset(Math.Max(0, top - 40));
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
            SetPrinterButtons(false);
            PrinterText.Text = "Printer: settings need fixing";
            ShowMessage(ex.Message);
            MessageBox.Show(ex.Message, "LabelScope", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Enables or disables both printer buttons together.</summary>
    private void SetPrinterButtons(bool enabled)
    {
        InstallButton.IsEnabled = enabled;
        RemoveButton.IsEnabled = enabled;
    }

    private async Task RefreshPrinterStatusAsync()
    {
        if (_installer is null) return;
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
            // Core advises offering no install/remove action when the status is unknown.
            if (!_printerBusy) SetPrinterButtons(status != PrinterStatus.Unknown);
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
        var ask = MessageBox.Show(
            $"LabelScope will add a Windows printer named \"{_settings.PrinterName}\" that sends labels to this program.\n\n" +
            "Windows will ask for permission once. Continue?",
            "Install printer", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (ask != MessageBoxResult.Yes) return;
        await RunPrinterJobAsync(_installer.InstallAsync, "Install printer");
    }

    private async void OnRemovePrinter(object sender, RoutedEventArgs e)
    {
        if (_installer is null || _printerBusy) return;
        var ask = MessageBox.Show($"Remove the Windows printer \"{_settings.PrinterName}\"?", "Remove printer",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (ask != MessageBoxResult.Yes) return;
        await RunPrinterJobAsync(_installer.RemoveAsync, "Remove printer");
    }

    /// <summary>Runs one install/remove with both buttons off, shows the message the installer returned, then re-checks the status.</summary>
    private async Task RunPrinterJobAsync(Func<CancellationToken, Task<OperationResult>> job, string what)
    {
        _printerBusy = true;
        SetPrinterButtons(false);
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
            SetPrinterButtons(true); // RefreshPrinterStatusAsync turns them off again if the status is unknown
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

    private void ShowMessage(string text) => MessageText.Text = text;

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // Order matters: stop new events reaching us first, then stop the listener, then close the log.
        _closing = true;
        if (_listener is not null)
        {
            _listener.LabelReceived -= OnLabelReceived;
            _listener.ProblemReported -= OnProblemReported;
            _listener.Dispose();
        }
        Log.Information("LabelScope stopped");
        Log.CloseAndFlush();
    }
}
