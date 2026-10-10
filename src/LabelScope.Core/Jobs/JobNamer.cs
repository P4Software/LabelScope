using System.Collections.Concurrent;
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

    // Command text runs until the next command prefix (^ or ~) or the end of the text.
    [GeneratedRegex(@"\^FX([^\^~]*)", RegexOptions.IgnoreCase)]
    private static partial Regex CommentPattern();

    [GeneratedRegex(@"\^FD([^\^~]*)", RegexOptions.IgnoreCase)]
    private static partial Regex DataPattern();

    // The first command that starts a field: from here on a ^FX describes a single field, not the label.
    [GeneratedRegex(@"\^(FO|FT|FD)", RegexOptions.IgnoreCase)]
    private static partial Regex FirstFieldPattern();

    /// <summary>
    /// The job name: the text of a ^FX comment that comes before the first ^FO, ^FT or ^FD, because
    /// programs often put the label type there; otherwise the first ^FD data; otherwise the localized default
    /// ("Label"). The text is put on one line, trimmed, and cut to <see cref="MaxNameLength"/> characters with an
    /// ellipsis. ^FH escapes are not decoded. Never returns an empty string.
    /// </summary>
    public static string Name(string? zpl) => DisplayName(NameFromZpl(zpl));

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

    /// <summary>
    /// The name to show for a stored name: <paramref name="name"/> itself, or the localized default ("Label") when it
    /// is empty. Looked up on every call, so the default follows a language switch.
    /// </summary>
    public static string DisplayName(string? name) =>
        string.IsNullOrWhiteSpace(name) ? Text.Get("Job_DefaultName") : name;

    /// <summary>
    /// One line, trimmed, at most <see cref="MaxNameLength"/> characters (the last one an ellipsis when cut). Every
    /// control character (line breaks, tabs, the NUL or ESC bytes some programs send) becomes a space, because a card
    /// title cannot show them.
    /// </summary>
    private static string Clean(string text)
    {
        // CR LF must become one space, not two, so line endings are handled before the other control characters.
        var chars = text.ReplaceLineEndings(" ").ToCharArray();
        for (var i = 0; i < chars.Length; i++)
            if (char.IsControl(chars[i])) chars[i] = ' ';
        var line = new string(chars).Trim();
        if (line.Length <= MaxNameLength) return line;
        var cut = MaxNameLength - 1;
        // Never end on the first half of a surrogate pair (an emoji, say): that would leave a broken character.
        if (char.IsHighSurrogate(line[cut - 1])) cut--;
        return line[..cut].TrimEnd() + "…";
    }

    /// <summary>
    /// The source shown on a job card, using <see cref="HostNameResolver.Default"/> for remote senders. A file or
    /// pasted job, a sender on this computer, or a missing address returns a localized text; another computer returns
    /// its short host name, or its IP address when the name is not known within 300 ms. Text that is not an IP
    /// address (such as "unknown") is returned as it is.
    /// </summary>
    /// <remarks>
    /// When the address was looked up before in this session the answer comes from the cache at once. Otherwise this
    /// method can wait up to 300 ms for DNS, so never call it on the UI thread; use
    /// <see cref="SourceAsync(JobOrigin, string?, HostNameResolver?, CancellationToken)"/> there.
    /// </remarks>
    public static string Source(JobOrigin origin, string? remoteAddress)
    {
        var resolver = HostNameResolver.Default;
        if (!NeedsLookup(origin, remoteAddress)) return DisplaySource(origin, remoteAddress, null);
        if (resolver.TryGetCached(remoteAddress!, out var cached)) return DisplaySource(origin, remoteAddress, cached);
        return SourceAsync(origin, remoteAddress, resolver, CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>Same as <see cref="Source"/> without blocking the calling thread.</summary>
    /// <param name="origin">How the job reached LabelScope.</param>
    /// <param name="remoteAddress">The sender's IP address as text; null or empty means this computer.</param>
    /// <param name="resolver">Looks up host names; null uses <see cref="HostNameResolver.Default"/> (system DNS).</param>
    /// <param name="cancellationToken">Stops waiting; the IP address is shown.</param>
    public static async Task<string> SourceAsync(JobOrigin origin, string? remoteAddress, HostNameResolver? resolver = null,
        CancellationToken cancellationToken = default)
    {
        string? host = null;
        if (NeedsLookup(origin, remoteAddress))
            host = await (resolver ?? HostNameResolver.Default).ResolveAsync(remoteAddress!, cancellationToken).ConfigureAwait(false);
        return DisplaySource(origin, remoteAddress, host);
    }

    /// <summary>
    /// The source text from facts already known, without asking DNS: the localized text for a file, a paste or this
    /// computer; otherwise <paramref name="resolvedHost"/> when known, else the address. Looked up on every call, so it
    /// follows a language switch.
    /// </summary>
    /// <param name="origin">How the job reached LabelScope.</param>
    /// <param name="remoteAddress">The sender's IP address as text; null or empty means this computer.</param>
    /// <param name="resolvedHost">The short host name found earlier, or null.</param>
    public static string DisplaySource(JobOrigin origin, string? remoteAddress, string? resolvedHost)
    {
        switch (origin)
        {
            case JobOrigin.OpenedFromFile: return Text.Get("Job_OpenedFromFile");
            case JobOrigin.Pasted: return Text.Get("Job_Pasted");
        }
        // The Windows printer always sends from this computer; a missing address can only mean the same.
        if (string.IsNullOrWhiteSpace(remoteAddress)) return Text.Get("Job_ThisComputer");
        if (!HostNameResolver.TryParseAddress(remoteAddress, out var address, out var loopback)) return remoteAddress.Trim();
        if (loopback) return Text.Get("Job_ThisComputer");
        return string.IsNullOrWhiteSpace(resolvedHost) ? address!.ToString() : resolvedHost;
    }

    /// <summary>True for a printed job from another computer: only then is a host name worth looking up.</summary>
    public static bool NeedsLookup(JobOrigin origin, string? remoteAddress) =>
        origin == JobOrigin.Printed
        && HostNameResolver.TryParseAddress(remoteAddress, out _, out var loopback) && !loopback;
}

/// <summary>
/// Turns a sender's IP address into its short host name ("pc1.corp.local" gives "pc1") with a time limit, and
/// remembers every answer for the session, so a busy sender is looked up once instead of costing up to 300 ms per job.
/// A failed or timed-out lookup is remembered too: that sender shows its IP address until LabelScope restarts.
/// </summary>
public sealed class HostNameResolver
{
    /// <summary>The resolver used by <see cref="JobNamer"/>: the system DNS with a 300 ms limit.</summary>
    public static HostNameResolver Default { get; } = new(SystemLookup, TimeSpan.FromMilliseconds(300));

    // A hostile network could send from thousands of addresses; past this the cache starts over rather than growing.
    private const int MaxCached = 1000;

    private readonly Func<IPAddress, CancellationToken, Task<string?>> _lookup;
    private readonly TimeSpan _timeout;
    private readonly ConcurrentDictionary<string, string?> _cache = new();

    /// <summary>Creates a resolver with its own lookup, so the way names are found (and how long that may take) can be
    /// chosen by the caller instead of always asking DNS.</summary>
    /// <param name="lookup">Returns the host name of an address, or null when it has none.</param>
    /// <param name="timeout">How long to wait for <paramref name="lookup"/> before giving up.</param>
    public HostNameResolver(Func<IPAddress, CancellationToken, Task<string?>> lookup, TimeSpan timeout)
    {
        _lookup = lookup ?? throw new ArgumentNullException(nameof(lookup));
        _timeout = timeout;
    }

    /// <summary>
    /// True when <paramref name="remoteAddress"/> was looked up before in this session; <paramref name="host"/> is then
    /// its short name, or null when it has none.
    /// </summary>
    public bool TryGetCached(string remoteAddress, out string? host)
    {
        host = null;
        return TryParseAddress(remoteAddress, out var address, out _) && _cache.TryGetValue(address!.ToString(), out host);
    }

    /// <summary>
    /// The short host name of <paramref name="remoteAddress"/>, or null when it has none, is not an IP address, is this
    /// computer, or the lookup fails or takes longer than the time limit. Never throws.
    /// </summary>
    public async Task<string?> ResolveAsync(string remoteAddress, CancellationToken cancellationToken = default)
    {
        if (!TryParseAddress(remoteAddress, out var address, out var loopback) || loopback) return null;
        var key = address!.ToString();
        if (_cache.TryGetValue(key, out var cached)) return cached;

        string? host;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            // WaitAsync enforces the limit even when the lookup ignores its token (Windows DNS often does).
            limit.CancelAfter(_timeout);
            host = ShortHostName(await _lookup(address, limit.Token).WaitAsync(_timeout, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            return null; // the caller gave up; that says nothing about the sender, so nothing is remembered
        }
        catch (Exception)
        {
            host = null; // a timeout, no PTR record, or no network: the IP address is still a correct source
        }
        if (_cache.Count >= MaxCached) _cache.Clear();
        _cache[key] = host;
        return host;
    }

    /// <summary>Parses an IP address and says whether it is this computer (an IPv4 address in IPv6 form included).</summary>
    internal static bool TryParseAddress(string? text, out IPAddress? address, out bool loopback)
    {
        loopback = false;
        if (!IPAddress.TryParse((text ?? "").Trim(), out address)) return false;
        // An IPv4 sender reaching a dual-mode socket shows up as ::ffff:127.0.0.1, which IsLoopback does not catch.
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        loopback = IPAddress.IsLoopback(address);
        return true;
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
