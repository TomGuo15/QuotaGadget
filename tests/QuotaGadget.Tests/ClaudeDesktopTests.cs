using System.Net;
using System.Text.Json;
using Xunit;

namespace QuotaGadget.Tests;

public sealed class ClaudeDesktopTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 12, 10, 0, 0, TimeSpan.Zero);
    private readonly TempDirectory root = new("QuotaGadget Claude Desktop ");

    [Fact]
    public void FindsExistingHistoriesInRoamingAndClaudePackages()
    {
        var regular = root.WriteFile("roaming/Claude/plan-usage-history.json", "{}");
        var packaged = root.WriteFile("local/Packages/Claude_fixture/LocalCache/Roaming/Claude/plan-usage-history.json", "{}");
        var otherPackage = root.WriteFile("local/Packages/Claude_other/LocalCache/Roaming/Claude/plan-usage-history.json", "{}");
        root.WriteFile("local/Packages/Other_fixture/LocalCache/Roaming/Claude/plan-usage-history.json", "{}");
        root.WriteFile("local/Packages/Claude_empty/LocalCache/Roaming/Claude/unrelated.json", "{}");

        var paths = ClaudeDesktop.HistoryFiles(key => key switch
        {
            "APPDATA" => root.PathFor("roaming"),
            "LOCALAPPDATA" => root.PathFor("local"),
            _ => throw new ArgumentOutOfRangeException(nameof(key))
        });
        Assert.Equal(new[] { regular, packaged, otherPackage }.Order(), paths.Order());
        Assert.Empty(ClaudeDesktop.HistoryFiles(_ => "relative"));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("2")]
    public async Task UsesLatestFiniteTimestampAndShowsUsedPercentWithoutResets(string version)
    {
        string path = root.WriteFile("history.json", History(version,
            Sample(Now, """{"org":"sample","u":{"fh":12.5,"sd":91,"other":43,"resets_at":"2030-01-01"}}"""),
            Sample(Now.AddMinutes(-1), """{"org":"previous","u":{"fh":99}}"""),
            """{"t":1e400,"org":"ignored","u":{"fh":99}}""",
            """{"t":"9000000000000","org":"ignored","u":{"fh":99}}""", "null"));

        var quota = await ClaudeDesktop.ReadAsync([path], Now, default);

        Assert.Null(quota.Error);
        Assert.Null(quota.Email);
        Assert.Null(quota.Tier);
        Assert.Null(quota.Resets);
        Assert.Collection(quota.Windows,
            window => Assert.Equal(new("5h", 12.5, null), window),
            window => Assert.Equal(new("Week", 91, null), window));
    }

    [Theory]
    [InlineData("""{"org":"","u":{"fh":2}}""")]
    [InlineData("""{"org":" ","u":{"fh":2}}""")]
    [InlineData("""{"org":5,"u":{"fh":2}}""")]
    [InlineData("""{"org":"sample","u":[]}""")]
    [InlineData("""{"org":"sample","u":{"fh":-0.1,"sd":100.1}}""")]
    [InlineData("""{"org":"sample","u":{"fh":"0","sd":1e400}}""")]
    [InlineData("""{"org":"sample","u":{"other":2}}""")]
    public async Task AnInvalidLatestSampleDoesNotReviveAnEarlierSample(string fields)
    {
        string path = root.WriteFile("history.json", History("2", Sample(Now, fields), Sample(Now.AddMinutes(-1))));

        var quota = await ClaudeDesktop.ReadAsync([path], Now, default);

        Assert.Equal("Quota unavailable", quota.Error);
        Assert.Empty(quota.Windows);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"version\":1,\"samples\":{}}")]
    [InlineData("{\"version\":1,\"samples\":[]}")]
    [InlineData("{\"version\":1,\"samples\":[{\"t\":true}]}")]
    [InlineData("{\"version\":1,\"samples\":[")]
    public async Task MalformedHistoryReturnsAQuotaError(string history)
    {
        string path = root.WriteFile("history.json", history);

        Assert.Equal("Quota unavailable", (await ClaudeDesktop.ReadAsync([path], Now, default)).Error);
    }

    [Fact]
    public async Task RepeatedFieldsUseTheLastValueAtEachLevel()
    {
        var path = root.WriteFile("history.json", $$$"""
            {"version":99,"version":2,"samples":[],"samples":[
              {"t":0,"t":{{{Now.ToUnixTimeMilliseconds()}}},"org":"","org":"sample","u":[],"u":{"fh":-1,"fh":35}}]}
            """);

        var quota = await ClaudeDesktop.ReadAsync([path], Now, default);

        Assert.Equal(new QuotaWindow("5h", 35, null), Assert.Single(quota.Windows));
    }

    [Theory]
    [InlineData("""{"version":2,"samples":[{"t":99,"unused\ud800":0},{"t":100,"org":"sample","u":{"fh":20}}]}""")]
    [InlineData("""{"version":2,"unused\ud800":0,"samples":[{"t":100,"org":"sample","u":{"fh":20}}]}""")]
    [InlineData("""{"version":2,"samples":[{"t":100,"org":"sample","u":{"fh":20,"unused\ud800":0}}]}""")]
    [InlineData("""{"version":2,"unusedÿ":0,"samples":[{"t":100,"org":"sample","u":{"fh":20}}]}""")]
    public async Task UndecodableUnusedKeysDoNotHideQuota(string history)
    {
        var path = root.PathFor("history.json");
        // Latin-1 writes the final case's ÿ as a lone 0xff byte.
        await File.WriteAllBytesAsync(path, System.Text.Encoding.Latin1.GetBytes(history));
        var quota = await ClaudeDesktop.ReadAsync([path], DateTimeOffset.FromUnixTimeMilliseconds(100), default);
        Assert.Equal(new QuotaWindow("5h", 20, null), Assert.Single(quota.Windows));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("3")]
    [InlineData("1.0")]
    [InlineData("2e0")]
    [InlineData("\"1\"")]
    public async Task UnsupportedVersionsRejectOtherwiseUsableHistory(string version)
    {
        string path = root.WriteFile("history.json", History(version, Sample(Now)));

        Assert.Equal("Quota unavailable", (await ClaudeDesktop.ReadAsync([path], Now, default)).Error);
    }

    [Theory]
    [InlineData(-7_200_001, "Open Claude Desktop to refresh")]
    [InlineData(-7_200_000, null)]
    [InlineData(60_000, null)]
    [InlineData(60_001, "Quota unavailable")]
    public async Task EnforcesFreshnessBoundariesAndSkipsInvalidWindows(int milliseconds, string? error)
    {
        string path = root.WriteFile("history.json", History("1",
            Sample(Now.AddMilliseconds(milliseconds), """{"org":"sample","u":{"fh":false,"sd":100}}""")));

        var quota = await ClaudeDesktop.ReadAsync([path], Now, default);

        Assert.Equal(error, quota.Error);
        if (error == null) Assert.Equal(new QuotaWindow("Week", 100, null), Assert.Single(quota.Windows));
        else Assert.Empty(quota.Windows);
    }

    [Fact]
    public async Task UsesTheFreshestValidSampleAcrossLocations()
    {
        string older = root.WriteFile("old.json", History("1", Sample(Now.AddMinutes(-3))));
        string newest = root.WriteFile("new.json", History("2", Sample(Now, """{"org":"sample","u":{"fh":0}}""")));
        string future = root.WriteFile("future.json", History("2", Sample(Now.AddMinutes(10)), Sample(Now)));
        string corrupt = root.WriteFile("corrupt.json", "{");

        string[][] orders = [[older, future, corrupt, newest], [newest, older, corrupt, future]];
        foreach (var paths in orders)
        {
            var quota = await ClaudeDesktop.ReadAsync(paths, Now, default);
            Assert.Null(quota.Error);
            Assert.Equal(new QuotaWindow("5h", 0, null), Assert.Single(quota.Windows));
        }
    }

    [Fact]
    public async Task AcceptsUtf8HistoryWithAByteOrderMark()
    {
        string path = root.PathFor("history.json");
        await File.WriteAllTextAsync(path, History("1", Sample(Now)), new System.Text.UTF8Encoding(true));
        var quota = await ClaudeDesktop.ReadAsync([path], Now, default);
        Assert.Equal(new QuotaWindow("5h", 20, null), Assert.Single(quota.Windows));
    }

    [Fact]
    public async Task ReadsAtMostFourMebibytes()
    {
        const int limit = 4 * 1024 * 1024;
        var older = Sample(Now.AddMinutes(-1), """{"org":"sample","u":{"sd":95}}""");
        var latest = Sample(Now);
        var count = (limit - History("2", latest).Length) / (older.Length + 1);
        var samples = string.Join(",", Enumerable.Repeat(older, count).Append(latest));
        string path = root.WriteFile("limit.json", History("2", samples).PadRight(limit));
        var quota = await ClaudeDesktop.ReadAsync([path], Now, default);
        Assert.Null(quota.Error);
        Assert.Equal(new QuotaWindow("5h", 20, null), Assert.Single(quota.Windows));
        await File.AppendAllTextAsync(path, " ");

        var oversized = await ClaudeDesktop.ReadAsync([path], Now, default);

        Assert.Equal("Quota unavailable", oversized.Error);
        Assert.Empty(oversized.Windows);
    }

    [Fact]
    public async Task DefaultClaudeWithoutCredentialsUsesDesktopHistoryOrTheSignInMessage()
    {
        var account = DefaultAccount();
        string history = root.WriteFile("history.json", History("1", Sample(Now)));
        using var http = new HttpClient(new MockHttpHandler((_, _) => throw new InvalidOperationException("Unexpected HTTP request")));
        var quota = await new ClaudeQuota(http, () => [history], new FixedClock())
            .FetchAsync(account, default, _ => account.ProfileDir);
        var absent = await new ClaudeQuota(http, () => [], new FixedClock())
            .FetchAsync(account, default, _ => account.ProfileDir);

        Assert.Equal(new QuotaWindow("5h", 20, null), Assert.Single(quota.Windows));
        Assert.Equal("Sign in to load quota", absent.Error);
        Assert.False(Directory.Exists(account.ProfileDir));
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task ExistingDefaultCredentialsKeepTheirOAuthOutcome(HttpStatusCode status)
    {
        var account = DefaultAccount();
        const string credentials = """{"claudeAiOauth":{"accessToken":"fixture"}}""";
        string path = root.WriteFile(".claude/.credentials.json", credentials);
        int requests = 0;
        using var http = new HttpClient(new MockHttpHandler(request =>
        {
            requests++;
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("fixture", request.Headers.Authorization?.Parameter);
            return new HttpResponseMessage(status)
            {
                Content = new StringContent("""{"five_hour":{"utilization":65}}""")
            };
        }));
        var provider = new ClaudeQuota(http, UnexpectedHistory);

        if (status == HttpStatusCode.TooManyRequests)
        {
            var error = await Assert.ThrowsAsync<HttpRequestException>(() => provider.FetchAsync(account, default, _ => account.ProfileDir));
            Assert.Equal(status, error.StatusCode);
        }
        else
        {
            var quota = await provider.FetchAsync(account, default, _ => account.ProfileDir);
            if (status == HttpStatusCode.OK) Assert.Equal(65, Assert.Single(quota.Windows).UsedPercent);
            else Assert.Equal("Login expired — re-login", quota.Error);
        }
        Assert.Equal(1, requests);
        Assert.Equal(credentials, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task OwnedOrNondefaultProfilesAndMalformedCredentialsCannotUseDesktopHistory()
    {
        var account = DefaultAccount();
        using var http = new HttpClient(new MockHttpHandler((_, _) => throw new InvalidOperationException("Unexpected HTTP request")));
        var provider = new ClaudeQuota(http, UnexpectedHistory);
        var owned = new Account(Provider.Claude, Path.Combine(Config.Profiles, Guid.NewGuid().ToString("N")));

        Assert.Equal("Sign in to load quota", (await provider.FetchAsync(owned, default, _ => account.ProfileDir)).Error);
        await Assert.ThrowsAsync<InvalidDataException>(() => provider.FetchAsync(account with
        {
            ProfileDir = root.PathFor("other")
        }, default, _ => account.ProfileDir));

        root.WriteFile(".claude/.credentials.json", "broken JSON");
        await Assert.ThrowsAnyAsync<JsonException>(() => provider.FetchAsync(account, default, _ => account.ProfileDir));
    }

    public void Dispose() => root.Dispose();

    private Account DefaultAccount() => new(Provider.Claude, root.PathFor(".claude"));

    private static string Sample(DateTimeOffset time, string fields = """{"org":"sample","u":{"fh":20}}""") =>
        "{\"t\":" + time.ToUnixTimeMilliseconds() + "," + fields[1..];

    private static string History(string version, params string[] samples) =>
        "{\"version\":" + version + ",\"samples\":[" + string.Join(",", samples) + "]}";

    private static IEnumerable<string> UnexpectedHistory() => throw new InvalidOperationException("Unexpected Desktop history read");

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
