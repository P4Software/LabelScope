// src/LabelScope.Core/Printing/PrinterInstaller.cs
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
}

/// <summary>Creates and removes the Windows printer that forwards print jobs to LabelScope's listener.</summary>
public sealed class PrinterInstaller
{
    /// <summary>
    /// Stored in the printer's Comment so we can tell our printer from someone else's with the same name.
    /// </summary>
    internal const string OwnerMarker = "Created by LabelScope";

    // The built-in driver passes the job bytes through untouched, so ZPL arrives as ZPL,
    // and it ships with Windows, so there is nothing to license or redistribute.
    private const string DriverName = "Generic / Text Only";
    private const string Address = "127.0.0.1";
    private static readonly char[] ForbiddenNameChars = { (char)92, (char)47, (char)33 };

    private readonly IPowerShellRunner _runner;
    private readonly string _name;
    private readonly int _port;
    private readonly string _portName;

    /// <summary>Creates an installer for printer <paramref name="printerName"/> forwarding to local <paramref name="port"/>.</summary>
    /// <exception cref="ArgumentException">
    /// The name is blank, longer than 200 characters, or contains a control character (such as a line break),
    /// a backslash, a slash or an exclamation mark.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">The port is not between 1 and 65535.</exception>
    public PrinterInstaller(IPowerShellRunner runner, string printerName, int port)
    {
        // Script injection is already impossible because variable values reach PowerShell only as base64
        // (see Header). The checks below exist for the user: a clear message that names the setting to fix
        // is far better than a cryptic PowerShell failure after a Windows permission prompt. Windows itself
        // rejects backslash and slash in printer names, and '!' is refused too.
        if (string.IsNullOrWhiteSpace(printerName))
            throw new ArgumentException("PrinterName in settings.json is empty. Type a name for the printer and start LabelScope again.", nameof(printerName));
        if (printerName.Length > 200)
            throw new ArgumentException("PrinterName in settings.json is too long (200 characters at most). Shorten it and start LabelScope again.", nameof(printerName));
        if (printerName.Any(char.IsControl))
            throw new ArgumentException("PrinterName in settings.json must be a single line of normal text, without line breaks or hidden characters. Edit it and start LabelScope again.", nameof(printerName));
        if (printerName.IndexOfAny(ForbiddenNameChars) >= 0)
            throw new ArgumentException("PrinterName in settings.json must not contain a backslash, a slash or an exclamation mark. Remove them and start LabelScope again.", nameof(printerName));
        if (port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port), port,
                "The port in settings.json must be a number between 1 and 65535. Edit it and start LabelScope again.");

        _runner = runner;
        _name = printerName;
        _port = port;
        _portName = $"LabelScope-{port}";
    }

    /// <summary>Looks the printer up. Needs no administrator rights.</summary>
    public async Task<PrinterStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var script = Header() +
                     "$p = Get-Printer -Name $printerName -ErrorAction SilentlyContinue\n" +
                     "if ($p) { $p | Select-Object Name,Comment,PortName | ConvertTo-Json -Compress }";
        var output = Clean(await _runner.RunAsync(script, elevated: false, ct));
        if (output.Length == 0) return PrinterStatus.NotInstalled;

        try
        {
            using var doc = JsonDocument.Parse(output);
            var comment = doc.RootElement.TryGetProperty("Comment", out var c) ? c.GetString() : null;
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
        }

        var script = Header() + $$"""
            try {
              if (-not (Get-PrinterPort -Name $portName -ErrorAction SilentlyContinue)) {
                Add-PrinterPort -Name $portName -PrinterHostAddress '{{Address}}' -PortNumber {{_port.ToString(CultureInfo.InvariantCulture)}}
              }
              Add-Printer -Name $printerName -DriverName '{{DriverName}}' -PortName $portName -Comment '{{OwnerMarker}}'
              'OK'
            } catch { 'ERROR: ' + $_.Exception.Message }
            """;

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
        }

        var script = Header() + $$"""
            try {
              Remove-Printer -Name $printerName
              if (Get-PrinterPort -Name $portName -ErrorAction SilentlyContinue) { Remove-PrinterPort -Name $portName }
              'OK'
            } catch { 'ERROR: ' + $_.Exception.Message }
            """;

        return await RunElevated(script, ok: $"The printer \"{_name}\" was removed.", failPrefix: "The printer could not be removed", ct);
    }

    private async Task<OperationResult> RunElevated(string script, string ok, string failPrefix, CancellationToken ct)
    {
        string output;
        try
        {
            output = Clean(await _runner.RunAsync(script, elevated: true, ct));
        }
        catch (OperationCanceledException)
        {
            return OperationResult.Fail("Windows asked for permission and it was not given, so nothing was changed. " +
                                        "Press the button again and choose Yes when Windows asks.");
        }

        if (output.StartsWith("OK", StringComparison.Ordinal)) return OperationResult.Ok(ok);

        var reason = output.StartsWith("ERROR:", StringComparison.Ordinal) ? output[6..].Trim() : "Windows gave no answer";
        return OperationResult.Fail($"{failPrefix}: {reason}. Check that the Windows \"Print Spooler\" service is running, then try again.");
    }

    /// <summary>
    /// Strips a UTF-8 byte order mark and surrounding blank space from script output. Windows PowerShell 5.1
    /// writes a BOM with <c>Out-File -Encoding utf8</c>, and <c>string.Trim()</c> does not treat U+FEFF as
    /// whitespace, so without this a BOM-prefixed "OK" would be reported as a failure.
    /// </summary>
    private static string Clean(string? output) => (output ?? "").Trim().Trim('\uFEFF').Trim();

    /// <summary>
    /// Defines <c>$printerName</c> and <c>$portName</c> at the top of every script. Values are passed as
    /// base64 data and decoded inside PowerShell, so no variable text ever becomes script text: base64 holds
    /// only A-Z a-z 0-9 + / =, which cannot break out of a quoted string (PowerShell also treats the Unicode
    /// quotes U+2018 to U+201B as quotes, which escaping alone would have to chase). The scripts can run elevated.
    /// </summary>
    private string Header() =>
        $"$printerName = {DecodeExpression(_name)}\n$portName = {DecodeExpression(_portName)}\n";

    /// <summary>Builds the PowerShell expression that rebuilds <paramref name="value"/> from its base64 UTF-8 bytes.</summary>
    internal static string DecodeExpression(string value) =>
        "[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" +
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value)) + "'))";
}
