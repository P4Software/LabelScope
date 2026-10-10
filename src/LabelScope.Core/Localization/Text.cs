using System.Globalization;
using System.Resources;

namespace LabelScope.Core.Localization;

/// <summary>
/// Looks up every user-visible message in the English, Spanish, Brazilian Portuguese or French resource files. Messages are
/// resolved on each call (never cached as finished strings) so a language switch takes effect
/// the next time a message is requested.
/// </summary>
public static class Text
{
    private static readonly ResourceManager Resources =
        new("LabelScope.Core.Localization.Strings", typeof(Text).Assembly);

    // Labels render on socket threads while the UI changes the language, so the reference is
    // read and written with Volatile to avoid stale reads.
    private static CultureInfo _culture = DefaultCulture();

    /// <summary>
    /// The language used by <see cref="Get"/>. Defaults to the Windows display language when LabelScope speaks it
    /// (see <see cref="CultureForWindows"/>), otherwise English.
    /// </summary>
    public static CultureInfo Culture
    {
        get => Volatile.Read(ref _culture);
        set => Volatile.Write(ref _culture, value ?? throw new ArgumentNullException(nameof(value)));
    }

    /// <summary>All message keys that exist in the neutral (English) resource file.</summary>
    public static IReadOnlyList<string> Keys { get; } = LoadKeys();

    /// <summary>
    /// Returns the message for <paramref name="key"/> in <see cref="Culture"/>, formatted with
    /// <paramref name="args"/>. An unknown key returns the key itself so a missing text is visible
    /// but never crashes the program. A null <paramref name="args"/> is treated as no arguments.
    /// </summary>
    public static string Get(string key, params object[]? args)
    {
        var culture = Culture;
        string? template;
        try { template = Resources.GetString(key, culture); }
        catch (MissingManifestResourceException) { template = null; }
        if (template is null) return key;
        if (args is null || args.Length == 0) return template;
        try { return string.Format(culture, template, args); }
        catch (FormatException) { return template; } // a bad translation must not take the app down
    }

    /// <summary>
    /// The values the Language setting accepts besides "" (follow Windows), in the one spelling the app stores and
    /// compares. The order is the order of the language pickers.
    /// </summary>
    public static IReadOnlyList<string> Languages { get; } = ["en", "es", "pt-BR", "fr"];

    /// <summary>
    /// Returns the stored spelling of a Language setting value: "" for empty or blank, otherwise one of
    /// <see cref="Languages"/> matched without regard to case or surrounding spaces ("PT-br" gives "pt-BR").
    /// Returns null for a language LabelScope does not speak, so the caller can say so.
    /// </summary>
    public static string? NormalizeLanguage(string? value)
    {
        var trimmed = (value ?? "").Trim();
        if (trimmed.Length == 0) return "";
        foreach (var code in Languages)
            if (string.Equals(code, trimmed, StringComparison.OrdinalIgnoreCase)) return code;
        return null;
    }

    /// <summary>
    /// The language LabelScope uses for a Windows display language: any Spanish gives Spanish, any Portuguese gives
    /// Brazilian Portuguese (the only Portuguese translation), any French gives French, everything else English.
    /// </summary>
    public static CultureInfo CultureForWindows(CultureInfo windowsUiCulture)
    {
        ArgumentNullException.ThrowIfNull(windowsUiCulture);
        return windowsUiCulture.TwoLetterISOLanguageName switch
        {
            "es" => new CultureInfo("es"),
            "pt" => new CultureInfo("pt-BR"),
            "fr" => new CultureInfo("fr"),
            _ => new CultureInfo("en"),
        };
    }

    /// <summary>
    /// The culture for a Language setting value: the chosen language, or for "" (and anything unknown, which
    /// settings validation already reported) the Windows display language rule of <see cref="CultureForWindows"/>.
    /// </summary>
    public static CultureInfo CultureForLanguage(string? language) =>
        NormalizeLanguage(language) is { Length: > 0 } code
            ? new CultureInfo(code)
            : CultureForWindows(CultureInfo.CurrentUICulture);

    /// <summary>
    /// The Language setting value (one of <see cref="Languages"/>) that matches <paramref name="culture"/>, used to
    /// show the right entry in a language picker. A culture LabelScope does not speak gives "en".
    /// </summary>
    public static string LanguageOf(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        return culture.TwoLetterISOLanguageName switch
        {
            "es" => "es",
            "pt" => "pt-BR",
            "fr" => "fr",
            _ => "en",
        };
    }

    private static CultureInfo DefaultCulture() => CultureForWindows(CultureInfo.CurrentUICulture);

    private static IReadOnlyList<string> LoadKeys()
    {
        // The invariant (neutral) set has every key; the translated files are checked against it by tests.
        var set = Resources.GetResourceSet(CultureInfo.InvariantCulture, true, true);
        var keys = new List<string>();
        if (set is not null)
            foreach (System.Collections.DictionaryEntry e in set)
                keys.Add((string)e.Key);
        return keys.ToArray();
    }
}
