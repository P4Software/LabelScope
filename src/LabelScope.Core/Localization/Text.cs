using System.Globalization;
using System.Resources;

namespace LabelScope.Core.Localization;

/// <summary>
/// Looks up every user-visible message in the English or Spanish resource files. Messages are
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
    /// The language used by <see cref="Get"/>. Defaults to Spanish when the Windows display
    /// language is Spanish, otherwise English.
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

    private static CultureInfo DefaultCulture() =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "es"
            ? new CultureInfo("es")
            : new CultureInfo("en");

    private static IReadOnlyList<string> LoadKeys()
    {
        // The invariant (neutral) set has every key; the Spanish file is kept in step with it, key for key.
        var set = Resources.GetResourceSet(CultureInfo.InvariantCulture, true, true);
        var keys = new List<string>();
        if (set is not null)
            foreach (System.Collections.DictionaryEntry e in set)
                keys.Add((string)e.Key);
        return keys.ToArray();
    }
}
