using System.Text.Json.Nodes;

namespace QuotaGadget;

internal sealed class ClaudeQuota(HttpClient http, Func<IEnumerable<string>>? historyFiles = null,
    TimeProvider? clock = null) : OAuthQuota(http)
{
    protected override string CredentialFile => ".credentials.json";
    protected override Task<AccountQuota> MissingDefaultCredentials(Account account, CancellationToken ct) =>
        account.Provider == Provider.Claude ? ClaudeDesktop.ReadAsync((historyFiles ?? ClaudeDesktop.HistoryFiles)(),
            (clock ?? TimeProvider.System).GetUtcNow(), ct) : base.MissingDefaultCredentials(account, ct);

    protected override HttpRequestMessage UsageRequest(JsonNode auth)
    {
        var request = BearerGet("https://api.anthropic.com/api/oauth/usage", auth["claudeAiOauth"]?["accessToken"]);
        request.Headers.Add("anthropic-beta", "oauth-2025-04-20");
        return request;
    }

    protected override async Task Refresh(JsonNode auth, CancellationToken ct)
    {
        var oauth = auth["claudeAiOauth"]!;
        var result = await Exchange("https://platform.claude.com/v1/oauth/token", "9d1c250a-e61b-44d9-88ed-5944d1962f5e",
            oauth["refreshToken"], ct);
        oauth["accessToken"] = Required(result["access_token"]);
        if (result["refresh_token"].Text() is { Length: > 0 } rotated) oauth["refreshToken"] = rotated;
        if (result["expires_in"].Number() is double seconds)
            oauth["expiresAt"] = DateTimeOffset.UtcNow.AddSeconds(seconds).ToUnixTimeMilliseconds();
    }

    internal override AccountQuota Parse(JsonNode data)
    {
        List<QuotaWindow> windows = [];
        foreach (var (key, name) in new[] { ("five_hour", "5h"), ("seven_day", "Week") })
        {
            if (data[key] is JsonObject w && w["utilization"].Number() is double used && used is >= 0 and <= 100)
                windows.Add(new(name, used, w["resets_at"].Timestamp()));
        }
        return AccountQuota.From(windows);
    }
}
