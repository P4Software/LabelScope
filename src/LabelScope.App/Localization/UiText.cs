using System.Resources;
using LabelScope.Core.Localization;

namespace LabelScope.App.Localization;

/// <summary>
/// Looks up the texts of the window itself (toolbar, cards, tabs, status bar) in UiStrings.resx and
/// UiStrings.es.resx. It follows the same language as Core's <see cref="Text"/> (<see cref="Text.Culture"/>), so one
/// language switch changes both the window texts and the messages that come from Core.
/// </summary>
/// <remarks>
/// The window texts live in the App rather than in Core's Strings.resx because they belong to this window only;
/// the lookup rules (unknown key shows the key, a bad translation never crashes) are the same as in Core.
/// </remarks>
public static class UiText
{
    private static readonly ResourceManager Resources =
        new("LabelScope.App.Localization.UiStrings", typeof(UiText).Assembly);

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
