using System.Resources;
using LabelScope.Core.Localization;

namespace LabelScope.App.Localization;

/// <summary>
/// Looks up the texts of the Printer setup window in SetupStrings.resx and its translations (SetupStrings.es.resx,
/// SetupStrings.pt-BR.resx, SetupStrings.fr.resx). Like
/// <see cref="UiText"/>, it follows Core's <see cref="Text.Culture"/>, so the dialog always speaks the same language
/// as the main window and as the messages that come from Core.
/// </summary>
/// <remarks>
/// The dialog has its own resource pair so it can be built and changed without touching the main window's
/// UiStrings or Core's Strings files. The lookup rules are the same: an unknown key shows the key, a bad
/// translation never crashes.
/// </remarks>
public static class SetupText
{
    private static readonly ResourceManager Resources =
        new("LabelScope.App.Localization.SetupStrings", typeof(SetupText).Assembly);

    /// <summary>
    /// Returns the text for <paramref name="key"/> in the current language, formatted with <paramref name="args"/>.
    /// An unknown key returns the key itself, so a missing text is visible but never stops the program.
    /// </summary>
    public static string Get(string key, params object[]? args)
    {
        var culture = Text.Culture;
        string? template;
        try { template = Resources.GetString(key, culture); }
        catch (MissingManifestResourceException) { template = null; }
        if (template is null) return key;
        if (args is null || args.Length == 0) return template;
        try { return string.Format(culture, template, args); }
        catch (FormatException) { return template; } // a bad translation must not take the window down
    }
}
