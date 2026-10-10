using System.Text.RegularExpressions;

namespace LabelScope.App;

/// <summary>
/// Picks a readable name for a received job from its ZPL, so the job list shows "Shipping label" or "Maria Gonzalez"
/// instead of only a time. Rule (spec decision 2): a ^FX comment before the first field wins, because programs often
/// put the label type there; otherwise the first ^FD text; otherwise nothing (the caller shows "Label").
/// </summary>
public static partial class JobNaming
{
    private const int MaxLength = 40;

    // Command text runs until the next command prefix (^ or ~) or the end of the text.
    [GeneratedRegex(@"\^FX([^\^~]*)", RegexOptions.IgnoreCase)]
    private static partial Regex CommentPattern();

    [GeneratedRegex(@"\^FD([^\^~]*)", RegexOptions.IgnoreCase)]
    private static partial Regex DataPattern();

    /// <summary>Returns the job name, or an empty string when the ZPL holds neither a usable comment nor field data.</summary>
    public static string NameFrom(string zpl)
    {
        var data = DataPattern().Match(zpl);
        var comment = CommentPattern().Match(zpl);
        // Only a comment that comes before the first field names the job; later ^FX lines describe single fields.
        if (comment.Success && (!data.Success || comment.Index < data.Index) && Clean(comment.Groups[1].Value) is { Length: > 0 } c)
            return c;
        return data.Success ? Clean(data.Groups[1].Value) : "";
    }

    /// <summary>One line, trimmed, at most 40 characters.</summary>
    private static string Clean(string text)
    {
        var line = text.ReplaceLineEndings(" ").Trim();
        return line.Length <= MaxLength ? line : line[..MaxLength].TrimEnd();
    }
}
