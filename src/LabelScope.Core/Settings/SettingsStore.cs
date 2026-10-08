using System.Net;
using System.Text;
using System.Text.Json;

namespace LabelScope.Core.Settings;

/// <summary>Settings plus any plain-language notes about problems found while loading them.</summary>
public sealed record SettingsLoadResult(AppSettings Settings, IReadOnlyList<string> Messages);

/// <summary>Reads settings.json; creates a commented starter file when it is missing.</summary>
public sealed class SettingsStore
{
    /// <summary>The commented file written on first run. JSON allows no comments by spec, so we parse with comments enabled.</summary>
    public const string StarterText = """
        {
          // Address LabelScope listens on. "127.0.0.1" = this computer only.
          // Use "0.0.0.0" to also accept labels from other computers on your network.
          "ListenAddress": "127.0.0.1",

          // Port for incoming labels. 9100 is the standard label-printer port.
          // If another program already uses it, pick another number (1 to 65535).
          "ListenPort": 9100,

          // Print resolution: 152, 203, 300 or 600 dots per inch.
          "DefaultDpi": 203,

          // Label size used when the label itself does not say (101.6 x 152.4 mm = 4 x 6 inch).
          "DefaultLabelWidthMm": 101.6,
          "DefaultLabelHeightMm": 152.4,

          // How many received labels to keep in the list on the left.
          "HistoryLimit": 100,

          // Folder with your own TrueType fonts (used by a later version). Leave empty for none.
          "FontsFolder": "",

          // Where log files are written. A relative folder is created next to LabelScope.
          "LogFolder": "logs",

          // Name of the Windows printer LabelScope installs when you press "Install printer".
          "PrinterName": "P4 LabelScope Printer"
        }
        """;

    // Parser options: comments and trailing commas are accepted so a hand-edited file still loads.
    // Property names ignore case so "listenport" works as well as "ListenPort".
    private static readonly JsonSerializerOptions Options = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Loads settings from <paramref name="path"/>, creating the starter file if absent.</summary>
    public SettingsLoadResult LoadOrCreate(string path)
    {
        var messages = new List<string>();

        if (!File.Exists(path))
        {
            // Written without a byte-order mark so editors and the parser both read it cleanly.
            File.WriteAllText(path, StarterText, new UTF8Encoding(false));
            messages.Add($"A settings file was created at {path}. Open it to change the port, label size or other options.");
            return new SettingsLoadResult(new AppSettings(), messages);
        }

        AppSettings? loaded;
        try
        {
            loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options);
        }
        catch (JsonException ex)
        {
            // Never overwrite the user's file: they may only have a typo to fix.
            messages.Add($"The settings file could not be read ({ex.Message}). Standard settings are used for now. " +
                         $"Fix the file, or delete it to get a fresh one, then restart LabelScope: {path}");
            return new SettingsLoadResult(new AppSettings(), messages);
        }

        // A file containing only "null" deserializes to null; treat it as an empty file.
        loaded ??= new AppSettings();
        Validate(loaded, messages);
        return new SettingsLoadResult(loaded, messages);
    }

    /// <summary>Replaces bad values with defaults and records one message per problem, naming the key.</summary>
    private static void Validate(AppSettings s, List<string> messages)
    {
        // A fresh instance supplies the default for every key, so defaults are defined in one place only.
        var d = new AppSettings();

        if (!IPAddress.TryParse(s.ListenAddress, out _))
        {
            messages.Add($"ListenAddress \"{s.ListenAddress}\" is not a valid IP address; \"{d.ListenAddress}\" is used instead.");
            s.ListenAddress = d.ListenAddress;
        }
        if (s.ListenPort is < 1 or > 65535)
        {
            messages.Add($"ListenPort {s.ListenPort} is not between 1 and 65535; {d.ListenPort} is used instead.");
            s.ListenPort = d.ListenPort;
        }
        if (s.DefaultDpi is not (152 or 203 or 300 or 600))
        {
            messages.Add($"DefaultDpi {s.DefaultDpi} is not one of 152, 203, 300, 600; {d.DefaultDpi} is used instead.");
            s.DefaultDpi = d.DefaultDpi;
        }
        if (s.DefaultLabelWidthMm <= 0)
        {
            messages.Add($"DefaultLabelWidthMm must be above 0; {d.DefaultLabelWidthMm} is used instead.");
            s.DefaultLabelWidthMm = d.DefaultLabelWidthMm;
        }
        if (s.DefaultLabelHeightMm <= 0)
        {
            messages.Add($"DefaultLabelHeightMm must be above 0; {d.DefaultLabelHeightMm} is used instead.");
            s.DefaultLabelHeightMm = d.DefaultLabelHeightMm;
        }
        if (s.HistoryLimit is < 1 or > 1000)
        {
            messages.Add($"HistoryLimit {s.HistoryLimit} is not between 1 and 1000; {d.HistoryLimit} is used instead.");
            s.HistoryLimit = d.HistoryLimit;
        }
        if (string.IsNullOrWhiteSpace(s.PrinterName))
        {
            messages.Add($"PrinterName is empty; \"{d.PrinterName}\" is used instead.");
            s.PrinterName = d.PrinterName;
        }
        // An empty log folder is repaired silently: the default is harmless and nothing is lost.
        if (string.IsNullOrWhiteSpace(s.LogFolder)) s.LogFolder = d.LogFolder;
    }
}
