using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace QuotaGadget;

internal sealed record QuotaWindow(string Name, double UsedPercent, DateTimeOffset? ResetsAt);
internal sealed record SavedResets(int Count, DateTimeOffset[] Expiries)
{
    internal string Tooltip => $"{Count} reset{(Count == 1 ? "" : "s")}" +
        (Expiries.Length == 0 ? "" : "\n" + string.Join(" · ", Expiries.Take(3).Select(date => date.LocalDateTime.ToString("MMM d"))) +
            (Expiries.Length > 3 ? $" · +{Expiries.Length - 3}" : ""));
}
internal sealed record AccountQuota(IReadOnlyList<QuotaWindow> Windows, string? Error = null, string? Tier = null,
    SavedResets? Resets = null, string? Email = null)
{
    internal static AccountQuota Fail(string reason) => new([], reason);
    internal static AccountQuota From(List<QuotaWindow> windows) =>
        windows.Count > 0 ? new(windows) : Fail("Quota unavailable");
}

internal static class Fields
{
    internal static string? Text(this JsonNode? node) =>
        node is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    internal static double? Number(this JsonNode? node) =>
        node is JsonValue v && v.TryGetValue(out double n) && double.IsFinite(n) ? n : null;

    internal static DateTimeOffset? Timestamp(this JsonNode? node) =>
        DateTimeOffset.TryParse(node.Text(), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var date) ? date : null;
}

// The two file-backed CLIs have the same 401 recovery sequence and different token schemas.
internal abstract class OAuthQuota(HttpClient http)
{
    protected HttpClient Http => http;
    protected abstract string CredentialFile { get; }
    protected abstract HttpRequestMessage UsageRequest(JsonNode auth);
    protected abstract Task Refresh(JsonNode auth, CancellationToken ct);
    internal abstract AccountQuota Parse(JsonNode data);
    protected virtual Task<AccountQuota> ReadUsage(JsonNode data, JsonNode auth, CancellationToken ct) =>
        Task.FromResult(Parse(data));
    protected virtual Task<AccountQuota> MissingDefaultCredentials(Account account, CancellationToken ct) =>
        Task.FromResult(AccountQuota.Fail("Sign in to load quota"));

    protected static string Required(JsonNode? field) =>
        !string.IsNullOrWhiteSpace(field.Text()) ? field.Text()! : throw new InvalidDataException();

    protected static HttpRequestMessage BearerGet(string url, JsonNode? token)
    {
        var message = new HttpRequestMessage(HttpMethod.Get, url);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Required(token));
        return message;
    }

    protected async Task<JsonNode> Exchange(string url, string clientId, JsonNode? token, CancellationToken ct)
    {
        var payload = new JsonObject
        {
            ["grant_type"] = "refresh_token", ["client_id"] = clientId, ["refresh_token"] = Required(token)
        };
        using var response = await http.PostAsJsonAsync(url, payload, GadgetJson.Default.JsonObject, ct);
        response.EnsureSuccessStatusCode();
        return await ReadJson(response, ct);
    }

    protected static async Task<JsonNode> ReadJson(HttpResponseMessage response, CancellationToken ct) =>
        await response.Content.ReadFromJsonAsync(GadgetJson.Default.JsonNode, ct) ?? throw new JsonException();

    internal async Task<AccountQuota> FetchAsync(Account account, CancellationToken ct, Func<Provider, string>? defaultDir = null)
    {
        var path = Path.Combine(account.ProfileDir, CredentialFile);
        bool owned = Config.CheckProfile(account, defaultDir);
        string original;
        try
        {
            original = await ReadCredentials();
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return owned ? AccountQuota.Fail("Sign in to load quota") : await MissingDefaultCredentials(account, ct);
        }
        var auth = JsonNode.Parse(original) ?? throw new InvalidDataException();
        for (int attempt = 0; ; attempt++)
        {
            using var request = UsageRequest(auth);
            using var response = await http.SendAsync(request, ct);
            if (response.StatusCode != HttpStatusCode.Unauthorized)
            {
                response.EnsureSuccessStatusCode();
                return await ReadUsage(await ReadJson(response, ct), auth, ct);
            }
            if (!owned || attempt > 0) return AccountQuota.Fail("Login expired — re-login");
            await Refresh(auth, ct);
            // A login console may have replaced the file while the request was in flight.
            Config.CheckOwnedPath(account.ProfileDir);
            var latest = await ReadCredentials();
            if (latest == original) Config.WriteAtomic(path, auth.ToJsonString());
            else auth = JsonNode.Parse(latest) ?? throw new InvalidDataException();
        }

        Task<string> ReadCredentials()
        {
            Config.CheckCredentialFile(path, owned);
            return File.ReadAllTextAsync(path, ct);
        }
    }
}
