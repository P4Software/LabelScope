using System.ComponentModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using LabelScope.App.Localization;
using LabelScope.Core;
using LabelScope.Core.Printing;
using LabelScope.Core.Settings;
using Serilog;

namespace LabelScope.App;

/// <summary>
/// The Printer setup screen: Windows printer name (with Reinstall printer and a live status line), loaded label size,
/// print density, language and the job-list behaviour. Save checks every value first, writes settings.json through
/// <see cref="SettingsStore.Save"/> and hands the new settings to the caller in <see cref="Result"/>.
/// </summary>
/// <remarks>
/// The caller should apply <see cref="Result"/> whenever it is not null, whatever <see cref="Window.ShowDialog"/>
/// returned: Reinstall printer saves the settings before it touches Windows, so a person who reinstalls and then
/// presses Cancel has still changed settings.json, and the main window must follow the file.
/// </remarks>
public partial class PrinterSetupWindow : Window
{
    /// <summary>The print densities offered as options. Any other value in settings.json (152 dpi) is kept as it is.</summary>
    private static readonly int[] OfferedDpis = [203, 300, 600];

    private readonly AppSettings _current;
    private readonly string _settingsPath;

    // Name of the printer as settings.json last stored it. Reinstall removes the printer under this name when the
    // typed name differs, so renaming never leaves an orphaned LabelScope printer behind in Windows.
    private string _savedPrinterName;

    // True while Save or Reinstall talks to Windows; the window then ignores clicks and refuses to close.
    private bool _busy;

    // Every status check takes a number; an answer that arrives after a newer check started is dropped, so a slow
    // check for an old name can never overwrite the status of the name typed since.
    private int _statusCheck;

    // Waits for a short pause in typing before checking the printer name, so Windows is not asked once per key.
    private readonly DispatcherTimer _nameTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };

    /// <summary>
    /// The settings as saved to settings.json, or null when nothing was saved. Set by Save and also by Reinstall
    /// printer (which saves first), so check it even when the dialog was cancelled.
    /// </summary>
    public AppSettings? Result { get; private set; }

    /// <summary>
    /// "Open settings file" was clicked. The dialog closes right after raising this, so the person cannot press
    /// Save here and overwrite what they type in the file.
    /// </summary>
    public event EventHandler? OpenSettingsFileRequested;

    /// <summary>"Open log folder" was clicked. The dialog stays open.</summary>
    public event EventHandler? OpenLogFolderRequested;

    /// <summary>
    /// Runs the update check when "Check for updates" is clicked and returns the plain-language answer, which the
    /// dialog shows itself (the main window's message strip is hidden behind this modal window). The link is hidden
    /// when this is not set.
    /// </summary>
    public Func<Task<string>>? CheckForUpdates { get; init; }

    /// <summary>Creates the dialog for <paramref name="current"/>; nothing is changed until Save or Reinstall printer.</summary>
    /// <param name="current">The settings in use. They are copied, never changed in place.</param>
    /// <param name="settingsPath">Full path of settings.json, written on Save.</param>
    public PrinterSetupWindow(AppSettings current, string settingsPath)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentException.ThrowIfNullOrEmpty(settingsPath);
        InitializeComponent();
        _current = Copy(current);
        _settingsPath = settingsPath;
        _savedPrinterName = _current.PrinterName;
        _nameTimer.Tick += (_, _) =>
        {
            _nameTimer.Stop();
            _ = RefreshStatusAsync();
        };
        ApplyTexts();
        LoadValues();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        CheckUpdatesHolder.Visibility = CheckForUpdates is null ? Visibility.Collapsed : Visibility.Visible;
        PrinterNameBox.Focus();
        PrinterNameBox.SelectAll();
        _ = RefreshStatusAsync();
    }

    /// <summary>
    /// A full copy of every setting, including the ones this screen does not show (port, fonts folder, history
    /// limit…). Save writes the whole object, so building a fresh one would silently reset those to defaults. A JSON
    /// round trip copies properties added in later versions too, without this method having to know them.
    /// </summary>
    private static AppSettings Copy(AppSettings s) =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(s)) ?? new AppSettings();

    // ---- texts and values ------------------------------------------------------------------------

    /// <summary>Sets every visible text in the current language (Core's Text.Culture).</summary>
    private void ApplyTexts()
    {
        Title = TitleText.Text = SetupText.Get("Setup_WindowTitle");
        SubtitleText.Text = SetupText.Get("Setup_Subtitle");
        WindowsPrinterHeading.Text = SetupText.Get("Setup_WindowsPrinter");
        PrinterNameLabel.Content = SetupText.Get("Setup_PrinterName");
        ReinstallButton.Content = SetupText.Get("Setup_Reinstall");
        ReinstallButton.ToolTip = SetupText.Get("Setup_ReinstallTip");
        LabelHeading.Text = SetupText.Get("Setup_Label");
        LabelSizeLabel.Content = SetupText.Get("Setup_LabelSize");
        WidthLabel.Content = SetupText.Get("Setup_WidthMm");
        HeightLabel.Content = SetupText.Get("Setup_HeightMm");
        DensityLabel.Content = SetupText.Get("Setup_Density");
        Dpi203.Content = SetupText.Get("Setup_Dpi", 203);
        Dpi300.Content = SetupText.Get("Setup_Dpi", 300);
        Dpi600.Content = SetupText.Get("Setup_Dpi", 600);
        BehaviorHeading.Text = SetupText.Get("Setup_Behavior");
        LanguageLabel.Content = SetupText.Get("Setup_Language");
        LanguageWindowsItem.Content = SetupText.Get("Setup_LanguageWindows");
        ShowNewestBox.Content = SetupText.Get("Setup_ShowNewest");
        KeepJobsBox.Content = SetupText.Get("Setup_KeepJobs");
        CancelButton.Content = SetupText.Get("Setup_Cancel");
        SaveButton.Content = SetupText.Get("Setup_Save");
        OpenSettingsRun.Text = SetupText.Get("Setup_OpenSettingsFile");
        OpenSettingsLink.ToolTip = SetupText.Get("Setup_OpenSettingsFileTip");
        OpenLogRun.Text = SetupText.Get("Setup_OpenLogFolder");
        CheckUpdatesRun.Text = SetupText.Get("Setup_CheckUpdates");
    }

    /// <summary>Fills the controls from the current settings.</summary>
    private void LoadValues()
    {
        PrinterNameBox.Text = _current.PrinterName;

        // The size list: every preset, then "Custom…" (Tag null) for any other size.
        foreach (var preset in LabelSizes.Presets)
            SizeBox.Items.Add(new ComboBoxItem { Content = preset.Describe(), Tag = preset });
        var custom = new ComboBoxItem { Content = SetupText.Get("Setup_Custom") };
        SizeBox.Items.Add(custom);
        // The boxes are filled even for a preset, so choosing "Custom…" starts from the size in use.
        WidthBox.Text = FormatMm(_current.LabelWidthMm);
        HeightBox.Text = FormatMm(_current.LabelHeightMm);
        var match = LabelSizes.Find(_current.LabelWidthMm, _current.LabelHeightMm);
        SizeBox.SelectedItem = match is null
            ? custom
            : SizeBox.Items.OfType<ComboBoxItem>().First(i => Equals(i.Tag, match));

        // A density this screen does not offer (152 dpi, still valid in settings.json) selects no option and is
        // kept unchanged on Save unless the person picks one.
        Dpi203.IsChecked = _current.DefaultDpi == 203;
        Dpi300.IsChecked = _current.DefaultDpi == 300;
        Dpi600.IsChecked = _current.DefaultDpi == 600;

        var language = _current.Language ?? "";
        LanguageBox.SelectedItem = LanguageBox.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(i => string.Equals((string)i.Tag, language, StringComparison.OrdinalIgnoreCase))
            ?? LanguageWindowsItem;

        ShowNewestBox.IsChecked = _current.ShowNewestJob;
        KeepJobsBox.IsChecked = _current.KeepJobs;
    }

    /// <summary>A millimetre value for an input box: up to three decimals so a hand-typed size survives unchanged.</summary>
    private static string FormatMm(double mm) => mm.ToString("0.###", LabelScope.Core.Localization.Text.Culture);

    private void OnSizeChanged(object sender, SelectionChangedEventArgs e)
    {
        var isCustom = SizeBox.SelectedItem is ComboBoxItem { Tag: null };
        CustomSizePanel.Visibility = isCustom ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- printer status --------------------------------------------------------------------------

    private void OnPrinterNameChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded) return;
        // Restart the pause timer; the check runs once typing stops.
        _nameTimer.Stop();
        _nameTimer.Start();
    }

    /// <summary>
    /// Shows whether Windows has a LabelScope printer with the typed name. A name the installer would refuse shows
    /// that problem at once, without asking Windows.
    /// </summary>
    private async Task RefreshStatusAsync()
    {
        var check = ++_statusCheck;
        var name = PrinterNameBox.Text.Trim();
        if (!TryMakeInstaller(name, out var installer, out var problem))
        {
            ShowStatus(problem, ok: false);
            return;
        }

        ShowStatus(SetupText.Get("Setup_StatusChecking"), ok: null);
        var status = await CheckStatusAsync(installer);
        if (check != _statusCheck) return; // a newer check is under way; its answer counts
        switch (status)
        {
            case PrinterStatus.Installed:
                ShowStatus(SetupText.Get("Setup_StatusInstalled", name), ok: true);
                break;
            case PrinterStatus.NotInstalled:
                ShowStatus(SetupText.Get("Setup_StatusNotInstalled", name), ok: false);
                break;
            case PrinterStatus.NameTakenByOther:
                ShowStatus(SetupText.Get("Setup_StatusNameTaken", name), ok: false);
                break;
            default:
                ShowStatus(SetupText.Get("Setup_StatusUnknown"), ok: false);
                break;
        }
    }

    /// <summary>Asks Windows about the printer; never throws (a failed check is <see cref="PrinterStatus.Unknown"/>).</summary>
    private static async Task<PrinterStatus> CheckStatusAsync(PrinterInstaller installer)
    {
        try { return await installer.GetStatusAsync(); }
        catch (Exception ex)
        {
            Log.Warning(ex, "Printer setup: status check failed");
            return PrinterStatus.Unknown;
        }
    }

    /// <summary>
    /// The status line under the printer name: green tick (<paramref name="ok"/> true), red mark (false), or a
    /// plain muted line while something is still running (null).
    /// </summary>
    private void ShowStatus(string text, bool? ok)
    {
        PrinterStatusText.Text = text;
        var brush = (Brush)FindResource(ok switch { true => "SuccessBrush", false => "ErrorBrush", null => "MutedBrush" });
        PrinterStatusText.Foreground = PrinterStatusIcon.Foreground = brush;
        PrinterStatusIcon.Text = ok == false ? (string)FindResource("IconError") : (string)FindResource("IconCheck");
        PrinterStatusIcon.Visibility = ok is null ? Visibility.Collapsed : Visibility.Visible;
    }

    // ---- validation ------------------------------------------------------------------------------

    /// <summary>
    /// Builds the installer for <paramref name="name"/>. The installer's constructor holds the printer name rules,
    /// so it decides whether a name is usable; this screen only picks the matching message.
    /// </summary>
    private bool TryMakeInstaller(string name, out PrinterInstaller installer, out string problem)
    {
        try
        {
            installer = new PrinterInstaller(new PowerShellRunner(), name, _current.ListenPort, new WinspoolPrinterLookup());
            problem = "";
            return true;
        }
        catch (ArgumentException ex) // ArgumentOutOfRangeException (port) included
        {
            installer = null!;
            problem = DescribeNameProblem(name, ex);
            return false;
        }
    }

    /// <summary>
    /// The message for a name the installer refused. Core's own texts talk about editing settings.json and starting
    /// LabelScope again, which is wrong on this screen, so the same rules are walked in the installer's order to pick
    /// this screen's wording. A refusal none of them explains keeps Core's text.
    /// </summary>
    private static string DescribeNameProblem(string name, ArgumentException ex)
    {
        // Mirrors Core's PrinterInstaller.MaxNameLength, which is internal. Only the wording depends on it: the
        // installer's constructor still decides, and a mismatch falls through to Core's own message below.
        const int maxLength = 60;
        if (ex.ParamName == "printerName")
        {
            if (string.IsNullOrWhiteSpace(name)) return SetupText.Get("Setup_NameEmpty");
            if (name.Length > maxLength) return SetupText.Get("Setup_NameTooLong", maxLength);
            if (name.Any(char.IsControl)) return SetupText.Get("Setup_NameControlChars");
            if (name.IndexOfAny(['\\', '/', '!', '*', '?', '[', ']']) >= 0) return SetupText.Get("Setup_NameForbiddenChars");
        }
        // ArgumentException appends " (Parameter 'x')" to the text; that is for programmers, not for the person.
        var message = ex.Message;
        var suffix = $" (Parameter '{ex.ParamName}')";
        var at = message.IndexOf(suffix, StringComparison.Ordinal);
        return at >= 0 ? message[..at] : message;
    }

    /// <summary>
    /// The values this screen owns. Every other key in settings.json belongs to the file and is never written from
    /// here, so a hand edit made while the screen was open (ListenPort, LogFolder, FontsFolder…) survives Save.
    /// </summary>
    /// <param name="PrinterName">Windows printer name, trimmed.</param>
    /// <param name="WidthMm">Loaded label width in millimetres.</param>
    /// <param name="HeightMm">Loaded label height in millimetres.</param>
    /// <param name="Dpi">The chosen density, or null when no option is checked (a 152 from the file stays as it is).</param>
    /// <param name="Language">"" (the Windows language) or one of Text.Languages: "en", "es", "pt-BR" or "fr".</param>
    /// <param name="ShowNewestJob">Show the newest job as it arrives.</param>
    /// <param name="KeepJobs">Keep jobs after closing LabelScope.</param>
    private sealed record DialogValues(string PrinterName, double WidthMm, double HeightMm, int? Dpi, string Language,
        bool ShowNewestJob, bool KeepJobs)
    {
        /// <summary>Writes these values over <paramref name="s"/>, leaving every other setting as it is.</summary>
        public void ApplyTo(AppSettings s)
        {
            s.PrinterName = PrinterName;
            s.LabelWidthMm = WidthMm;
            s.LabelHeightMm = HeightMm;
            if (Dpi is { } dpi) s.DefaultDpi = dpi;
            s.Language = Language;
            s.ShowNewestJob = ShowNewestJob;
            s.KeepJobs = KeepJobs;
        }
    }

    /// <summary>
    /// Reads and checks every value. On a problem shows the message, puts the cursor in the field to fix and
    /// returns false; settings.json is not touched.
    /// </summary>
    private bool TryReadValues(out DialogValues values, out PrinterInstaller installer)
    {
        values = null!;
        var name = PrinterNameBox.Text.Trim();
        if (!TryMakeInstaller(name, out installer, out var problem))
            return Refuse(problem, PrinterNameBox);

        double width, height;
        if (SizeBox.SelectedItem is ComboBoxItem { Tag: LabelSize preset })
        {
            (width, height) = (preset.WidthMm, preset.HeightMm);
        }
        else
        {
            if (!LabelSizes.TryParseMm(WidthBox.Text, out width) || !LabelSizes.IsValidMm(width))
                return Refuse(SetupText.Get("Setup_WidthInvalid"), WidthBox);
            if (!LabelSizes.TryParseMm(HeightBox.Text, out height) || !LabelSizes.IsValidMm(height))
                return Refuse(SetupText.Get("Setup_HeightInvalid"), HeightBox);
        }
        var dpi = OfferedDpis.Zip([Dpi203, Dpi300, Dpi600]).FirstOrDefault(p => p.Second.IsChecked == true).First;
        values = new DialogValues(
            name,
            // Rounded so the file shows 57.15 rather than 57.150000000000006 (2.25 in × 25.4).
            Math.Round(width, 3),
            Math.Round(height, 3),
            dpi == 0 ? null : dpi, // none checked: keep the value from the file (e.g. 152)
            LanguageBox.SelectedItem is ComboBoxItem { Tag: string code } ? code : "",
            ShowNewestBox.IsChecked == true,
            KeepJobsBox.IsChecked == true);
        return true;
    }

    /// <summary>
    /// Refuses a new name that already belongs to somebody else's printer: the main window could never install it,
    /// and the person would only find out later. An unchanged name is never refused here. Used by Save and by
    /// Reinstall printer, before anything is written.
    /// </summary>
    /// <returns>True when the name was refused (the message is shown).</returns>
    private async Task<bool> RefuseTakenNameAsync(string name, PrinterInstaller installer)
    {
        if (string.Equals(name, _savedPrinterName, StringComparison.OrdinalIgnoreCase)) return false;
        SetBusy(true);
        PrinterStatus status;
        try { status = await CheckStatusAsync(installer); }
        finally { SetBusy(false); }
        if (status != PrinterStatus.NameTakenByOther) return false;
        Refuse(SetupText.Get("Setup_NameTaken", name), PrinterNameBox);
        return true;
    }

    private bool Refuse(string message, Control field)
    {
        ShowMessage(message, isError: true);
        field.Focus();
        if (field is TextBox box) box.SelectAll();
        return false;
    }

    /// <summary>The message line above the footer; empty text hides it.</summary>
    private void ShowMessage(string text, bool isError)
    {
        MessageText.Text = text;
        MessageText.Foreground = (Brush)FindResource(isError ? "ErrorBrush" : "MutedBrush");
        MessageText.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// Writes settings.json; on failure shows Core's plain-language message and returns false. The file is read
    /// again first and only this screen's values are put on top, so other keys keep what the file holds now, not
    /// what it held when the screen opened. When that read reports problems (a damaged value), its repaired values
    /// are used for the keys this screen does not own, exactly as LabelScope would use them at start. When the file
    /// exists but cannot be opened or read at all, nothing is saved and the message says how to fix it: writing this
    /// screen's values on top of the defaults would erase every other value the person wrote in the file.
    /// </summary>
    private bool TrySave(DialogValues values)
    {
        AppSettings settings;
        try
        {
            var fresh = new SettingsStore().LoadOrCreate(_settingsPath);
            foreach (var message in fresh.Messages) Log.Information("Printer setup: settings: {Message}", message);
            if (fresh.ExistingFileUnreadable)
            {
                var refused = LabelScope.Core.Localization.Text.Get("Settings_SaveRefusedUnreadable", _settingsPath);
                Log.Warning("Printer setup: {Message}", refused);
                ShowMessage(refused, isError: true);
                return false;
            }
            settings = fresh.Settings;
        }
        catch (Exception ex)
        {
            // LoadOrCreate reports problems as messages; this only guards against the unexpected. The settings in
            // use are then the best remaining answer for the keys this screen does not own.
            Log.Warning(ex, "Printer setup: settings.json could not be read again before saving");
            settings = Copy(_current);
        }
        values.ApplyTo(settings);

        var result = new SettingsStore().Save(_settingsPath, settings);
        Log.Information("Printer setup: save {Success} {Message}", result.Success, result.Message);
        if (!result.Success)
        {
            ShowMessage(result.Message, isError: true);
            return false;
        }
        Result = settings;
        _savedPrinterName = settings.PrinterName;
        return true;
    }

    // ---- buttons ---------------------------------------------------------------------------------

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        try
        {
            ShowMessage("", isError: false);
            if (!TryReadValues(out var values, out var installer)) return;
            if (await RefuseTakenNameAsync(values.PrinterName, installer)) return;
            if (!TrySave(values)) return;
            DialogResult = true;
        }
        catch (Exception ex)
        {
            // async void: nothing may escape.
            Log.Error(ex, "Printer setup: Save failed unexpectedly");
            SetBusy(false);
            ShowMessage(SetupText.Get("Setup_ActionFailed"), isError: true);
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        // Result stays as it is: after a Reinstall it holds the settings already saved.
        DialogResult = false;
    }

    /// <summary>
    /// Reinstall printer: check the name, save (so the main window and the Windows printer agree on the name), then
    /// put the printer into Windows under the typed name (see <see cref="ReinstallAsync"/>). Each Windows change asks
    /// for permission once.
    /// </summary>
    private async void OnReinstall(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        try
        {
            ShowMessage("", isError: false);
            if (!TryReadValues(out var values, out var installer)) return;
            // The same refusal as Save, and before saving: a taken name must leave settings.json unchanged.
            if (await RefuseTakenNameAsync(values.PrinterName, installer)) return;
            var oldName = _savedPrinterName;
            if (!TrySave(values)) return;

            var name = values.PrinterName;
            SetBusy(true);
            ShowStatus(SetupText.Get("Setup_StatusWorking", name), ok: null);
            OperationResult result;
            try
            {
                result = await ReinstallAsync(oldName, name, installer);
            }
            finally { SetBusy(false); }

            Log.Information("Printer setup: reinstall {Success} {Message}", result.Success, result.Message);
            // On success the live status says it best ("Installed — choose …"); on failure the installer's message
            // says what went wrong and what to do.
            if (result.Success) await RefreshStatusAsync();
            else ShowStatus(result.Message, ok: false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Printer setup: Reinstall failed unexpectedly");
            SetBusy(false);
            ShowStatus(SetupText.Get("Setup_ActionFailed"), ok: false);
        }
    }

    /// <summary>
    /// Puts the printer into Windows under <paramref name="name"/>.
    /// <list type="bullet">
    /// <item>Same name: remove it, then add it again (a true reinstall, which repairs a broken printer).</item>
    /// <item>New name: add the new printer FIRST, and only then remove our printer under the old name. If the person
    /// declines the second permission prompt, or that removal fails, Windows still has a working LabelScope printer;
    /// it never ends up with none. The shared port stays, because Core removes it only when no printer uses it.</item>
    /// </list>
    /// </summary>
    private async Task<OperationResult> ReinstallAsync(string oldName, string name, PrinterInstaller installer)
    {
        if (string.Equals(oldName, name, StringComparison.OrdinalIgnoreCase))
        {
            var removed = await installer.RemoveAsync();
            if (!removed.Success) return removed;
            return await installer.InstallAsync();
        }

        var installed = await installer.InstallAsync(); // "already installed" counts as success
        if (!installed.Success) return installed;

        // Only our own printer under the old name is removed; somebody else's printer with that name is left alone.
        if (TryMakeInstaller(oldName, out var old, out _) && await CheckStatusAsync(old) == PrinterStatus.Installed)
        {
            var removedOld = await old.RemoveAsync();
            if (!removedOld.Success)
                return OperationResult.Fail(SetupText.Get("Setup_OldPrinterKept", name, oldName, removedOld.Message));
        }
        return installed;
    }

    /// <summary>Switches every input off while Windows is being asked something, so nothing changes underneath.</summary>
    private void SetBusy(bool busy)
    {
        _busy = busy;
        Body.IsEnabled = !busy;
        Footer.IsEnabled = !busy;
        Cursor = busy ? System.Windows.Input.Cursors.Wait : null;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        // Closing halfway through a printer change would leave the person not knowing what Windows did.
        if (_busy) e.Cancel = true;
        else _nameTimer.Stop();
    }

    // ---- footer links ----------------------------------------------------------------------------

    private void OnOpenSettingsFile(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        OpenSettingsFileRequested?.Invoke(this, EventArgs.Empty);
        DialogResult = false;
    }

    private void OnOpenLogFolder(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        OpenLogFolderRequested?.Invoke(this, EventArgs.Empty);
    }

    private async void OnCheckForUpdates(object sender, RoutedEventArgs e)
    {
        if (_busy || CheckForUpdates is null) return;
        try
        {
            CheckUpdatesLink.IsEnabled = false;
            ShowMessage(SetupText.Get("Setup_CheckingUpdates"), isError: false);
            ShowMessage(await CheckForUpdates(), isError: false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Printer setup: update check failed");
            ShowMessage(SetupText.Get("Setup_ActionFailed"), isError: true);
        }
        finally
        {
            CheckUpdatesLink.IsEnabled = true;
        }
    }
}
