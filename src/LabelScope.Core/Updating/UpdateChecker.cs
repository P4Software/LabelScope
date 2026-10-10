using System.Net.Http.Headers;
using System.Text.Json;

namespace LabelScope.Core.Updating;

/// <summary>A newer LabelScope release that can be installed.</summary>
/// <param name="Version">The release version, for example 0.1.2.</param>
/// <param name="DownloadUrl">Where the installer file can be downloaded (an https GitHub address).</param>
/// <param name="ReleasePage">The release page, for the "what changed" text.</param>
public sealed record UpdateInfo(Version Version, Uri DownloadUrl, Uri ReleasePage);

/// <summary>What a check for updates found.</summary>
/// <param name="Update">The newer release, or null when the installed version is the newest.</param>
/// <param name="Problem">Plain-language reason when the check could not be completed; null when it worked.</param>
public sealed record UpdateCheckResult(UpdateInfo? Update, string? Problem);

/// <summary>
/// Asks GitHub whether a newer LabelScope release exists. Only the public "latest release" address is
/// requested; nothing about the user, the computer or any label is sent.
/// </summary>
public sealed class UpdateChecker
{
    /// <summary>Public address that describes the newest release.</summary>
    public const string LatestReleaseUrl = "https://api.github.com/repos/P4Software/LabelScope/releases/latest";

    /// <summary>File name of the installer attached to every release; the updater looks for exactly this asset.</summary>
    public const string InstallerAssetName = "LabelScope-Setup.exe";

    private readonly HttpClient _http;

    /// <summary>Creates the checker. A client can be supplied so tests never touch the network.</summary>
    public UpdateChecker(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    }

    /// <summary>Checks once. Never throws: a missing network connection is a normal situation for an offline tool.</summary>
    public async Task<UpdateCheckResult> CheckAsync(Version installed, CancellationToken cancel = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUrl);
            // GitHub rejects requests without a User-Agent.
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("LabelScope", installed.ToString(3)));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            using var response = await _http.SendAsync(request, cancel).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return new(null, Text.Get("Update_HttpStatus", (int)response.StatusCode));

            var json = await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false);
            return Parse(json, installed);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException)
        {
            return new(null, Text.Get("Update_Unreachable"));
        }
    }

    /// <summary>Reads the GitHub release description. Public so the rules can be tested without a network.</summary>
    public static UpdateCheckResult Parse(string json, Version installed)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Draft and pre-release builds are not offered to users.
        if (root.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) return new(null, null);
        if (root.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True) return new(null, null);

        var tag = root.TryGetProperty("tag_name", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        if (!TryParseVersion(tag, out var latest))
            return new(null, Text.Get("Update_BadVersionName"));
        if (latest <= installed) return new(null, null);

        Uri? download = null;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            foreach (var asset in assets.EnumerateArray())
                if (asset.TryGetProperty("name", out var n) && n.GetString() == InstallerAssetName &&
                    asset.TryGetProperty("browser_download_url", out var u) &&
                    Uri.TryCreate(u.GetString(), UriKind.Absolute, out var parsed))
                    download = parsed;

        // Only https addresses on GitHub are followed, so a tampered answer cannot point the updater elsewhere.
        if (download is null || download.Scheme != Uri.UriSchemeHttps || !IsGitHubHost(download.Host))
            return new(null, Text.Get("Update_NoInstaller", latest.ToString(3)));

        var page = root.TryGetProperty("html_url", out var h) && Uri.TryCreate(h.GetString(), UriKind.Absolute, out var hp)
            ? hp : new Uri("https://github.com/P4Software/LabelScope/releases");
        return new(new UpdateInfo(latest, download, page), null);
    }

    /// <summary>Turns "v0.1.2" or "0.1.2" into a version; false for anything else.</summary>
    public static bool TryParseVersion(string? text, out Version version)
    {
        version = new Version(0, 0);
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (!Version.TryParse(text.Trim().TrimStart('v', 'V'), out var parsed)) return false;
        version = parsed;
        return true;
    }

    // GitHub serves release files from github.com and, after a redirect, githubusercontent.com.
    private static bool IsGitHubHost(string host) =>
        host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase);
}
