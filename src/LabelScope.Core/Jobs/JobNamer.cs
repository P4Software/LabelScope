using System.Net;
using System.Text.RegularExpressions;

namespace LabelScope.Core.Jobs;

/// <summary>
/// Picks the readable name and the source shown on a job card, so the job list says "Shipping label" from
/// "WAREHOUSE-PC" instead of only a time.
/// </summary>
public static partial class JobNamer
{
    /// <summary>Longest name shown on a card, ellipsis included.</summary>
    public const int MaxNameLength = 40;

    /// <summary>How long a reverse DNS lookup may take before the IP address is shown instead.</summary>
    public static readonly TimeSpan LookupTimeout = TimeSpan.FromMilliseconds(300);

    // Command text runs until the next command prefix (^ or ~) or the end of the text.
    [GeneratedRegex(@"\^FX([^\^~]*)", RegexOptions.IgnoreCase)]
    private static partial Regex CommentPattern();

    [GeneratedRegex(@"\^FD([^\^~]*)", RegexOptions.IgnoreCase)]
    private static partial Regex DataPattern();

    // The first command that starts a field: from here on a ^FX describes a single field, not the label.
    [GeneratedRegex(@"\^(FO|FT|FD)", RegexOptions.IgnoreCase)]
    private static partial Regex FirstFieldPattern();

    /// <summary>
    /// The job name (spec decision 2): the text of a ^FX comment that comes before the first ^FO, ^FT or ^FD, because
    /// programs often put the label type there; otherwise the first ^FD data; otherwise the localized default
    /// ("Label"). The text is put on one line, trimmed, and cut to <see cref="MaxNameLength"/> characters with an
    /// ellipsis. ^FH escapes are not decoded. Never returns an empty string.
    /// </summary>
    public static string Name(string? zpl)
    {
        var found = NameFromZpl(zpl);
        return found.Length > 0 ? found : Text.Get("Job_DefaultName");
    }

    /// <summary>Same as <see cref="Name"/> but returns "" instead of the localized default when the ZPL has no usable text.</summary>
    public static string NameFromZpl(string? zpl)
    {
        if (string.IsNullOrEmpty(zpl)) return "";
        var firstField = FirstFieldPattern().Match(zpl);
        var fieldStart = firstField.Success ? firstField.Index : int.MaxValue;

        // Only comments before the first field name the job; later ^FX lines describe single fields. Several leading
        // comments may exist and the first non-empty one wins.
        for (var c = CommentPattern().Match(zpl); c.Success && c.Index < fieldStart; c = c.NextMatch())
            if (Clean(c.Groups[1].Value) is { Length: > 0 } comment) return comment;

        // An empty ^FD (a blank field) says nothing about the job, so the next one is tried.
        for (var d = DataPattern().Match(zpl); d.Success; d = d.NextMatch())
            if (Clean(d.Groups[1].Value) is { Length: > 0 } data) return data;
        return "";
    }

    /// <summary>One line, trimmed, at most <see cref="MaxNameLength"/> characters (the last one an ellipsis when cut).</summary>
    private static string Clean(string text)
    {
        var line = text.ReplaceLineEndings(" ").Replace('\t', ' ').Trim();
        return line.Length <= MaxNameLength ? line : line[..(MaxNameLength - 1)].TrimEnd() + "…";
    }

    /// <summary>
    /// The source shown on a job card, using the system DNS for remote senders. A file or pasted job, a sender on
    /// this computer, or a missing address returns a localized text; another computer returns its short host name,
    /// or its IP address when the name is not known within <see cref="LookupTimeout"/>. Text that is not an IP
    /// address (such as "unknown") is returned as it is.
    /// </summary>
    /// <remarks>
    /// This method can wait up to 300 ms for DNS, so never call it on the UI thread; use <see cref="SourceAsync(JobOrigin, string?, CancellationToken)"/>
    /// there, or call it from the listener's socket thread where the job arrives.
    /// </remarks>
    public static string Source(JobOrigin origin, string? remoteAddress) =>
        SourceAsync(origin, remoteAddress, SystemLookup, LookupTimeout, CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>Same as <see cref="Source"/> without blocking the calling thread.</summary>
    public static Task<string> SourceAsync(JobOrigin origin, string? remoteAddress, CancellationToken cancellationToken = default) =>
        SourceAsync(origin, remoteAddress, SystemLookup, LookupTimeout, cancellationToken);

    /// <summary>
    /// Same as <see cref="Source"/> with the reverse lookup and its time limit supplied by the caller (tests use a fake
    /// lookup so no real DNS is asked). A lookup that fails, times out, or returns nothing gives the IP address.
    /// </summary>
    /// <param name="origin">How the job reached LabelScope.</param>
    /// <param name="remoteAddress">The sender's IP address as text; null or empty means this computer.</param>
    /// <param name="reverseLookup">Returns the host name of an address, or null when it has none.</param>
    /// <param name="timeout">How long to wait for <paramref name="reverseLookup"/>.</param>
    /// <param name="cancellationToken">Stops waiting; the IP address is returned.</param>
    public static async Task<string> SourceAsync(JobOrigin origin, string? remoteAddress,
        Func<IPAddress, CancellationToken, Task<string?>> reverseLookup, TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reverseLookup);
        switch (origin)
        {
            case JobOrigin.OpenedFromFile: return Text.Get("Job_OpenedFromFile");
            case JobOrigin.Pasted: return Text.Get("Job_Pasted");
        }

        // The Windows printer always sends from this computer; a missing address can only mean the same.
        if (string.IsNullOrWhiteSpace(remoteAddress)) return Text.Get("Job_ThisComputer");
        var shown = remoteAddress.Trim();
        if (!IPAddress.TryParse(shown, out var address)) return shown;
        // An IPv4 sender reaching a dual-mode socket shows up as ::ffff:127.0.0.1, which IsLoopback does not catch.
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return Text.Get("Job_ThisComputer");
        shown = address.ToString();

        string? host;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            // WaitAsync enforces the limit even when the lookup ignores its token (Windows DNS often does).
            limit.CancelAfter(timeout);
            host = await reverseLookup(address, limit.Token).WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A timeout, no PTR record, or no network: the IP address is still a correct and useful source.
            return shown;
        }
        return ShortHostName(host) ?? shown;
    }

    /// <summary>"pc1.corp.local" becomes "pc1"; an empty name or one that is only an IP address gives null.</summary>
    private static string? ShortHostName(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return null;
        host = host.Trim().TrimEnd('.');
        // Without a PTR record Windows can hand back the address itself; cutting "192.168.1.5" at the dot would give "192".
        if (IPAddress.TryParse(host, out _)) return null;
        var dot = host.IndexOf('.');
        var shortName = dot > 0 ? host[..dot] : host;
        return shortName.Length > 0 ? shortName : null;
    }

    private static async Task<string?> SystemLookup(IPAddress address, CancellationToken ct) =>
        (await Dns.GetHostEntryAsync(address.ToString(), ct).ConfigureAwait(false)).HostName;
}
