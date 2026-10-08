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

    /// <summary>Label width in millimetres used when the ZPL has no ^PW (default 4 inch).</summary>
    public double DefaultLabelWidthMm { get; set; } = 101.6;

    /// <summary>Label height in millimetres used when the ZPL has no ^LL (default 6 inch).</summary>
    public double DefaultLabelHeightMm { get; set; } = 152.4;

    /// <summary>How many labels are kept in the history list.</summary>
    public int HistoryLimit { get; set; } = 100;

    /// <summary>Folder with custom TrueType fonts (used from a later release).</summary>
    public string FontsFolder { get; set; } = "";

    /// <summary>Folder for log files; a relative path is relative to the program folder.</summary>
    public string LogFolder { get; set; } = "logs";

    /// <summary>Name of the Windows printer that LabelScope installs.</summary>
    public string PrinterName { get; set; } = "P4 LabelScope Printer";
}
