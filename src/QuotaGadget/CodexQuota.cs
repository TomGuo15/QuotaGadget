using System.Text.Json.Nodes;

namespace QuotaGadget;

internal sealed class CodexQuota(HttpClient http) : OAuthQuota(http)
{
    protected override string CredentialFile => "auth.json";
    protected override HttpRequestMessage UsageRequest(JsonNode auth) => Get(auth, "usage");

    private static HttpRequestMessage Get(JsonNode auth, string resource)
    {
        var tokens = auth["tokens"];
        var request = BearerGet($"https://chatgpt.com/backend-api/wham/{resource}", tokens?["access_token"]);
        request.Headers.Add("ChatGPT-Account-Id", Required(tokens?["account_id"]));
        return request;
    }

    protected override async Task Refresh(JsonNode auth, CancellationToken ct)
    {
        var tokens = auth["tokens"]!;
        var result = await Exchange("https://auth.openai.com/oauth/token", "app_EMoamEEZ73f0CkXaXp7hrann",
            tokens["refresh_token"], ct);
        tokens["access_token"] = Required(result["access_token"]);
        foreach (var key in new[] { "refresh_token", "id_token" })
            if (result[key].Text() is { Length: > 0 } value) tokens[key] = value;
        auth["last_refresh"] = DateTimeOffset.UtcNow;
    }

    internal override AccountQuota Parse(JsonNode data)
    {
        List<QuotaWindow> windows = [];
        var limits = data["rate_limit"] as JsonObject;
        foreach (var key in new[] { "primary_window", "secondary_window" })
        {
            if (limits?[key] is not JsonObject w || w["used_percent"].Number() is not double used ||
                used is < 0 or > 100) continue;
            var name = w["limit_window_seconds"].Number() switch
            {
                18000 => "5h", 604800 => "Week", 86400 => "Day",
                double s when s > 0 => (s / 3600).ToString("0.#") + "h",
                _ => key == "primary_window" ? "Primary" : "Secondary"
            };
            var epoch = w["reset_at"].Number();
            DateTimeOffset? reset = epoch is >= 0 and <= 253402300799 ?
                DateTimeOffset.FromUnixTimeSeconds((long)epoch.Value) : null;
            windows.Add(new(name, used, reset));
        }
        var quota = AccountQuota.From(windows);
        return quota with { Resets = Count(data["rate_limit_reset_credits"]) is int count && count > 0 ?
            new(count, []) : null };
    }

    private static int? Count(JsonNode? data) => data is JsonObject obj &&
        obj["available_count"] is JsonValue value && value.TryGetValue<int>(out var count) && count >= 0 ? count : null;

    protected override async Task<AccountQuota> ReadUsage(JsonNode data, JsonNode auth, CancellationToken ct)
    {
        var quota = Parse(data);
        if (quota.Resets == null) return quota;
        // Preserve the usage snapshot if only the optional expiry lookup fails.
        using var request = Get(auth, "rate-limit-reset-credits");
        try
        {
            using var response = await Http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return quota;
            var detail = await ReadJson(response, ct);
            if (Count(detail) is not int count) return quota;
            var expiries = (detail["credits"] as JsonArray)?.OfType<JsonObject>()
                .Where(credit => credit["status"].Text() == "available")
                .Select(credit => credit["expires_at"].Timestamp()).OfType<DateTimeOffset>().Order().ToArray() ?? [];
            return quota with { Resets = count == 0 ? null : new(count, expiries) };
        }
        catch (HttpRequestException) { return quota; }
        catch (System.Text.Json.JsonException) { return quota; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return quota; }
    }
}
