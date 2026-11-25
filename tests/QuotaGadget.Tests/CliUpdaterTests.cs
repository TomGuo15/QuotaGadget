using System.ComponentModel;
using System.Diagnostics;
using Xunit;

namespace QuotaGadget.Tests;

[Collection("CLI path")]
public class CliUpdaterTests
{
    [Theory]
    [InlineData("Codex", "LOCALAPPDATA", @"OpenAI\Codex\bin\codex.exe", "codex.cmd")]
    [InlineData("Codex", "APPDATA", @"npm\codex.cmd", "codex.exe")]
    [InlineData("Claude", "USERPROFILE", @".local\bin\claude.exe", "claude.exe")]
    [InlineData("Claude", "LOCALAPPDATA", @"Programs\claude\claude.exe", "claude.exe")]
    [InlineData("Claude", "APPDATA", @"Claude\claude-code\2.1.4\claude.exe", "claude.exe")]
    [InlineData("Antigravity", "LOCALAPPDATA", @"agy\bin\agy.exe", "agy.exe")]
    public void FindsStandardInstallsWithEmptyPathAndPrefersPathWhenPresent(string providerName,
        string root, string relativePath, string onPathName)
    {
        using var fixture = new CliFixture();
        var provider = Enum.Parse<Provider>(providerName);
        Assert.Equal(2, Assert.Throws<Win32Exception>(() => Login.FindCli(provider, fixture.ReadEnvironment)).NativeErrorCode);

        var installed = fixture.Write(Path.Combine(root, relativePath), "synthetic CLI");
        Assert.Equal(installed, Login.FindCli(provider, fixture.ReadEnvironment));

        var onPath = fixture.Write(Path.Combine("PATH", onPathName), "synthetic CLI on PATH");
        var path = Path.GetDirectoryName(onPath);
        Assert.Equal(onPath, Login.FindCli(provider,
            variable => variable == "PATH" ? $"\"{path}\"" : fixture.ReadEnvironment(variable)));
    }

    [Fact]
    public void PrefersTheNewestInstalledClaudeDesktopVersionNumerically()
    {
        using var fixture = new CliFixture();
        fixture.Write(@"APPDATA\Claude\claude-code\2.9.12\claude.exe", "older");
        var newest = fixture.Write(@"APPDATA\Claude\claude-code\2.10.3\bin\claude.exe", "newest");
        fixture.Write(@"APPDATA\Claude\claude-code\3.0.0\setup.exe", "no CLI yet");

        Assert.Equal(newest, Login.FindCli(Provider.Claude, fixture.ReadEnvironment));
    }

    [Fact]
    public async Task SkipsDesktopManagedClisOnAndOffPathWhileUpdatingStandaloneCli()
    {
        using var fixture = new CliFixture();
        var codex = fixture.Write(@"LOCALAPPDATA\OpenAI\Codex\bin\codex.cmd", """
            @echo off
            >"%~dp0ran.txt" echo ran
            exit /b 1
            """);
        fixture.Write(@"APPDATA\Claude\claude-code\2.10.3\claude.exe", "synthetic executable");
        var codexBin = Path.GetDirectoryName(codex)!;
        var standalone = Path.Combine(fixture.DirectoryPath, @"LOCALAPPDATA\OpenAI\CodexTools");
        string? EnvironmentWithPath(string variable) => variable == "PATH" ?
            codexBin + Path.PathSeparator + standalone : fixture.ReadEnvironment(variable);
        bool BeforeUpdate(Provider provider)
        {
            Assert.Equal(Provider.Antigravity, provider);
            return false;
        }
        var updater = new CliUpdater(new(), isInUse: BeforeUpdate, readEnvironment: EnvironmentWithPath);

        Assert.Equal(new CliUpdateResult(false, false, "Desktop apps manage these CLIs"),
            await updater.RunAsync(CancellationToken.None));

        fixture.WriteCli("agy", "3.4.5", "3.4.6");
        Directory.CreateDirectory(standalone);
        File.Move(Path.Combine(fixture.DirectoryPath, "agy.cmd"), Path.Combine(standalone, "agy.cmd"));
        Assert.Equal(new CliUpdateResult(false, true, "Updated agy 3.4.5 → 3.4.6"),
            await updater.RunAsync(CancellationToken.None));
        Assert.Equal(new[] { "agy --version", "agy update", "agy --version" },
            File.ReadAllLines(Path.Combine(standalone, "commands.txt")));
        Assert.False(File.Exists(Path.Combine(codexBin, "ran.txt")));
    }

    [Theory]
    [InlineData(5, false)] // ERROR_ACCESS_DENIED
    [InlineData(87, false)] // ERROR_INVALID_PARAMETER
    [InlineData(8, true)] // ERROR_NOT_ENOUGH_MEMORY
    public void NodeOpenFailuresOnlyIgnoreInaccessibleOrExitedProcesses(int error, bool mayBeCodex) =>
        Assert.Equal(mayBeCodex, CliProcesses.NodeMayBeCodexAfterOpenFailure(error));

    [Fact]
    public async Task RunsVersionsAndUpdatesSequentiallyWithClosedInput()
    {
        using var fixture = new CliFixture();
        fixture.WriteCli("codex", "codex-cli 1.2.3", "codex-cli 1.2.4");
        fixture.WriteCli("claude", "2.3.4 (Claude Code)", "2.3.4 (Claude Code)");
        fixture.WriteCli("agy", "3.4.5", "3.4.5");

        var result = await fixture.Updater().RunAsync(CancellationToken.None);

        Assert.Equal(new CliUpdateResult(false, true, "Updated Codex 1.2.3 → 1.2.4"), result);
        Assert.Equal(new[] { "codex --version", "codex update", "codex --version", "claude --version", "claude update",
            "claude --version", "agy --version", "agy update", "agy --version" }, fixture.Commands);
    }

    [Fact]
    public async Task MissingAndBusyClisDoNotRunAndFailedUpdateDoesNotReportUpgrade()
    {
        using var fixture = new CliFixture();
        fixture.WriteCli("codex", "codex-cli 1.2.3", "codex-cli 1.2.4", exitCode: 7);
        fixture.WriteCli("agy", "3.4.5", "3.4.6");

        var result = await fixture.Updater(inUse: provider => provider == Provider.Antigravity).RunAsync(CancellationToken.None);

        Assert.Equal(new CliUpdateResult(false, false, "Skipped agy (in use) · failed 1"), result);
        Assert.Equal(new[] { "codex --version", "codex update" }, fixture.Commands);
    }

    [Fact]
    public async Task ActiveAntigravityOperationSkipsTheOnlyInstalledCli()
    {
        using var fixture = new CliFixture();
        fixture.WriteCli("agy", "1.2.3", "1.2.4");
        var gate = Login.CliGate(Provider.Antigravity);
        Assert.True(gate.Wait(0));
        try
        {
            var result = await fixture.Updater().RunAsync(CancellationToken.None);
            Assert.Equal(new CliUpdateResult(true, false, "Skipped agy (in use)"), result);
            Assert.Empty(fixture.Commands);
        }
        finally { gate.Release(); }
    }

    [Theory]
    [InlineData("Codex", "Claude,Antigravity", false, "Skipped Codex (in use) · 2 up to date")]
    [InlineData("Codex,Antigravity", "Claude", false, "Skipped Codex, agy (in use) · 1 up to date")]
    [InlineData("Codex,Claude", "", true, "Skipped Codex, Claude (in use)")]
    public async Task BusyCliStatusNamesSkippedProvidersAndReportsUnchangedCount(
        string busyList, string idleList, bool expectedAllInUse, string expectedMessage)
    {
        var busy = ParseProviders(busyList);
        var idle = ParseProviders(idleList);
        using var fixture = new CliFixture();
        foreach (var provider in busy.Concat(idle))
            fixture.WriteCli(CliFixture.CliName(provider), "1.2.3", "1.2.3");

        var result = await fixture.Updater(inUse: provider => busy.Contains(provider)).RunAsync(CancellationToken.None);

        Assert.Equal(new CliUpdateResult(expectedAllInUse, false, expectedMessage), result);
        Assert.Equal(idle.SelectMany(provider =>
        {
            string cli = CliFixture.CliName(provider);
            return new[] { $"{cli} --version", $"{cli} update", $"{cli} --version" };
        }), fixture.Commands);
    }

    [Fact]
    public async Task MixedFailureSkipAndUnchangedIncludesAllSegmentsInOrder()
    {
        using var fixture = new CliFixture();
        fixture.WriteCli("codex", "codex-cli 1.2.3", "codex-cli 1.2.4", exitCode: 7);
        fixture.WriteCli("claude", "2.3.4 (Claude Code)", "2.3.4 (Claude Code)");
        fixture.WriteCli("agy", "3.4.5", "3.4.6");

        var result = await fixture.Updater(inUse: provider => provider == Provider.Antigravity).RunAsync(CancellationToken.None);

        Assert.Equal(new CliUpdateResult(false, false, "Skipped agy (in use) · failed 1 · 1 up to date"), result);
        Assert.Equal(new[] { "codex --version", "codex update", "claude --version", "claude update", "claude --version" },
            fixture.Commands);
    }

    [Theory]
    [InlineData(96)]
    [InlineData(120)]
    [InlineData(144)]
    [InlineData(192)]
    public void BusyAndUnchangedFooterTextFitsWithinGadgetWidthAcrossDpis(int dpi)
    {
        using var painter = new Painter();
        Assert.InRange(painter.Measure("Skipped Codex (in use) · 2 up to date", dpi, 11),
            1, Layout.Width - 2 * Layout.Margin);
    }

    private static Provider[] ParseProviders(string csv) => string.IsNullOrEmpty(csv)
        ? []
        : csv.Split(',').Select(Enum.Parse<Provider>).ToArray();

    [Fact]
    public async Task SuccessfulAgyUpdateInvalidatesPreviouslyCachedVersion()
    {
        using var fixture = new CliFixture();
        fixture.WriteCli("agy", "1.1.10", "1.1.11");
        Directory.CreateDirectory(Config.Home);
        string? path = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", fixture.DirectoryPath);
        try
        {
            var antigravity = new AntigravityQuota(fetchDesktop: _ => Task.FromResult(AccountQuota.Fail(AntigravityDesktop.Unavailable)));
            Assert.Equal("Update agy for quota reports", (await antigravity.FetchAsync(null, CancellationToken.None)).Error);
            Assert.True((await fixture.Updater(antigravity).RunAsync(CancellationToken.None)).Upgraded);
            Assert.Equal("Quota unavailable", (await antigravity.FetchAsync(null, CancellationToken.None)).Error);
            Assert.Equal(new[] { "agy --version", "agy --version", "agy update", "agy --version", "agy --version", "agy -p" }, fixture.Commands);
        }
        finally { Environment.SetEnvironmentVariable("PATH", path); }
    }

    [Fact]
    public async Task NpmCodexUsesNpmDirectlyAndTargetsTheShimPrefix()
    {
        using var fixture = new CliFixture();
        fixture.WriteCli("codex", "codex-cli 1.2.3", "codex-cli 1.2.4", exitCode: 1);
        var package = Path.Combine(fixture.DirectoryPath, "node_modules", "@openai", "codex", "bin");
        Directory.CreateDirectory(package);
        File.WriteAllText(Path.Combine(package, "codex.js"), "// synthetic npm installation");
        var npm = fixture.Write("npm.cmd", """
            @echo off
            if not "%npm_config_prefix%\"=="%~dp0" exit /b 2
            >>"%~dp0commands.txt" echo npm %*
            >"%~dp0codex.updated" echo updated
            exit /b 0
            """);

        Assert.True((await fixture.Updater(findNpm: () => npm).RunAsync(CancellationToken.None)).Upgraded);
        Assert.Equal(new[] { "codex --version", "npm install -g @openai/codex@latest", "codex --version" }, fixture.Commands);
    }

    [Theory]
    [InlineData("codex-cli 0.171.0\r\n", "0.171.0")]
    [InlineData("warning: using cached metadata\n codex-cli 1.2.3-alpha.2+build.8 ", "1.2.3-alpha.2+build.8")]
    [InlineData("codex-cli 1.2.3-", null)]
    [InlineData("Downloading Codex 1.2.3", null)]
    public void OnlyVersionLinesAreUsed(string output, string? expected) =>
        Assert.Equal(expected, CliUpdater.ParseVersion(Provider.Codex, output));

    [Theory]
    [InlineData("1.2.2", false, "Failed 1")]
    [InlineData("1.2.3-alpha.2", false, "Failed 1")]
    [InlineData("1.2.3-alpha.10", true, "Updated agy 1.2.3-alpha.9 → 1.2.3-alpha.10")]
    [InlineData("1.2.3-alpha.9+build.2", false, "CLIs up to date")]
    [InlineData("1.2.3", true, "Updated agy 1.2.3-alpha.9 → 1.2.3")]
    [InlineData("1.2.3-alpha.9.z", true, "Updated agy 1.2.3-alpha.9 → 1.2.3-alpha.9.z")]
    [InlineData("1.2.3-alpha", false, "Failed 1")]
    [InlineData("1.2.3-alpha.beta", true, "Updated agy 1.2.3-alpha.9 → 1.2.3-alpha.beta")]
    [InlineData("1.2.3-beta", true, "Updated agy 1.2.3-alpha.9 → 1.2.3-beta")]
    public async Task OnlyIncreasedVersionPrecedenceIsAnUpgrade(string after, bool upgraded, string message)
    {
        using var fixture = new CliFixture();
        fixture.WriteCli("agy", "1.2.3-alpha.9", after);
        Assert.Equal(new CliUpdateResult(false, upgraded, message), await fixture.Updater().RunAsync(CancellationToken.None));
    }

    [Fact]
    public async Task DeadlineStopsAnUpdateAndChecksTheNextCli()
    {
        using var fixture = new CliFixture();
        fixture.WriteCli("codex", "codex-cli 1.2.3", "codex-cli 1.2.4", beforeUpdate: "\"%SystemRoot%\\System32\\ping.exe\" -n 30 127.0.0.1 >nul");
        fixture.WriteCli("agy", "1.2.3", "1.2.4");

        var result = await fixture.Updater(timeout: TimeSpan.FromSeconds(2)).RunAsync(CancellationToken.None);

        Assert.Equal(new CliUpdateResult(false, true, "Updated 1 · failed 1"), result);
        Assert.Equal(new[] { "codex --version", "codex update", "agy --version", "agy update", "agy --version" }, fixture.Commands);
    }

    [Fact]
    public async Task CancellationTerminatesAnUpdaterDescendantEvenAfterTheShimExits()
    {
        using var fixture = new CliFixture();
        fixture.WriteCli("codex", "codex-cli 1.2.3", "codex-cli 1.2.4", beforeUpdate: """
            set "QuotaGadgetTestChildPid=%~dp0child.pid"
            start "" /b "%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -NonInteractive -Command "[IO.File]::WriteAllText($env:QuotaGadgetTestChildPid, [string]$PID); Start-Sleep -Seconds 20" >nul 2>&1
            """);
        fixture.WriteCli("agy", "1.2.3", "1.2.4");
        using var cancellation = new CancellationTokenSource();
        using var testDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = fixture.Updater().RunAsync(cancellation.Token);
        var pidFile = Path.Combine(fixture.DirectoryPath, "child.pid");
        try
        {
            while (!File.Exists(pidFile) || new FileInfo(pidFile).Length == 0) await Task.Delay(20, testDeadline.Token);
            using var child = Process.GetProcessById(int.Parse(File.ReadAllText(pidFile)));
            Assert.False(child.HasExited);
            // The updater must wait for the whole job even after the shell exits and closes its output.
            Assert.False(run.IsCompleted);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
            await child.WaitForExitAsync(testDeadline.Token);
            Assert.Equal(new[] { "codex --version", "codex update" }, fixture.Commands);
        }
        finally
        {
            cancellation.Cancel();
            try { await run; }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        }
    }

    private sealed class CliFixture : IDisposable
    {
        private readonly TempDirectory directory = new("QuotaGadget CLI fixture ");
        internal string DirectoryPath => directory.FullName;
        internal string[] Commands => File.Exists(Path.Combine(DirectoryPath, "commands.txt"))
            ? File.ReadAllLines(Path.Combine(DirectoryPath, "commands.txt")) : [];

        internal CliUpdater Updater(AntigravityQuota? antigravity = null, Func<Provider, bool>? inUse = null,
            TimeSpan? timeout = null, Func<string>? findNpm = null) =>
            new(antigravity ?? new(), Find, inUse ?? (_ => false), timeout, findNpm);

        internal static string CliName(Provider provider) =>
            provider switch { Provider.Codex => "codex", Provider.Claude => "claude", _ => "agy" };

        private string Find(Provider provider)
        {
            var file = Path.Combine(DirectoryPath, CliName(provider) + ".cmd");
            return File.Exists(file) ? file : throw new Win32Exception(2);
        }

        internal string Write(string name, string contents) => directory.WriteScript(name, contents);

        internal string? ReadEnvironment(string name) => name == "PATH" ? "" : Path.Combine(DirectoryPath, name);

        internal void WriteCli(string name, string before, string after, int exitCode = 0, string beforeUpdate = "") =>
            Write(name + ".cmd", $$$$"""
                @echo off
                set /p QuotaGadgetTestInput= && exit /b 43
                >>"%~dp0commands.txt" echo {{{{name}}}} %~1
                if "%~1"=="--version" goto version
                if "%~1"=="update" goto update
                if not "%~1"=="-p" exit /b 44
                echo {"status":"SUCCESS","command":{"name":"usage","data":{"groups":[]}}}
                exit /b 0
                :version
                if exist "%~dp0{{{{name}}}}.updated" goto updatedVersion
                echo {{{{before}}}}
                exit /b 0
                :updatedVersion
                echo {{{{after}}}}
                exit /b 0
                :update
                {{{{beforeUpdate}}}}
                >"%~dp0{{{{name}}}}.updated" echo updated
                1>&2 echo synthetic updater message
                exit /b {{{{exitCode}}}}
                """);

        public void Dispose()
        {
            for (int attempt = 0; attempt < 20; attempt++)
            {
                try { directory.Dispose(); return; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 19)
                { Thread.Sleep(50); }
            }
        }
    }
}
