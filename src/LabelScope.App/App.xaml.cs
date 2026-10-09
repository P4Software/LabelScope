using System.IO;
using System.Windows;
using System.Windows.Threading;
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
            try
            {
                Log.Fatal(ex, "Unhandled exception on a background thread");
                Log.CloseAndFlush(); // the process is ending: push the log lines to disk now
            }
            catch
            {
                // Serilog may not be configured yet; the crash log above has the details.
            }

            if (e.IsTerminating)
            {
                MessageBox.Show(
                    "LabelScope hit an unexpected error and has to close.\n\n" + ex.Message + "\n\n" +
                    (path is null
                        ? "The details could not be saved."
                        : "The details were saved in:\n" + path) +
                    "\n\nStart LabelScope again to continue. If this keeps happening, send that file to support.",
                    "LabelScope", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        catch
        {
            // Nothing more can be done.
        }
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
        try
        {
            var path = _crashLog.Write("error in the window", e.Exception);
            // Log may not be configured yet (the default logger discards silently), which is fine.
            Log.Error(e.Exception, "Unhandled error");
            MessageBox.Show(
                "Something unexpected went wrong, but LabelScope is still running.\n\n" +
                e.Exception.Message +
                "\n\nThe details were written to " + (path ?? "the log file (use \"Open log folder\" in LabelScope)") + ".",
                "LabelScope", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch
        {
            // Nothing more can be done; staying alive is the goal.
        }
    }
}
