using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace LabelScope.Core.Settings;

/// <summary>Settings plus any plain-language notes about problems found while loading them.</summary>
/// <param name="Settings">The settings to use: the file's values, or the defaults when the file could not be used.</param>
/// <param name="Messages">Plain-language notes about problems found while loading.</param>
/// <param name="ExistingFileUnreadable">True when settings.json exists but could not be opened (locked, access
/// denied) or could not be parsed, so <paramref name="Settings"/> are only the defaults. Saving on top of such a
/// result would replace every value the person wrote in the file, so callers must not save then.</param>
public sealed record SettingsLoadResult(AppSettings Settings, IReadOnlyList<string> Messages,
                                        bool ExistingFileUnreadable = false);

/// <summary>Reads settings.json; creates a commented starter file when it is missing.</summary>
public sealed class SettingsStore
{
    /// <summary>
    /// The commented file written on first run: <see cref="Compose"/> with every default. JSON allows no comments by
    /// spec, so we parse with comments enabled.
    /// </summary>
    public static readonly string StarterText = Compose(new AppSettings());

    // Values are written by the JSON serializer, never by string formatting: in a Spanish culture 101.6 would become
    // "101,6" and true would become "True", and both would break the file. The relaxed encoder keeps letters such as
    // an accented vowel in a folder name readable instead of writing a \u escape; quotes and backslashes are still escaped.
    private static readonly JsonSerializerOptions ValueOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, ValueOptions);

    /// <summary>
    /// The commented settings file holding the values of <paramref name="s"/>. The starter file and
    /// <see cref="Save"/> both use it, so the file looks the same however it was written. The comments stay in
    /// English, like the log files, so a file sent in for support reads the same everywhere.
    /// </summary>
    public static string Compose(AppSettings s) => $$"""
        {
          // Address LabelScope listens on. Only two values are allowed:
          // "127.0.0.1" = this computer only (the Windows printer always sends here).
          // "0.0.0.0"   = also accept labels from other computers on your network.
          "ListenAddress": {{Json(s.ListenAddress)}},

          // Port for incoming labels. 9100 is the standard label-printer port.
          // If another program already uses it, pick another number (1 to 65535).
          "ListenPort": {{Json(s.ListenPort)}},

          // Print density of the label printer: 152, 203, 300 or 600 dots per inch.
          "DefaultDpi": {{Json(s.DefaultDpi)}},

          // Size of the label loaded in the printer, in millimetres (5 to 2000). It is used only when the ZPL
          // (this job or an earlier one) does not give the size with ^PW and ^LL. 101.6 x 152.4 is 4 x 6 inches.
          // The Printer setup window in LabelScope changes these for you.
          "LabelWidthMm": {{Json(s.LabelWidthMm)}},
          "LabelHeightMm": {{Json(s.LabelHeightMm)}},

          // How many received labels to keep in the list on the left.
          "HistoryLimit": {{Json(s.HistoryLimit)}},

          // Show a light 10 mm measuring grid over the label picture when LabelScope starts.
          // true or false. The "Light grid" box in the window switches it on and off while the program runs.
          "ShowGrid": {{Json(s.ShowGrid)}},

          // Show the label picture above the ZPL text instead of beside it, when LabelScope starts.
          // true or false. Handy on a narrow screen. The "Stacked layout" box in the window does the same while running.
          "StackedLayout": {{Json(s.StackedLayout)}},

          // Language of the window and messages: "en" = English, "es" = Spanish, "pt-BR" = Portuguese (Brazil),
          // "fr" = French, "" = the language of Windows.
          "Language": {{Json(s.Language)}},

          // true = a job that arrives is selected and shown at once. false = the job you are looking at stays.
          "ShowNewestJob": {{Json(s.ShowNewestJob)}},

          // true = keep the job list when LabelScope closes and show it again at the next start. false = start empty.
          "KeepJobs": {{Json(s.KeepJobs)}},

          // Folder with your own TrueType (.ttf) or OpenType (.otf) fonts. A label that names a font such as
          // E:ARIAL.TTF (with ^A@ or ^CW) uses the file with that name from this folder. Leave empty for none.
          "FontsFolder": {{Json(s.FontsFolder)}},

          // Where log files are written. A relative folder is created next to LabelScope.
          "LogFolder": {{Json(s.LogFolder)}},

          // Name of the Windows printer LabelScope installs when you press "Install printer".
          "PrinterName": {{Json(s.PrinterName)}},

          // true = at start, ask GitHub once whether a newer LabelScope exists (nothing about you is sent).
          // LabelScope never installs anything by itself; you press the update button. false = never ask.
          "CheckForUpdates": {{Json(s.CheckForUpdates)}}
        }
        """;

    /// <summary>
    /// Writes <paramref name="s"/> to <paramref name="path"/> in the commented format. The new file is written next
    /// to the old one first and then swapped in, so a failure halfway (disk full, power cut) never leaves a broken or
    /// half-written settings file. A read-only file is refused and left as it is. Never throws.
    /// </summary>
    /// <returns>Success with a short note, or failure with a plain-language message saying what to do.</returns>
    public OperationResult Save(string path, AppSettings s)
    {
        string? temp = null;
        try
        {
            var full = Path.GetFullPath(path);
            // Same folder as the target: File.Replace and File.Move are only a swap (not a copy) on the same volume.
            temp = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, Compose(s), new UTF8Encoding(false));
            if (File.Exists(full))
            {
                // Someone made the file read-only on purpose; File.Replace would still swap it on some systems.
                if (File.GetAttributes(full).HasFlag(FileAttributes.ReadOnly))
                    throw new UnauthorizedAccessException(Text.Get("Settings_FileReadOnly"));
                File.Replace(temp, full, null);
            }
            else
            {
                File.Move(temp, full);
            }
            temp = null;
            return OperationResult.Ok(Text.Get("Settings_Saved", full));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                       or NotSupportedException or System.Security.SecurityException)
        {
            return OperationResult.Fail(Text.Get("Settings_SaveFailed", path, ex.Message));
        }
        finally
        {
            // A temp file left by a failed save is removed; failing to remove it changes nothing for the user.
            if (temp is not null)
            {
                try { File.Delete(temp); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }

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
            try
            {
                // Written without a byte-order mark so editors and the parser both read it cleanly.
                File.WriteAllText(path, StarterText, new UTF8Encoding(false));
                messages.Add(Text.Get("Settings_Created", path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A read-only program folder or a missing parent folder must not crash the first run;
                // the defaults still work, so the user is told and the program carries on.
                messages.Add(Text.Get("Settings_CreateFailed", path, ex.Message));
            }
            return new SettingsLoadResult(new AppSettings(), messages);
        }

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Another program (an editor or antivirus) can hold the file open. We only read here,
            // so the user's file is left untouched and the defaults are used until the lock is gone.
            messages.Add(Text.Get("Settings_OpenFailed", ex.Message, path));
            return new SettingsLoadResult(new AppSettings(), messages, ExistingFileUnreadable: true);
        }

        AppSettings? loaded;
        try
        {
            loaded = JsonSerializer.Deserialize<AppSettings>(text, Options);
        }
        catch (JsonException ex)
        {
            // Never overwrite the user's file: they may only have a typo to fix.
            // An empty file also lands here ("no JSON tokens"), so it gets this same message.
            messages.Add(Text.Get("Settings_ReadFailed", ex.Message, path));
            return new SettingsLoadResult(new AppSettings(), messages, ExistingFileUnreadable: true);
        }

        // A file containing only "null" deserializes to null; treat it as an empty file.
        loaded ??= new AppSettings();
        NoteRetiredKeys(text, messages);
        Validate(loaded, messages);
        return new SettingsLoadResult(loaded, messages);
    }

    /// <summary>
    /// Settings that older versions had and this one ignores. Unknown keys are skipped silently by the parser, so
    /// someone editing one of these would otherwise wonder why nothing changes.
    /// </summary>
    private static readonly string[] RetiredKeys = ["DefaultLabelWidthMm", "DefaultLabelHeightMm"];

    private static void NoteRetiredKeys(string text, List<string> messages)
    {
        try
        {
            using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return;
            var found = doc.RootElement.EnumerateObject()
                .Select(p => p.Name)
                .Where(n => RetiredKeys.Contains(n, StringComparer.OrdinalIgnoreCase))
                .ToList();
            if (found.Count > 0)
                messages.Add(Text.Get(found.Count == 1 ? "Settings_RetiredKey_One" : "Settings_RetiredKey_Many",
                    string.Join(Text.Get("Settings_And"), found)));
        }
        catch (JsonException)
        {
            // Already read successfully above; a second read cannot fail in practice, and this note is optional.
        }
    }

    /// <summary>Replaces bad values with defaults and records one message per problem, naming the key.</summary>
    private static void Validate(AppSettings s, List<string> messages)
    {
        // A fresh instance supplies the default for every key, so defaults are defined in one place only.
        var d = new AppSettings();

        // Only these two are meaningful: the Windows printer always forwards to 127.0.0.1, and 0.0.0.0 is the
        // explicit choice to open the port to the network. Any other address (a specific network card, a name)
        // would either break the printer or open the port in a way the user did not clearly ask for.
        if (s.ListenAddress is not ("127.0.0.1" or "0.0.0.0"))
        {
            messages.Add(Text.Get("Settings_BadListenAddress", s.ListenAddress, d.ListenAddress));
            s.ListenAddress = d.ListenAddress;
        }
        if (s.ListenPort is < 1 or > 65535)
        {
            messages.Add(Text.Get("Settings_BadListenPort", s.ListenPort, d.ListenPort));
            s.ListenPort = d.ListenPort;
        }
        if (s.DefaultDpi is not (152 or 203 or 300 or 600))
        {
            messages.Add(Text.Get("Settings_BadDefaultDpi", s.DefaultDpi, d.DefaultDpi));
            s.DefaultDpi = d.DefaultDpi;
        }
        if (s.HistoryLimit is < 1 or > 1000)
        {
            messages.Add(Text.Get("Settings_BadHistoryLimit", s.HistoryLimit, d.HistoryLimit));
            s.HistoryLimit = d.HistoryLimit;
        }
        if (string.IsNullOrWhiteSpace(s.PrinterName))
        {
            messages.Add(Text.Get("Settings_EmptyPrinterName", d.PrinterName));
            s.PrinterName = d.PrinterName;
        }
        // Written as "not between" because a NaN size would slip past "is < 5 or > 2000".
        if (!(s.LabelWidthMm is >= 5 and <= 2000))
        {
            messages.Add(Text.Get("Settings_BadLabelSize", nameof(s.LabelWidthMm), s.LabelWidthMm, d.LabelWidthMm));
            s.LabelWidthMm = d.LabelWidthMm;
        }
        if (!(s.LabelHeightMm is >= 5 and <= 2000))
        {
            messages.Add(Text.Get("Settings_BadLabelSize", nameof(s.LabelHeightMm), s.LabelHeightMm, d.LabelHeightMm));
            s.LabelHeightMm = d.LabelHeightMm;
        }
        // Language: "" follows Windows. Case and spaces are forgiven ("ES", "pt-br" work) and the value is stored in
        // the one spelling the app compares ("pt-BR", not "pt-br"), which is why this is not a plain lower-casing.
        var language = Text.NormalizeLanguage(s.Language);
        if (language is null)
        {
            messages.Add(Text.Get("Settings_BadLanguage", s.Language ?? ""));
            language = d.Language;
        }
        s.Language = language;
        // A null folder ("FontsFolder": null) is repaired silently, so nothing later meets a null.
        s.FontsFolder ??= d.FontsFolder;
        // An empty log folder is repaired silently: the default is harmless and nothing is lost.
        if (string.IsNullOrWhiteSpace(s.LogFolder)) s.LogFolder = d.LogFolder;
    }
}
