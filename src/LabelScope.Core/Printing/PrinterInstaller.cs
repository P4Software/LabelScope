using System.Globalization;
using System.Text;
using System.Text.Json;

namespace LabelScope.Core.Printing;

/// <summary>Whether the LabelScope printer exists in Windows.</summary>
public enum PrinterStatus
{
    /// <summary>No printer with the configured name exists.</summary>
    NotInstalled,
    /// <summary>The printer exists and LabelScope created it.</summary>
    Installed,
    /// <summary>A printer with the configured name exists but was not created by LabelScope; it is never touched.</summary>
    NameTakenByOther,
    /// <summary>
    /// The check itself failed or timed out (PowerShell could not be started, for example), so nothing is known
    /// about the printer. Callers should show that the status could not be checked. Install and Remove stay
    /// available on purpose: both re-check the printer themselves and refuse safely when they still cannot tell.
    /// </summary>
    Unknown,
}

/// <summary>Creates and removes the Windows printer that forwards print jobs to LabelScope's listener.</summary>
/// <remarks>
/// None of the async methods throws except for cancellation through the caller's token: every other problem
/// (a runner failure, a declined permission prompt, a script error) comes back as a result with plain text.
/// </remarks>
public sealed class PrinterInstaller
{
    /// <summary>Longest accepted printer name. Kept short because the elevated script travels on a command line of limited length.</summary>
    internal const int MaxNameLength = 60;

    /// <summary>
    /// Stored in the printer's Comment so we can tell our printer from someone else's with the same name.
    /// </summary>
    internal const string OwnerMarker = "Created by LabelScope";

    // The built-in driver passes the job bytes through untouched, so ZPL arrives as ZPL,
    // and it ships with Windows, so there is nothing to license or redistribute.
    private const string DriverName = "Generic / Text Only";
    private const string Address = "127.0.0.1";

    // Characters refused in a printer name: Windows rejects backslash and slash, '!' is refused as well,
    // and * ? [ ] are wildcards that Get-Printer -Name would expand, so a name could match (and later remove)
    // somebody else's printer.
    private static readonly char[] ForbiddenNameChars = { (char)92, (char)47, '!', '*', '?', '[', ']' };

    /// <summary>How long the status check may take before it is given up. The check needs no permission prompt, so 15 seconds is plenty.</summary>
    internal TimeSpan StatusTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How long an install or remove may take before it is given up. Longer than the status check because the
    /// user may need a while to answer the Windows permission prompt.
    /// </summary>
    internal TimeSpan ElevatedTimeout { get; init; } = TimeSpan.FromSeconds(120);

    private readonly IPowerShellRunner _runner;
    private readonly string _name;
    private readonly int _port;
    private readonly string _portName;

    /// <summary>Creates an installer for printer <paramref name="printerName"/> forwarding to local <paramref name="port"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="runner"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// The name is blank, longer than 60 characters, or contains a control character (such as a line break),
    /// a backslash, a slash, an exclamation mark or one of the wildcard characters * ? [ ].
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">The port is not between 1 and 65535.</exception>
    public PrinterInstaller(IPowerShellRunner runner, string printerName, int port)
    {
        ArgumentNullException.ThrowIfNull(runner);

        // Script injection is already impossible because the name reaches PowerShell only as base64
        // (see Header). The checks below exist for the user: a clear message that names the setting to fix
        // is far better than a cryptic PowerShell failure after a Windows permission prompt.
        if (string.IsNullOrWhiteSpace(printerName))
            throw new ArgumentException("PrinterName in settings.json is empty. Type a name for the printer and start LabelScope again.", nameof(printerName));
        if (printerName.Length > MaxNameLength)
            throw new ArgumentException($"PrinterName in settings.json is too long ({MaxNameLength} characters at most). Shorten it and start LabelScope again.", nameof(printerName));
        if (printerName.Any(char.IsControl))
            throw new ArgumentException("PrinterName in settings.json must be a single line of normal text, without line breaks or hidden characters. Edit it and start LabelScope again.", nameof(printerName));
        if (printerName.IndexOfAny(ForbiddenNameChars) >= 0)
            throw new ArgumentException("PrinterName in settings.json must not contain a backslash, a slash, an exclamation mark, an asterisk, a question mark or a square bracket. Remove them and start LabelScope again.", nameof(printerName));
        if (port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port), port,
                "The port in settings.json must be a number between 1 and 65535. Edit it and start LabelScope again.");

        _runner = runner;
        _name = printerName;
        _port = port;
        _portName = string.Create(CultureInfo.InvariantCulture, $"LabelScope-{port}");
    }

    /// <summary>
    /// Looks the printer up. Needs no administrator rights. Returns <see cref="PrinterStatus.Unknown"/> when the
    /// check itself failed; only cancellation through <paramref name="ct"/> is thrown.
    /// </summary>
    public async Task<PrinterStatus> GetStatusAsync(CancellationToken ct = default)
    {
        // Comments about the scripts stay out here: every character inside them is paid for on the command line.
        var script = Header() +
                     "$p=Get-Printer -Name $n -EA SilentlyContinue;" +
                     "if($p){$p|Select-Object Name,Comment,PortName|ConvertTo-Json -Compress}";

        // The runner gets a token that fires after StatusTimeout; it then ends (and kills) its hidden PowerShell.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(StatusTimeout);

        string output;
        try
        {
            output = Clean(await _runner.RunAsync(script, elevated: false, timeout.Token));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // A UAC decline cannot happen for a non-elevated run, so anything here is a real failure;
            // a timeout (the token fired without the caller cancelling) lands here as well.
            return PrinterStatus.Unknown;
        }

        if (output.Length == 0) return PrinterStatus.NotInstalled;

        try
        {
            using var doc = JsonDocument.Parse(output);
            // Only a JSON object can be our printer. An array, a number or null means PowerShell printed
            // something unexpected, so be safe and treat the name as taken.
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return PrinterStatus.NameTakenByOther;

            var comment = doc.RootElement.TryGetProperty("Comment", out var c) && c.ValueKind == JsonValueKind.String
                ? c.GetString()
                : null;
            return comment == OwnerMarker ? PrinterStatus.Installed : PrinterStatus.NameTakenByOther;
        }
        catch (JsonException)
        {
            // Unexpected output means PowerShell printed something else (an error text); do not guess.
            return PrinterStatus.NameTakenByOther;
        }
    }

    /// <summary>Installs the printer (one Windows permission prompt).</summary>
    public async Task<OperationResult> InstallAsync(CancellationToken ct = default)
    {
        switch (await GetStatusAsync(ct))
        {
            case PrinterStatus.Installed:
                return OperationResult.Ok($"The printer \"{_name}\" is already installed. You can print to it from any program.");
            case PrinterStatus.NameTakenByOther:
                return OperationResult.Fail($"A printer named \"{_name}\" already exists and was not created by LabelScope, so it was left alone. " +
                                            "Choose a different PrinterName in settings.json and try again.");
            case PrinterStatus.Unknown:
                return StatusCheckFailed();
        }

        // The script re-checks ownership itself: the status above was read before the user spent time on the
        // permission prompt, and the privileged step must not rely on a stale answer.
        // Stop on every error (non-terminating errors skip catch and could end in a false 'OK'),
        // and send command output to Out-Null so nothing but the final marker can precede 'OK'.
        var script = ElevatedHeader() +
                     "try{$x=Get-Printer -Name $n -EA SilentlyContinue;" +
                     $"if($x){{if($x.Comment -ne '{OwnerMarker}'){{throw 'Another printer already uses this name.'}}}}" +
                     "else{if(!(Get-PrinterPort -Name $o -EA SilentlyContinue)){" +
                     $"Add-PrinterPort -Name $o -PrinterHostAddress '{Address}' -PortNumber {_port.ToString(CultureInfo.InvariantCulture)}|Out-Null}};" +
                     // The text-only driver ships with Windows but can be switched off; add it when it is missing.
                     $"if(!(Get-PrinterDriver -Name '{DriverName}' -EA SilentlyContinue)){{Add-PrinterDriver -Name '{DriverName}'|Out-Null}};" +
                     $"Add-Printer -Name $n -DriverName '{DriverName}' -PortName $o -Comment '{OwnerMarker}'|Out-Null}};" +
                     "'OK'}catch{'ERROR: '+$_.Exception.Message}";

        return await RunElevated(script,
            ok: $"The printer \"{_name}\" is installed. Print to it from any program and the label appears in LabelScope.",
            failPrefix: "The printer could not be installed", ct);
    }

    /// <summary>Removes the printer and its port, but only if LabelScope created them.</summary>
    public async Task<OperationResult> RemoveAsync(CancellationToken ct = default)
    {
        switch (await GetStatusAsync(ct))
        {
            case PrinterStatus.NotInstalled:
                return OperationResult.Ok($"The printer \"{_name}\" is not installed, so there is nothing to remove.");
            case PrinterStatus.NameTakenByOther:
                return OperationResult.Fail($"A printer named \"{_name}\" exists but was not created by LabelScope, so it was left alone.");
            case PrinterStatus.Unknown:
                return StatusCheckFailed();
        }

        // Ownership is checked again inside the elevated script (see InstallAsync). The port is removed only
        // when no other printer still uses it, and a failure to remove the port is ignored on purpose: the
        // printer is already gone, and reporting failure would make a retry answer "nothing to remove".
        var script = ElevatedHeader() +
                     "try{$x=Get-Printer -Name $n -EA SilentlyContinue;" +
                     $"if($x){{if($x.Comment -ne '{OwnerMarker}'){{throw 'This printer was not created by LabelScope.'}}" +
                     "Remove-Printer -Name $n|Out-Null;" +
                     "try{if(!(Get-Printer|Where-Object PortName -eq $o)){Remove-PrinterPort -Name $o|Out-Null}}catch{}};" +
                     "'OK'}catch{'ERROR: '+$_.Exception.Message}";

        return await RunElevated(script, ok: $"The printer \"{_name}\" was removed.", failPrefix: "The printer could not be removed", ct);
    }

    private static OperationResult StatusCheckFailed() =>
        OperationResult.Fail("LabelScope could not check which printers are installed, so nothing was changed. " +
                             "Check that the Windows \"Print Spooler\" service is running, then try again.");

    private async Task<OperationResult> RunElevated(string script, string ok, string failPrefix, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ElevatedTimeout);

        string output;
        try
        {
            output = Clean(await _runner.RunAsync(script, elevated: true, timeout.Token));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The caller cancelled; that is not the user declining Windows' prompt, so let it travel on.
            throw;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            // Our own timer fired, so this is not a declined prompt either. Say what happened and what to do.
            return OperationResult.Fail($"{failPrefix}: Windows did not finish in time (more than {(int)ElevatedTimeout.TotalSeconds} seconds). " +
                                        "Nothing may have been changed. Press the button again and answer the Windows prompt promptly.");
        }
        catch (OperationCanceledException)
        {
            return OperationResult.Fail("Windows asked for permission and it was not given, so nothing was changed. " +
                                        "Press the button again and choose Yes when Windows asks.");
        }
        catch (Exception ex)
        {
            // The runner reports its own failures with plain-language messages.
            return OperationResult.Fail($"{failPrefix}: {EndWithPeriod(ex.Message)}");
        }

        if (output.StartsWith("OK", StringComparison.Ordinal)) return OperationResult.Ok(ok);

        var reason = output.StartsWith("ERROR:", StringComparison.Ordinal) ? output[6..].Trim() : "";
        // Blaming the spooler for an unrelated error (a missing driver, say) would send the user to the wrong place,
        // so the spooler advice appears only when the message mentions it or there is no message at all.
        var blamesSpooler = reason.Length == 0 || reason.Contains("spooler", StringComparison.OrdinalIgnoreCase);
        var shown = reason.Length == 0 ? "Windows gave no answer." : EndWithPeriod(reason);
        var advice = blamesSpooler
            ? "Check that the Windows \"Print Spooler\" service is running, then try again."
            : "Try again. If it keeps failing, show this message to your administrator.";
        return OperationResult.Fail($"{failPrefix}: {shown} {advice}");
    }

    /// <summary>Adds a final period unless the text already ends with one, so messages never show "..".</summary>
    private static string EndWithPeriod(string text)
    {
        text = text.Trim();
        return text.EndsWith('.') ? text : text + ".";
    }

    /// <summary>
    /// Strips a UTF-8 byte order mark and surrounding blank space from script output. Windows PowerShell 5.1
    /// writes a BOM with <c>Out-File -Encoding utf8</c>, and <c>string.Trim()</c> does not treat U+FEFF as
    /// whitespace, so without this a BOM-prefixed "OK" would be reported as a failure.
    /// </summary>
    private static string Clean(string? output) => (output ?? "").Trim().Trim('\uFEFF').Trim();

    /// <summary>
    /// Defines <c>$n</c> (printer name) and <c>$o</c> (port name) at the top of every script. The name is passed
    /// as base64 data and decoded inside PowerShell, so no variable text ever becomes script text: base64 holds
    /// only A-Z a-z 0-9 + / =, which cannot break out of a quoted string (PowerShell also treats the Unicode
    /// quotes U+2018 to U+201B as quotes, which escaping alone would have to chase). The port name is built from
    /// an integer and a fixed prefix, so it is safe as a plain literal. Variable names are short on purpose:
    /// the script is sent on a command line of limited length.
    /// </summary>
    private string Header() => $"$n={DecodeExpression(_name)};$o='{_portName}';";

    /// <summary>Header for scripts that change things: any error must stop the script instead of being skipped.</summary>
    private string ElevatedHeader() => "$ErrorActionPreference='Stop';" + Header();

    /// <summary>Builds the PowerShell expression that rebuilds <paramref name="value"/> from its base64 UTF-8 bytes.</summary>
    internal static string DecodeExpression(string value) =>
        "[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" +
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value)) + "'))";
}
