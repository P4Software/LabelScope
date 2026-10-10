namespace LabelScope.Core.Settings;

/// <summary>User-editable settings, stored in settings.json beside the program.</summary>
public sealed class AppSettings
{
    /// <summary>IP address to listen on; 127.0.0.1 means this computer only.</summary>
    public string ListenAddress { get; set; } = "127.0.0.1";

    /// <summary>TCP port for incoming ZPL (9100 is the standard raw label-printer port).</summary>
    public int ListenPort { get; set; } = 9100;

    /// <summary>Print resolution used when ZPL does not say otherwise: 152, 203, 300 or 600.</summary>
    public int DefaultDpi { get; set; } = 203;

    /// <summary>How many labels are kept in the history list.</summary>
    public int HistoryLimit { get; set; } = 100;

    /// <summary>Start with the light measuring grid shown over the label picture (the window has a box for it too).</summary>
    public bool ShowGrid { get; set; }

    /// <summary>Start with the label picture above the ZPL text instead of beside it (the window has a box for it too).</summary>
    public bool StackedLayout { get; set; }

    /// <summary>Folder with your own TrueType (.ttf) and OpenType (.otf) fonts; a label that names a font file with ^A@ or ^CW uses the file of the same name from here. Empty = none.</summary>
    public string FontsFolder { get; set; } = "";

    /// <summary>Folder for log files; a relative path is relative to the program folder.</summary>
    public string LogFolder { get; set; } = "logs";

    /// <summary>Name of the Windows printer that LabelScope installs.</summary>
    public string PrinterName { get; set; } = "LabelScope";

    /// <summary>
    /// When true, LabelScope asks GitHub once at start whether a newer version exists. Nothing is installed
    /// unless the user presses the update button. The "Check for updates" button works either way.
    /// </summary>
    public bool CheckForUpdates { get; set; } = true;
}
