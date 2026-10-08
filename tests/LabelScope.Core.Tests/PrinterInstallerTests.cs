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

        public Task<string> RunAsync(string script, bool elevated, CancellationToken ct = default)
        {
            if (!elevated) return Task.FromResult(StatusOutput);
            ElevatedCalls++;
            LastElevatedScript = script;
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
    public void UnusableName_IsRejectedWithAMessageNamingTheSetting(string name)
    {
        var ex = Assert.Throws<ArgumentException>(() => new PrinterInstaller(new FakeRunner(), name, 9100));
        Assert.Contains("PrinterName", ex.Message);
    }

    [Fact]
    public void NameOver200Characters_IsRejected_ButExactly200IsAccepted()
    {
        Assert.Throws<ArgumentException>(() => new PrinterInstaller(new FakeRunner(), new string('a', 201), 9100));
        _ = new PrinterInstaller(new FakeRunner(), new string('a', 200), 9100);
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
        var runner = new FakeRunner { StatusOutput = "﻿\r\n\r\n" + Ours + "\r\n\r\n" };

        Assert.Equal(PrinterStatus.Installed, await Make(runner).GetStatusAsync());
    }

    [Fact]
    public async Task Status_BomOnlyOutput_MeansNotInstalled()
    {
        Assert.Equal(PrinterStatus.NotInstalled, await Make(new FakeRunner { StatusOutput = "﻿\r\n" }).GetStatusAsync());
    }

    [Fact]
    public async Task Install_BomPrefixedOk_IsSuccess()
    {
        var result = await Make(new FakeRunner { ElevatedOutput = "﻿OK" }).InstallAsync();

        Assert.True(result.Success);
    }

    [Fact]
    public async Task Install_BomPrefixedError_StillShowsTheCause()
    {
        var result = await Make(new FakeRunner { ElevatedOutput = "﻿ERROR: The spooler service is not running" }).InstallAsync();

        Assert.False(result.Success);
        Assert.Contains("spooler", result.Message);
    }
}
