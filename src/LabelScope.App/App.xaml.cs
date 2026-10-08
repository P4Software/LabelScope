using System.Windows;
using System.Windows.Threading;
using Serilog;

namespace LabelScope.App;

/// <summary>Application entry point.</summary>
public partial class App : Application
{
    /// <summary>
    /// Last line of defence: log the error and show it in plain language instead of vanishing,
    /// so the user can read it and send it to support. This handler must never throw itself,
    /// otherwise the original problem would be replaced by a crash.
    /// </summary>
    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Keep the window open no matter what happens below.
        e.Handled = true;
        try
        {
            // Log may not be configured yet (the default logger discards silently), which is fine.
            Log.Error(e.Exception, "Unhandled error");
            MessageBox.Show(
                "Something unexpected went wrong, but LabelScope is still running.\n\n" +
                e.Exception.Message +
                "\n\nThe details were written to the log file (use \"Open log folder\" in LabelScope).",
                "LabelScope", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch
        {
            // Nothing more can be done; staying alive is the goal.
        }
    }
}
