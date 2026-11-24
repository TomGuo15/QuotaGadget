using System.Net;
using System.Text.Json.Nodes;
using Xunit;

namespace QuotaGadget.Tests;

[Collection("CLI path")]
public class ProviderTests
{
    private static JsonNode Json(string text) => JsonNode.Parse(text)!;

    [Fact]
    public void CodexUsesDurationsEvenWhenThePrimaryWindowIsWeekly()
    {
        using var http = new HttpClient();
        var q = new CodexQuota(http).Parse(Json("""
            {"rate_limit":{"primary_window":{"used_percent":37,"limit_window_seconds":604800,"reset_at":1800000000},
                           "secondary_window":{"used_percent":0,"limit_window_seconds":18000}}}
            """));
        Assert.Collection(q.Windows,
            w => Assert.Equal(new("Week", 37, DateTimeOffset.FromUnixTimeSeconds(1800000000)), w),
            w => Assert.Equal(new("5h", 0, null), w));
    }

    [Fact]
    public async Task CodexListsOnlyAvailableExpiriesAndPreservesUsageIfDetailsFail()
    {
        using var profile = new OwnedProfile(Provider.Codex);
        var path = profile.PathFor("auth.json");
        await File.WriteAllTextAsync(path, """{"tokens":{"access_token":"fixture","account_id":"sample"}}""");
        int count = 2, requests = 0;
        bool fail = false;
        using var http = new HttpClient(new MockHttpHandler(req =>
        {
            Assert.Equal(HttpMethod.Get, req.Method);
            Assert.Equal("fixture", req.Headers.Authorization!.Parameter);
            Assert.Equal("sample", req.Headers.GetValues("ChatGPT-Account-Id").Single());
            if (req.RequestUri!.AbsolutePath == "/backend-api/wham/usage")
                return MockHttpHandler.Json($$$"""
                    {"rate_limit":{"primary_window":{"used_percent":30}},"rate_limit_reset_credits":{
                    "available_count":{{{count}}},"applicable_available_count":0}}
                    """);
            Assert.Equal("/backend-api/wham/rate-limit-reset-credits", req.RequestUri.AbsolutePath);
            requests++;
            return fail ? new HttpResponseMessage(HttpStatusCode.TooManyRequests) : MockHttpHandler.Json("""
                {"available_count":2,"credits":[{"status":"available","expires_at":"2031-03-01T00:00:00Z"},
                {"status":"redeemed","expires_at":"2031-01-01T00:00:00Z"},
                {"status":"available","expires_at":"2031-02-01T00:00:00Z"}]}
                """);
        }));
        var provider = new CodexQuota(http);
        var resets = (await provider.FetchAsync(profile.Account, default)).Resets!;
        Assert.Equal(2, resets.Count);
        Assert.Equal(new[] { 2, 3 }, resets.Expiries.Select(date => date.Month));
        Assert.Equal($"2 resets\n{resets.Expiries[0].LocalDateTime:MMM d} · {resets.Expiries[1].LocalDateTime:MMM d}", resets.Tooltip);
        fail = true;
        var partial = await provider.FetchAsync(profile.Account, default);
        Assert.Equal(30, Assert.Single(partial.Windows).UsedPercent);
        Assert.Equal(2, partial.Resets!.Count);
        Assert.Empty(partial.Resets.Expiries);
        count = 0;
        Assert.Null((await provider.FetchAsync(profile.Account, default)).Resets);
        Assert.Equal(2, requests);
    }

    [Fact]
    public void ClaudeSkipsMissingAndInvalidWindows()
    {
        using var http = new HttpClient();
        var p = new ClaudeQuota(http);
        var quota = p.Parse(Json("""
            {"five_hour":{"utilization":81.5,"resets_at":"2030-01-02T03:04:00+02:00"},"seven_day":null}
            """));
        var window = Assert.Single(quota.Windows);
        Assert.Equal("5h", window.Name);
        Assert.Equal(81.5, window.UsedPercent);
        Assert.Equal(DateTimeOffset.Parse("2030-01-02T01:04:00Z"), window.ResetsAt);
        Assert.Equal("Week", Assert.Single(p.Parse(Json("""
            {"five_hour":{"utilization":false},"seven_day":{"utilization":100}}
            """)).Windows).Name);
        Assert.NotNull(p.Parse(Json("""{"five_hour":{"utilization":-4}}""")).Error);
    }

    [Fact]
    public void AntigravityConvertsRemainingQuotaAndKeepsGroupNames()
    {
        var report = Json("""
            {"status":"SUCCESS","command":{"name":"usage","data":{"groups":[
              {"name":"Gemini Models","buckets":[{"window":"weekly","remaining_fraction":0.75,"reset_time":"2030-01-03T00:00:00Z"},
                {"window":"5h","remaining_fraction":0,"enabled":false}]},
              {"name":"Claude and GPT models","buckets":[{"window":"5h","remaining_fraction":0},
                {"window":"weekly","reset_time":"2030-01-01T00:00:00Z"}]}]}}}
            """);
        var q = AntigravityQuota.Parse(report);
        Assert.Collection(q.Windows,
            w => Assert.Equal(new("Gemini Week", 25, DateTimeOffset.Parse("2030-01-03T00:00:00Z")), w),
            w => Assert.Equal(new("Claude/GPT 5h", 100, null), w));
        report["command"]!["name"] = "models";
        Assert.Empty(AntigravityQuota.Parse(report).Windows);
    }

    [Fact]
    public void AntigravityOmitsDisabledFiveHourBucketWhenWeeklyQuotaIsExhausted()
    {
        var report = Json("""
            {"status":"SUCCESS","command":{"name":"usage","data":{"groups":[
              {"name":"Claude and GPT models","buckets":[
                {"id":"3p-5h","window":"5h","disabled":true,"remaining_fraction":1},
                {"id":"3p-weekly","window":"weekly","disabled":"false","enabled":1,
                 "remaining_fraction":0,"reset_time":"2030-01-03T00:00:00Z"}]}]}}}
            """);
        Assert.Equal(new QuotaWindow("Claude/GPT Week", 100, DateTimeOffset.Parse("2030-01-03T00:00:00Z")),
            Assert.Single(AntigravityQuota.Parse(report).Windows));
    }

    [Theory]
    [InlineData("not JSON", 1, null)]
    [InlineData("""{"method":"notice","params":{}}""", 1, null)]
    [InlineData("""{"id":2,"method":"request","params":{}}""", 2, null)]
    [InlineData("""{"id":1,"result":{}}""", 2, null)]
    [InlineData("""{"id":1,"result":{"authMethods":[{"id":"oauth-personal"}]}}""", 1, true)]
    [InlineData("""{"id":1,"error":{"code":-1}}""", 1, false)]
    [InlineData("""{"id":1,"result":{"authMethods":[]}}""", 1, true)]
    [InlineData("""{"id":2,"result":{},"error":{"code":-1}}""", 2, false)]
    [InlineData("""{"id":2}""", 2, false)]
    [InlineData("""{"id":2,"result":{}}""", 2, true)]
    public void AcpRepliesMatchTheOutstandingRequest(string line, int id, bool? expected)
    {
        bool matched = Login.TryAcpReply(line, id, out var result);
        Assert.Equal(expected.HasValue, matched);
        Assert.Equal(expected == true, result != null);
    }

    [Theory]
    [InlineData("oauth-personal", """{"jsonrpc":"2.0","id":2,"result":{}}""", true)]
    [InlineData("oauth-personal", """{"jsonrpc":"2.0","id":2,"result":null}""", true)]
    [InlineData("oauth-personal", """{"jsonrpc":"2.0","id":2,"error":{"code":-1}}""", false)]
    [InlineData("oauth-business", """{"jsonrpc":"2.0","id":2,"result":{}}""", false)]
    [InlineData("oauth-personal", null, false)]
    [InlineData("oauth-personal", """{"method":"pending"}""", null)]
    public async Task AntigravityAuthenticatesOnlyWhenPersonalLoginIsOffered(string offered, string? reply, bool? expected)
    {
        Directory.CreateDirectory(Config.Home);
        using var dir = new TempDirectory("QuotaGadget agy login ");
        var init = dir.PathFor("initialize.json");
        var auth = dir.PathFor("authenticate.json");
        string? path = Environment.GetEnvironmentVariable("PATH");
        try
        {
            dir.WriteScript("agy.cmd", $$$"""
                @echo off
                if not "%*"=="acp --log-file NUL" exit /b 1
                set /p init=
                >"%~dp0initialize.json" echo %init%
                echo synthetic startup message
                echo {"jsonrpc":"2.0","id":1,"result":{"protocolVersion":1,"authMethods":[{"id":"{{{offered}}}"}]}}
                set /p auth=
                >"%~dp0authenticate.json" echo %auth%
                echo {"jsonrpc":"2.0","method":"notification"}
                {{{(reply == null ? "exit /b 0" : "echo " + reply)}}}
                set /p waitForClient=
                """);
            Environment.SetEnvironmentVariable("PATH", dir.FullName);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var login = Login.RunAsync(new(Provider.Antigravity, ""), deadline.Token);
            if (expected.HasValue) Assert.Equal(expected.Value, await login);
            else
            {
                while (!File.Exists(auth) || new FileInfo(auth).Length == 0) await Task.Delay(10, deadline.Token);
                deadline.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => login);
            }
            Assert.True(JsonNode.DeepEquals(Json("""
                {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":1,"clientCapabilities":{}}}
                """), Json(File.ReadAllText(init))));
            if (offered == "oauth-personal")
            {
                Assert.True(JsonNode.DeepEquals(Json("""
                    {"jsonrpc":"2.0","id":2,"method":"authenticate","params":{"methodId":"oauth-personal"}}
                    """), Json(File.ReadAllText(auth))));
            }
            else Assert.False(File.Exists(auth));
        }
        finally { Environment.SetEnvironmentVariable("PATH", path); }
    }

    [Theory]
    [InlineData("1.5.13", """{"id":2,"result":{"_meta":{"antigravity":{"tier":{"id":"g1-ultra-lite-tier","name":"Google AI Ultra"}}}}}""", "Ultra Lite")]
    [InlineData("1.4.0", """{"id":2,"error":{"code":-32000}}""", null)]
    [InlineData("1.5.13", """{"id":2,"result":{}}""", null)]
    [InlineData("1.1.11", null, null)]
    public async Task AntigravityUsesUsageAndCachedSessionMetadata(string version, string? sessionReply, string? expectedTier)
    {
        Directory.CreateDirectory(Config.Home);
        using var dir = new TempDirectory("QuotaGadget & agy ");
        var signedIn = dir.CreateSubdirectory("signed-in");
        string? path = Environment.GetEnvironmentVariable("PATH");
        string? profile = Environment.GetEnvironmentVariable("USERPROFILE");
        string? geminiDir = Environment.GetEnvironmentVariable("ANTIGRAVITY_GEMINI_DIR");
        string? appDataDir = Environment.GetEnvironmentVariable("ANTIGRAVITY_APP_DATA_DIR");
        try
        {
            dir.WriteScript("agy.cmd", """
                @echo off
                if "%~1"=="--version" (
                    >>"%~dp0versions.txt" echo version
                    echo FIXTURE_VERSION
                    exit /b 0
                )
                if "%~1"=="acp" goto acp
                if not "%*"=="-p /usage --output-format json --log-file NUL" exit /b 1
                if /i not "%USERPROFILE%"=="%~dp0signed-in" exit /b 2
                echo {"status":"SUCCESS","command":{"name":"usage","data":{"groups":[{"name":"Gemini Models","buckets":[{"window":"weekly","remaining_fraction":0.75},{"window":"5h","remaining_fraction":0.5}]}]}}}
                exit /b 0
                :acp
                >>"%~dp0sessions.txt" echo session
                >"%~dp0args.txt" echo %*
                >"%~dp0cwd.txt" cd
                set /p init=
                >"%~dp0init.json" echo %init%
                echo {"id":1,"result":{"protocolVersion":1}}
                set /p session=
                >"%~dp0session.json" echo %session%
                set "history=%USERPROFILE%\.gemini\antigravity-cli"
                mkdir "%history%\conversations"
                mkdir "%history%\brain\fixture"
                >"%history%\conversations\fixture" echo synthetic conversation
                if defined ANTIGRAVITY_GEMINI_DIR exit /b 3
                if defined ANTIGRAVITY_APP_DATA_DIR exit /b 3
                echo {"method":"notification"}
                echo {"id":3,"result":{"_meta":{"antigravity":{"tier":{"name":"Free"}}}}}
                echo FIXTURE_SESSION_REPLY
                set /p extra=
                if defined extra >"%~dp0extra.txt" echo unexpected request
                """.Replace("FIXTURE_VERSION", version).Replace("FIXTURE_SESSION_REPLY", sessionReply));
            Environment.SetEnvironmentVariable("PATH", dir.FullName);
            Environment.SetEnvironmentVariable("USERPROFILE", signedIn.FullName);
            Environment.SetEnvironmentVariable("ANTIGRAVITY_GEMINI_DIR", Path.Combine(signedIn.FullName, ".gemini"));
            Environment.SetEnvironmentVariable("ANTIGRAVITY_APP_DATA_DIR", "antigravity-cli");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var clock = new TestClock();
            var provider = new AntigravityQuota(clock);
            var quota = await provider.FetchAsync("account@example.test", timeout.Token);

            Assert.Null(quota.Error);
            Assert.Empty(signedIn.EnumerateFileSystemInfos());
            Assert.Equal(expectedTier, quota.Tier);
            Assert.Collection(quota.Windows,
                w => Assert.Equal(new("Gemini Week", 25, null), w),
                w => Assert.Equal(new("Gemini 5h", 50, null), w));
            string args = dir.PathFor("args.txt");
            Assert.Equal(sessionReply != null, File.Exists(args));
            if (sessionReply != null)
            {
                Assert.Equal("acp --log-file NUL --include-default-customizations=false --disable-telemetry", File.ReadAllText(args).Trim());
                Assert.True(JsonNode.DeepEquals(Json(Login.AcpInitialize), Json(File.ReadAllText(dir.PathFor("init.json")))));
                var session = Json(File.ReadAllText(dir.PathFor("session.json")));
                Assert.Equal("session/new", session["method"].Text());
                var cwd = File.ReadAllText(dir.PathFor("cwd.txt")).Trim();
                Assert.Equal(cwd, session["params"]!["cwd"].Text());
                Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetTempPath()), Path.GetDirectoryName(cwd));
                Assert.False(Directory.Exists(cwd));
                Assert.Empty(session["params"]!["mcpServers"]!.AsArray());
                Assert.False(File.Exists(dir.PathFor("extra.txt")));
                clock.Now = clock.Now.AddHours(6).AddTicks(-1);
                Assert.Equal(expectedTier, (await provider.FetchAsync("account@example.test", timeout.Token)).Tier);
                var sessions = dir.PathFor("sessions.txt");
                Assert.Single(File.ReadAllLines(sessions));
                clock.Now = clock.Now.AddTicks(1);
                await provider.FetchAsync("account@example.test", timeout.Token);
                Assert.Equal(2, File.ReadAllLines(sessions).Length);
                await provider.FetchAsync("another@example.test", timeout.Token);
                Assert.Equal(3, File.ReadAllLines(sessions).Length);
            }
            Assert.Single(File.ReadAllLines(dir.PathFor("versions.txt")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", path);
            Environment.SetEnvironmentVariable("USERPROFILE", profile);
            Environment.SetEnvironmentVariable("ANTIGRAVITY_GEMINI_DIR", geminiDir);
            Environment.SetEnvironmentVariable("ANTIGRAVITY_APP_DATA_DIR", appDataDir);
        }
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Theory]
    [InlineData("""{"_meta":{"antigravity":{"tier":{"name":"Google AI Pro"}}}}""", "Pro")]
    [InlineData("""{"_meta":{"antigravity":{"tier":{"name":"Free"}}}}""", "Free")]
    [InlineData("""{"_meta":{"antigravity":{"tier":{"id":"g1-ultra-lite-tier"}}}}""", "Ultra Lite")]
    [InlineData("""{"_meta":{"antigravity":{"tier":{"id":"g1-pro-tier","name":"Google AI Ultra"}}}}""", "Pro")]
    [InlineData("""{"_meta":{"antigravity":{"tier":{"id":"g1-ultra-tier"}}}}""", "Ultra")]
    [InlineData("""{"_meta":{"antigravity":{"tier":{"id":"g1-unrecognized-tier","name":"Google AI Pro"}}}}""", "Pro")]
    [InlineData("""{"_meta":{"antigravity":{"tier":{"id":"g1-unrecognized-tier"}}}}""", null)]
    [InlineData("""{"_meta":{"antigravity":{"tier":{"name":"Google AI Ultra (5x)"}}}}""", "Ultra 5×")]
    [InlineData("""{"_meta":{"antigravity":"Free"}}""", null)]
    public void AntigravityReadsOnlyTheReportedSessionTier(string json, string? expected) =>
        Assert.Equal(expected, AntigravityQuota.ReportedTier(Json(json).AsObject()));

    [Theory]
    [InlineData(ProviderCase.Codex)]
    [InlineData(ProviderCase.Claude)]
    public async Task OwnedProfileRefreshesOnceAndPreservesCliMetadata(ProviderCase kind)
    {
        bool codex = kind == ProviderCase.Codex;
        using var profile = new OwnedProfile(codex ? Provider.Codex : Provider.Claude);
        var path = profile.PathFor(codex ? "auth.json" : ".credentials.json");
        await File.WriteAllTextAsync(path, codex
            ? """{"keep":42,"tokens":{"access_token":"old","refresh_token":"fixture","account_id":"example"}}"""
            : """{"keep":42,"claudeAiOauth":{"accessToken":"old","refreshToken":"fixture","subscriptionType":"test"}}""");
        int gets = 0, posts = 0;
        using var http = new HttpClient(new MockHttpHandler(async (req, ct) =>
        {
            if (req.Method == HttpMethod.Post)
            {
                posts++;
                Assert.Equal(codex ? "auth.openai.com" : "platform.claude.com", req.RequestUri!.Host);
                var grant = Json(await req.Content!.ReadAsStringAsync(ct));
                Assert.Equal("fixture", grant["refresh_token"]!.GetValue<string>());
                return MockHttpHandler.Json("""{"access_token":"new","refresh_token":"next","expires_in":3600}""");
            }
            gets++;
            Assert.Equal(codex ? "chatgpt.com" : "api.anthropic.com", req.RequestUri!.Host);
            Assert.Equal(codex ? "example" : "oauth-2025-04-20",
                req.Headers.GetValues(codex ? "ChatGPT-Account-Id" : "anthropic-beta").Single());
            Assert.Equal(gets == 1 ? "old" : "new", req.Headers.Authorization!.Parameter);
            return gets == 1 ? new HttpResponseMessage(HttpStatusCode.Unauthorized) :
                MockHttpHandler.Json(codex ? """{"rate_limit":{"primary_window":{"used_percent":21,"limit_window_seconds":18000}}}"""
                : """{"five_hour":{"utilization":21}}""");
        }));
        OAuthQuota provider = codex ? new CodexQuota(http) : new ClaudeQuota(http);
        var quota = await provider.FetchAsync(profile.Account, default);
        Assert.Equal(21, Assert.Single(quota.Windows).UsedPercent);
        Assert.Equal((2, 1), (gets, posts));
        var saved = Json(await File.ReadAllTextAsync(path));
        Assert.Equal(42, saved["keep"]!.GetValue<int>());
        var node = saved[codex ? "tokens" : "claudeAiOauth"]!;
        Assert.Equal("next", node[codex ? "refresh_token" : "refreshToken"]!.GetValue<string>());
        Assert.Equal(codex ? "example" : "test", node[codex ? "account_id" : "subscriptionType"]!.GetValue<string>());
    }

    public enum ProviderCase { Codex, Claude }
}
