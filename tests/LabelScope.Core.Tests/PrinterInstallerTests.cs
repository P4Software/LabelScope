// tests/LabelScope.Core.Tests/PrinterInstallerTests.cs
using LabelScope.Core.Printing;
using Xunit;

namespace LabelScope.Core.Tests;

public sealed class PrinterInstallerTests
{
    private sealed class FakeRunner : IPowerShellRunner
    {
        public string StatusOutput = "";
        public string ElevatedOutput = "OK";
        public bool UserDeclines;
        public int ElevatedCalls;
        public string? LastElevatedScript;
        public Exception? ThrowOnStatus;
        public Exception? ThrowOnElevated;

        public Task<string> RunAsync(string script, bool elevated, CancellationToken ct = default)
        {
            if (!elevated)
            {
                if (ThrowOnStatus != null) throw ThrowOnStatus;
                return Task.FromResult(StatusOutput);
            }
            ElevatedCalls++;
            LastElevatedScript = script;
            if (ThrowOnElevated != null) throw ThrowOnElevated;
            if (UserDeclines) throw new OperationCanceledException();
            return Task.FromResult(ElevatedOutput);
        }
    }

    private const string Ours =
        "{\"Name\":\"P4 LabelScope Printer\",\"Comment\":\"Created by LabelScope\",\"PortName\":\"LabelScope-9100\"}";
    private const string Foreign =
        "{\"Name\":\"P4 LabelScope Printer\",\"Comment\":\"Office printer\",\"PortName\":\"IP_10.0.0.5\"}";

    private static PrinterInstaller Make(FakeRunner r) => new(r, "P4 LabelScope Printer", 9100);

    [Fact]
    public async Task Status_NotInstalled_WhenNothingIsReturned()
    {
        Assert.Equal(PrinterStatus.NotInstalled, await Make(new FakeRunner()).GetStatusAsync());
    }

    [Fact]
    public async Task Status_Installed_WhenCommentMarksItAsOurs()
    {
        Assert.Equal(PrinterStatus.Installed, await Make(new FakeRunner { StatusOutput = Ours }).GetStatusAsync());
    }

    [Fact]
    public async Task Status_NameTakenByOther_WhenSameNameButNotOurs()
    {
        Assert.Equal(PrinterStatus.NameTakenByOther, await Make(new FakeRunner { StatusOutput = Foreign }).GetStatusAsync());
    }

    [Fact]
    public async Task Install_Succeeds_AndScriptUsesTextOnlyDriverOnLoopbackPort()
    {
        var runner = new FakeRunner();

        var result = await Make(runner).InstallAsync();

        Assert.True(result.Success);
        Assert.Contains("installed", result.Message);
        Assert.Contains("Generic / Text Only", runner.LastElevatedScript);
        Assert.Contains("127.0.0.1", runner.LastElevatedScript);
        Assert.Contains("9100", runner.LastElevatedScript);
        Assert.Contains("Created by LabelScope", runner.LastElevatedScript);
    }

    [Fact]
    public async Task Install_RefusesToTouchAForeignPrinter_AndNeverElevates()
    {
        var runner = new FakeRunner { StatusOutput = Foreign };

        var result = await Make(runner).InstallAsync();

        Assert.False(result.Success);
        Assert.Contains("PrinterName", result.Message);
        Assert.Equal(0, runner.ElevatedCalls);
    }

    [Fact]
    public async Task Install_WhenAlreadyInstalled_IsAHarmlessSuccess()
    {
        var runner = new FakeRunner { StatusOutput = Ours };

        var result = await Make(runner).InstallAsync();

        Assert.True(result.Success);
        Assert.Equal(0, runner.ElevatedCalls);
    }

    [Fact]
    public async Task Install_WhenUserDeclinesPermission_ExplainsWhatToDo()
    {
        var result = await Make(new FakeRunner { UserDeclines = true }).InstallAsync();

        Assert.False(result.Success);
        Assert.Contains("permission", result.Message);
    }

    [Fact]
    public async Task Install_WhenScriptReportsError_ShowsTheCause()
    {
        var result = await Make(new FakeRunner { ElevatedOutput = "ERROR: The spooler service is not running" }).InstallAsync();

        Assert.False(result.Success);
        Assert.Contains("spooler", result.Message);
    }

    [Fact]
    public async Task Remove_OnlyRemovesOurOwnPrinter()
    {
        var foreign = new FakeRunner { StatusOutput = Foreign };
        var r1 = await Make(foreign).RemoveAsync();
        Assert.False(r1.Success);
        Assert.Equal(0, foreign.ElevatedCalls);

        var ours = new FakeRunner { StatusOutput = Ours };
        var r2 = await Make(ours).RemoveAsync();
        Assert.True(r2.Success);
        Assert.Equal(1, ours.ElevatedCalls);
    }

    // ---- Injection safety: values reach PowerShell only as base64 data --------------------------

    private static string Base64(string value) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value));

    /// <summary>Names a user could type, including every character PowerShell may treat as a quote.</summary>
    public static IEnumerable<object[]> HostileNames() => new[]
    {
        "P4 LabelScope Printer",
        "Bob's Labels",
        "A\u2018B", "A\u2019B", "A\u201AB", "A\u201BB",
        "A\"B", "A$B", "A`B", "A;B",
        "x'; Remove-Item -Recurse; '",
    }.Select(n => new object[] { n });

    [Theory]
    [MemberData(nameof(HostileNames))]
    public async Task InstallScript_NeverContainsTheRawName_OnlyItsBase64(string name)
    {
        var runner = new FakeRunner();

        await new PrinterInstaller(runner, name, 9100).InstallAsync();

        Assert.DoesNotContain(name, runner.LastElevatedScript);
        Assert.Contains(Base64(name), runner.LastElevatedScript);
    }

    [Theory]
    [MemberData(nameof(HostileNames))]
    public async Task RemoveScript_NeverContainsTheRawName_OnlyItsBase64(string name)
    {
        var runner = new FakeRunner { StatusOutput = Ours };

        await new PrinterInstaller(runner, name, 9100).RemoveAsync();

        Assert.DoesNotContain(name, runner.LastElevatedScript);
        Assert.Contains(Base64(name), runner.LastElevatedScript);
    }

    [Theory]
    [MemberData(nameof(HostileNames))]
    public async Task StatusScript_NeverContainsTheRawName_OnlyItsBase64(string name)
    {
        var runner = new CapturingRunner();

        await new PrinterInstaller(runner, name, 9100).GetStatusAsync();

        Assert.DoesNotContain(name, runner.LastScript);
        Assert.Contains(Base64(name), runner.LastScript);
    }

    [Fact]
    public async Task HostileName_CannotSmuggleCommandsIntoTheScript()
    {
        var runner = new FakeRunner();

        await new PrinterInstaller(runner, "x'; Remove-Item -Recurse; '", 9100).InstallAsync();

        Assert.DoesNotContain("Remove-Item", runner.LastElevatedScript);
    }

    [Fact]
    public async Task PortNumber_IsWrittenAsPlainDigits()
    {
        var runner = new FakeRunner();

        await new PrinterInstaller(runner, "P", 9100).InstallAsync();

        Assert.Contains("-PortNumber 9100", runner.LastElevatedScript);
    }

    /// <summary>Records the script of the non-elevated (status) call.</summary>
    private sealed class CapturingRunner : IPowerShellRunner
    {
        public string? LastScript;

        public Task<string> RunAsync(string script, bool elevated, CancellationToken ct = default)
        {
            LastScript = script;
            return Task.FromResult("");
        }
    }

    // ---- Constructor validation ------------------------------------------------------------------
    // Decision: an unusable name or port is rejected once, in the constructor, with a plain-language message,
    // so Install, Remove and GetStatus can never run for it.

    [Theory]
    [InlineData("Line1\nLine2")]
    [InlineData("Line1\r\nLine2")]
    [InlineData("Tab\there")]
    [InlineData("Bell\a")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("A\\B")]
    [InlineData("A/B")]
    [InlineData("A!B")]
    [InlineData("A*B")]
    [InlineData("A?B")]
    [InlineData("A[B")]
    [InlineData("A]B")]
    public void UnusableName_IsRejectedWithAMessageNamingTheSetting(string name)
    {
        var ex = Assert.Throws<ArgumentException>(() => new PrinterInstaller(new FakeRunner(), name, 9100));
        Assert.Contains("PrinterName", ex.Message);
    }

    [Fact]
    public void NameOver60Characters_IsRejected_ButExactly60IsAccepted()
    {
        // 60 keeps the elevated command line short enough for ShellExecute (see the argument length tests).
        Assert.Throws<ArgumentException>(() => new PrinterInstaller(new FakeRunner(), new string('a', 61), 9100));
        _ = new PrinterInstaller(new FakeRunner(), new string('a', 60), 9100);
    }

    [Fact]
    public void NullRunner_IsRejected()
    {
        Assert.Throws<ArgumentNullException>(() => new PrinterInstaller(null!, "P", 9100));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void PortOutsideValidRange_IsRejected(int port)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new PrinterInstaller(new FakeRunner(), "P", port));
        Assert.Contains("65535", ex.Message);
    }

    // ---- Output parsing ----------------------------------------------------------------------------

    [Fact]
    public async Task Status_ToleratesBomAndBlankLinesAroundTheJson()
    {
        // Out-File -Encoding utf8 on Windows PowerShell 5.1 prefixes the text with a BOM.
        var runner = new FakeRunner { StatusOutput = "\uFEFF\r\n\r\n" + Ours + "\r\n\r\n" };

        Assert.Equal(PrinterStatus.Installed, await Make(runner).GetStatusAsync());
    }

    [Fact]
    public async Task Status_BomOnlyOutput_MeansNotInstalled()
    {
        Assert.Equal(PrinterStatus.NotInstalled, await Make(new FakeRunner { StatusOutput = "\uFEFF\r\n" }).GetStatusAsync());
    }

    [Fact]
    public async Task Install_BomPrefixedOk_IsSuccess()
    {
        var result = await Make(new FakeRunner { ElevatedOutput = "\uFEFFOK" }).InstallAsync();

        Assert.True(result.Success);
    }

    [Fact]
    public async Task Install_BomPrefixedError_StillShowsTheCause()
    {
        var result = await Make(new FakeRunner { ElevatedOutput = "\uFEFFERROR: The spooler service is not running" }).InstallAsync();

        Assert.False(result.Success);
        Assert.Contains("spooler", result.Message);
    }

    // ---- Hardening of the elevated scripts ----------------------------------------------------------

    private static async Task<string> RemoveScript()
    {
        var runner = new FakeRunner { StatusOutput = Ours };
        await Make(runner).RemoveAsync();
        return runner.LastElevatedScript!;
    }

    private static async Task<string> InstallScript()
    {
        var runner = new FakeRunner();
        await Make(runner).InstallAsync();
        return runner.LastElevatedScript!;
    }

    [Fact]
    public async Task ElevatedScripts_StopOnEveryError_AndKeepStrayOutputOutOfTheResult()
    {
        var install = await InstallScript();
        var remove = await RemoveScript();

        Assert.StartsWith("$ErrorActionPreference='Stop'", install);
        Assert.StartsWith("$ErrorActionPreference='Stop'", remove);
        Assert.Contains("Add-PrinterPort -Name $o -PrinterHostAddress '127.0.0.1' -PortNumber 9100|Out-Null", install);
        Assert.Contains("-Comment 'Created by LabelScope'|Out-Null", install);
        Assert.Contains("Remove-Printer -Name $n|Out-Null", remove);
        Assert.Contains("Remove-PrinterPort -Name $o|Out-Null", remove);
        Assert.EndsWith("'OK'}catch{'ERROR: '+$_.Exception.Message}", install);
        Assert.EndsWith("'OK'}catch{'ERROR: '+$_.Exception.Message}", remove);
    }

    [Fact]
    public async Task RemoveScript_ChecksOwnershipInsideTheElevatedScript_BeforeRemovingAnything()
    {
        var script = await RemoveScript();

        var check = script.IndexOf("$x.Comment -ne 'Created by LabelScope'", StringComparison.Ordinal);
        var remove = script.IndexOf("Remove-Printer -Name", StringComparison.Ordinal);
        Assert.True(check >= 0, "the script must compare the Comment with the owner marker");
        Assert.True(remove > check, "the ownership check must come before Remove-Printer");
        Assert.Contains("throw", script);
    }

    [Fact]
    public async Task InstallScript_ChecksOwnershipOfAnExistingNameInsideTheElevatedScript()
    {
        var script = await InstallScript();

        var check = script.IndexOf("$x.Comment -ne 'Created by LabelScope'", StringComparison.Ordinal);
        var add = script.IndexOf("Add-Printer -Name", StringComparison.Ordinal);
        Assert.True(check >= 0 && check < add);
    }

    [Fact]
    public async Task RemoveScript_RemovesThePortOnlyWhenNoPrinterUsesItAnymore_AndNeverFailsBecauseOfIt()
    {
        var script = await RemoveScript();

        // The port step has its own try/catch so that its failure cannot turn a removed printer into an error,
        // and it runs only when no remaining printer uses the port.
        Assert.Contains("try{if(!(Get-Printer|Where-Object PortName -eq $o)){Remove-PrinterPort -Name $o|Out-Null}}catch{}", script);
        Assert.True(script.IndexOf("Remove-Printer -Name", StringComparison.Ordinal) < script.IndexOf("Where-Object PortName", StringComparison.Ordinal));
    }

    [Fact]
    public void RunnerArguments_WidenOutFile_AndKeepThePathOutOfPlainText()
    {
        var args = PowerShellRunner.BuildArguments("'OK'", @"C:\Temp\x.txt");

        Assert.StartsWith("-NoProfile -Command \"&{'OK'}|Out-File", args);
        Assert.EndsWith("\"", args);
        Assert.Contains("-Width 4096", args);
        Assert.Contains("-NoClobber", args);
        Assert.DoesNotContain(@"C:\Temp\x.txt", args); // the path only travels as base64
    }

    [Fact]
    public async Task RealInstallerScripts_AreAcceptedByTheArgumentBuilder_ButScriptsWithQuotesOrNonAsciiAreNot()
    {
        // Plain text on the command line is only safe for pure ASCII without double quotes.
        _ = PowerShellRunner.BuildArguments(await InstallScript(), @"C:\Temp\x.txt");
        _ = PowerShellRunner.BuildArguments(await RemoveScript(), @"C:\Temp\x.txt");

        Assert.Throws<InvalidOperationException>(() => PowerShellRunner.BuildArguments("'a\"b'", @"C:\x"));
        Assert.Throws<InvalidOperationException>(() => PowerShellRunner.BuildArguments("'caf\u00E9'", @"C:\x"));
    }

    [Theory]
    [InlineData("P4 LabelScope Printer", false)]
    [InlineData("P4 LabelScope Printer", true)]
    [InlineData("012345678901234567890123456789012345678901234567890123456789", false)]
    [InlineData("012345678901234567890123456789012345678901234567890123456789", true)]
    public async Task CommandLine_StaysUnder2000Characters(string name, bool remove)
    {
        var runner = new FakeRunner { StatusOutput = remove ? Ours : "" };
        var installer = new PrinterInstaller(runner, name, 9100);
        if (remove) await installer.RemoveAsync(); else await installer.InstallAsync();

        // A deliberately long, realistic temp path (the real one is Path.GetTempPath() + LabelScope-<guid>.txt).
        var path = @"C:\Users\A.Very.Long.User.Name\AppData\Local\Temp\LabelScope-" + Guid.NewGuid().ToString("N") + ".txt";
        var length = PowerShellRunner.BuildArguments(runner.LastElevatedScript!, path).Length;

        Assert.True(length < 2000, $"command line is {length} characters");
    }

    // ---- Failures never escape as exceptions ---------------------------------------------------------

    [Fact]
    public async Task Status_JsonThatIsNotAnObject_IsTreatedAsTakenByOther()
    {
        foreach (var json in new[] { "[1,2]", "42", "null", "\"text\"", "true" })
            Assert.Equal(PrinterStatus.NameTakenByOther, await Make(new FakeRunner { StatusOutput = json }).GetStatusAsync());
    }

    [Fact]
    public async Task Status_NullOrNonTextComment_IsTreatedAsTakenByOther()
    {
        foreach (var json in new[] { "{\"Name\":\"x\",\"Comment\":null}", "{\"Name\":\"x\",\"Comment\":5}", "{\"Name\":\"x\"}" })
            Assert.Equal(PrinterStatus.NameTakenByOther, await Make(new FakeRunner { StatusOutput = json }).GetStatusAsync());
    }

    [Fact]
    public async Task Status_WhenTheRunnerFails_IsUnknown_NotAnException()
    {
        var runner = new FakeRunner { ThrowOnStatus = new InvalidOperationException("boom") };

        Assert.Equal(PrinterStatus.Unknown, await Make(runner).GetStatusAsync());
    }

    [Fact]
    public async Task InstallAndRemove_WhenTheStatusCheckFails_FailWithPlainTextAndNeverElevate()
    {
        var runner = new FakeRunner { ThrowOnStatus = new IOException("disk") };

        var install = await Make(runner).InstallAsync();
        var remove = await Make(runner).RemoveAsync();

        Assert.False(install.Success);
        Assert.False(remove.Success);
        Assert.Contains("could not check", install.Message);
        Assert.Equal(0, runner.ElevatedCalls);
    }

    [Fact]
    public async Task Install_WhenTheRunnerThrows_ShowsItsPlainMessage_WithoutDoublePeriod()
    {
        var runner = new FakeRunner
        {
            ThrowOnElevated = new InvalidOperationException("The printer name is too long for Windows to install. Use a shorter PrinterName in settings.json."),
        };

        var result = await Make(runner).InstallAsync();

        Assert.False(result.Success);
        Assert.Contains("too long", result.Message);
        Assert.DoesNotContain("..", result.Message);
    }

    [Fact]
    public async Task Install_ScriptErrorEndingInAPeriod_DoesNotGetASecondOne()
    {
        var result = await Make(new FakeRunner { ElevatedOutput = "ERROR: The spooler is stopped." }).InstallAsync();

        Assert.DoesNotContain("..", result.Message);
    }

    // ---- Timeouts --------------------------------------------------------------------------------------

    /// <summary>A runner that never answers on its own; it only ends when its token fires (as the real one does after killing PowerShell).</summary>
    private sealed class HangingRunner : IPowerShellRunner
    {
        public bool HangOnStatus = true;
        public bool HangOnElevated = true;
        public bool TokenWasCancelled;

        public async Task<string> RunAsync(string script, bool elevated, CancellationToken ct = default)
        {
            if (elevated ? !HangOnElevated : !HangOnStatus) return "";
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { TokenWasCancelled = true; throw; }
            return "";
        }
    }

    [Fact]
    public async Task Status_GivesUpAfterTheTimeout_AndIsUnknown()
    {
        var runner = new HangingRunner();
        var installer = new PrinterInstaller(runner, "P", 9100) { StatusTimeout = TimeSpan.FromMilliseconds(200) };

        var status = await installer.GetStatusAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(PrinterStatus.Unknown, status);
        Assert.True(runner.TokenWasCancelled, "the runner must be told to stop so it can end its PowerShell");
    }

    [Fact]
    public void DefaultTimeouts_Are15SecondsForStatusAnd120ForPermissionPrompts()
    {
        var installer = Make(new FakeRunner());

        Assert.Equal(TimeSpan.FromSeconds(15), installer.StatusTimeout);
        Assert.Equal(TimeSpan.FromSeconds(120), installer.ElevatedTimeout);
    }

    [Fact]
    public async Task Install_GivesUpAfterTheElevatedTimeout_WithAPlainMessage()
    {
        var runner = new HangingRunner { HangOnStatus = false };
        var installer = new PrinterInstaller(runner, "P", 9100) { ElevatedTimeout = TimeSpan.FromMilliseconds(200) };

        var result = await installer.InstallAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(result.Success);
        Assert.Contains("did not finish in time", result.Message);
        Assert.DoesNotContain("permission was not given", result.Message);
    }

    [Fact]
    public async Task Status_CallerCancellation_StillPropagates()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var installer = new PrinterInstaller(new HangingRunner(), "P", 9100);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => installer.GetStatusAsync(cts.Token));
    }

    // ---- Text-only driver and error advice -------------------------------------------------------------

    [Fact]
    public async Task InstallScript_AddsTheTextOnlyDriverWhenMissing_BeforeAddPrinter()
    {
        var script = await InstallScript();

        var check = script.IndexOf("if(!(Get-PrinterDriver -Name 'Generic / Text Only' -EA SilentlyContinue)){Add-PrinterDriver -Name 'Generic / Text Only'|Out-Null}", StringComparison.Ordinal);
        var add = script.IndexOf("Add-Printer -Name", StringComparison.Ordinal);
        Assert.True(check >= 0, "the script must add the driver when it is missing");
        Assert.True(check < add, "the driver must exist before Add-Printer runs");
    }

    [Fact]
    public async Task Install_ErrorThatIsNotAboutTheSpooler_DoesNotBlameTheSpooler()
    {
        var result = await Make(new FakeRunner { ElevatedOutput = "ERROR: The specified driver does not exist" }).InstallAsync();

        Assert.False(result.Success);
        Assert.Contains("driver does not exist", result.Message);
        Assert.DoesNotContain("Print Spooler", result.Message);
    }

    [Fact]
    public async Task Install_ErrorAboutTheSpooler_AdvisesCheckingTheService()
    {
        var result = await Make(new FakeRunner { ElevatedOutput = "ERROR: The Spooler service is not running" }).InstallAsync();

        Assert.Contains("Print Spooler", result.Message);
    }

    [Theory]
    [InlineData("ERROR:")]
    [InlineData("")]
    public async Task Install_EmptyErrorText_AdvisesCheckingTheService(string output)
    {
        var result = await Make(new FakeRunner { ElevatedOutput = output }).InstallAsync();

        Assert.False(result.Success);
        Assert.Contains("Print Spooler", result.Message);
    }

    [Fact]
    public async Task CancellationByTheCaller_Propagates_ButAUacDeclineDoesNot()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var cancelling = new FakeRunner { ThrowOnElevated = new OperationCanceledException(cts.Token) };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Make(cancelling).InstallAsync(cts.Token));

        var declined = await Make(new FakeRunner { UserDeclines = true }).InstallAsync(CancellationToken.None);
        Assert.False(declined.Success);
        Assert.Contains("permission", declined.Message);
    }
}
