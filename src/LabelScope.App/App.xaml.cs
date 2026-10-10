using System.IO;
using System.Windows;
using System.Windows.Threading;
using LabelScope.App.Localization;
using LabelScope.Core.Diagnostics;
using Serilog;

namespace LabelScope.App;

/// <summary>Application entry point.</summary>
public partial class App : Application
{
    // Fixed folders on purpose: they must not depend on settings.json, which may be what failed.
    private readonly CrashLog _crashLog = new(
        Path.Combine(AppContext.BaseDirectory, "logs"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LabelScope", "logs"),
        appVersion: typeof(App).Assembly.GetName().Version?.ToString() ?? "");

    private readonly ErrorDialogGuard _dialogGuard = new();

    /// <summary>Registers the error handlers before any window or background work exists.</summary>
    public App()
    {
        // Threads other than the window thread (socket listener, label rendering) never reach the dispatcher
        // handler below; without this their errors would end the program silently.
        AppDomain.CurrentDomain.UnhandledException += OnBackgroundThreadFailed;
        // A task whose error nobody looked at: log it, keep running.
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskError;
    }

    /// <summary>
    /// A background thread died and the program is about to end. Save the details and tell the user in plain
    /// language what happened and where the details are, because the window will disappear right after this.
    /// This handler must never throw itself.
    /// </summary>
    private void OnBackgroundThreadFailed(object sender, UnhandledExceptionEventArgs e)
    {
        try
        {
            var ex = e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString() ?? "unknown error");
            var path = _crashLog.Write("fatal error on a background thread", ex);
            if (e.IsTerminating)
            {
                try
                {
                    Log.Fatal(ex, "Unhandled exception on a background thread");
                    Log.CloseAndFlush(); // the process is ending: push the log lines to disk now
                }
                catch
                {
                    // Serilog may not be configured yet; the crash log above has the details.
                }

                var details = path is null
                    ? Say("Ui_CrashDetailsNotSaved", "The details could not be saved.")
                    : Say("Ui_CrashDetailsSaved", "The details were saved in:\n{0}", path);
                MessageBox.Show(
                    Say("Ui_CrashFatal",
                        "LabelScope hit an unexpected error and has to close.\n\n{0}\n\n{1}\n\n" +
                        "Start LabelScope again to continue. If this keeps happening, send that file to support.",
                        CrashLog.ShortMessage(ex.Message), details),
                    "LabelScope", MessageBoxButton.OK, MessageBoxImage.Error);
                // End the process ourselves: otherwise Windows adds its own "has stopped working" dialog after ours.
                Environment.Exit(1);
            }
            else
            {
                Log.Error(ex, "Unhandled exception on a background thread (the program keeps running)");
            }
        }
        catch
        {
            // Nothing more can be done.
        }
    }

    /// <summary>
    /// The text of a crash dialog in the current language. These dialogs appear when something is already broken,
    /// possibly the language resources themselves, so any failure (or a key the resources do not have) falls back to
    /// <paramref name="english"/>: the person always gets a readable message, never a key name or a second crash.
    /// </summary>
    /// <param name="key">The UiStrings key.</param>
    /// <param name="english">The English text with the same placeholders, used when the lookup fails.</param>
    /// <param name="args">Values for the placeholders.</param>
    private static string Say(string key, string english, params object[] args)
    {
        try
        {
            var text = UiText.Get(key, args);
            // UiText returns the key itself when the text is missing.
            if (!string.Equals(text, key, StringComparison.Ordinal)) return text;
        }
        catch
        {
            // Resources or the language setting are unusable; the English text below still works.
        }
        try { return args.Length == 0 ? english : string.Format(System.Globalization.CultureInfo.InvariantCulture, english, args); }
        catch (FormatException) { return english; }
    }

    /// <summary>A background task failed and nobody observed it. The program keeps running.</summary>
    private void OnUnobservedTaskError(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        try
        {
            _crashLog.Write("a background task failed (the program kept running)", e.Exception);
            Log.Error(e.Exception, "Unobserved task exception");
        }
        catch
        {
            // Logging must not make things worse.
        }
        e.SetObserved();
    }

    /// <summary>
    /// Last line of defence on the window thread: log the error and show it in plain language instead of vanishing,
    /// so the user can read it and send it to support. This handler must never throw itself,
    /// otherwise the original problem would be replaced by a crash.
    /// </summary>
    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Keep the window open no matter what happens below.
        e.Handled = true;
        var dialogClaimed = false;
        try
        {
            var path = _crashLog.Write("error in the window", e.Exception);
            // Log may not be configured yet (the default logger discards silently), which is fine.
            Log.Error(e.Exception, "Unhandled error");

            // Three errors in 30 seconds means the program is stuck in a loop: stop with one clear message
            // instead of an endless stream of dialogs.
            var loop = _dialogGuard.RecordAndCheckLoop();
            // MessageBox keeps the message loop running, so a new error can arrive while a dialog is open:
            // in that case only the log is written.
            dialogClaimed = _dialogGuard.TryEnterDialog();
            if (!dialogClaimed) return;

            var where = path ?? Say("Ui_CrashLogFallback", "the log file (use \"{0}\" in LabelScope)",
                                    Say("Ui_OpenLog", "Open log folder"));
            if (loop)
            {
                MessageBox.Show(
                    Say("Ui_CrashLoop",
                        "LabelScope keeps running into errors and has to close.\n\nThe details were written to {0}.\n\n" +
                        "Start LabelScope again to continue. If this keeps happening, send that file to support.",
                        where),
                    "LabelScope", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
                return;
            }

            MessageBox.Show(
                Say("Ui_CrashKeptRunning",
                    "Something unexpected went wrong, but LabelScope is still running.\n\n{0}\n\n" +
                    "The details were written to {1}.",
                    CrashLog.ShortMessage(e.Exception.Message), where),
                "LabelScope", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch
        {
            // Nothing more can be done; staying alive is the goal.
        }
        finally
        {
            if (dialogClaimed) _dialogGuard.ExitDialog();
        }
    }
}
