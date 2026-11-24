using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace QuotaGadget;

internal static class AntigravityDesktop
{
    internal const string Unavailable = "Open Antigravity or install agy";
    private const string Metadata = """{"metadata":{"ideName":"antigravity","extensionName":"quota-gadget","locale":"en"}}""";

    internal static IEnumerable<FileInfo> HubFiles(Func<string, string?> readEnvironment)
    {
        List<FileInfo> files = [];
        foreach (var (variable, folder) in new[] { ("USERPROFILE", @".gemini\antigravity\daemon"),
            ("LOCALAPPDATA", @"Antigravity\daemon"), ("APPDATA", @"Antigravity\daemon") })
        {
            if (readEnvironment(variable) is not { } root || !Path.IsPathFullyQualified(root)) continue;
            var directory = new DirectoryInfo(Path.Combine(root, folder));
            if (!directory.Exists) continue;
            try
            {
                files.AddRange(directory.EnumerateFiles("hub_*.json", new EnumerationOptions
                    { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return files.OrderByDescending(file => file.LastWriteTimeUtc).Take(10);
    }

    internal static Task<AccountQuota> FetchAsync(CancellationToken ct) =>
        FetchAsync(HubFiles(Environment.GetEnvironmentVariable), ct);

    internal static async Task<AccountQuota> FetchAsync(IEnumerable<FileInfo> files, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            JsonObject? hub;
            string? token;
            try
            {
                using var stream = file.OpenRead();
                if (stream.Length > 64 * 1024) continue;
                hub = JsonNode.Parse(stream) as JsonObject;
                if (hub?["pid"] is not JsonValue id || !id.TryGetValue(out int pid) || pid <= 0 || !CliProcesses.IsAlive(pid))
                    continue;
                token = hub["csrfToken"].Text();
                if (string.IsNullOrEmpty(token) || token.Any(c => c is < '!' or > '~')) continue;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { continue; }

            foreach (string scheme in new[] { "http", "https" })
            {
                if (hub[scheme + "Port"] is not JsonValue value || !value.TryGetValue(out int port) || port is < 1 or > 65535)
                    continue;
                if (await Probe(scheme, port, token, ct) is { } quota) return quota;
            }
        }
        return AccountQuota.Fail(Unavailable);
    }

    private static async Task<AccountQuota?> Probe(string scheme, int port, string token, CancellationToken ct)
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false, UseCookies = false };
        if (scheme == "https")
            handler.ServerCertificateCustomValidationCallback = (request, cert, chain, errors) =>
                request.RequestUri?.Host == "127.0.0.1" && cert != null &&
                (errors == SslPolicyErrors.None || cert.SubjectName.RawData.AsSpan().SequenceEqual(cert.IssuerName.RawData) &&
                chain != null && chain.ChainStatus.All(s => s.Status == X509ChainStatusFlags.UntrustedRoot));
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri($"{scheme}://127.0.0.1:{port}/exa.language_server_pb.LanguageServerService/"),
            Timeout = TimeSpan.FromSeconds(4),
            MaxResponseContentBufferSize = 4 * 1024 * 1024
        };
        client.DefaultRequestHeaders.Add("X-Codeium-Csrf-Token", token);
        client.DefaultRequestHeaders.Add("Connect-Protocol-Version", "1");

        var summary = await Post("RetrieveUserQuotaSummary", ParseSummary);
        var status = await Post("GetUserStatus", ParseStatus);
        if (summary.Error == null)
            return summary with { Tier = status.Tier ?? summary.Tier, Email = status.Email ?? summary.Email };
        return status.Error == null ? status : null;

        async Task<AccountQuota> Post(string method, Func<JsonNode?, AccountQuota> parse)
        {
            try
            {
                using var body = new StringContent(Metadata, Encoding.UTF8, "application/json");
                using var response = await client.PostAsync(method, body, ct);
                if (response.IsSuccessStatusCode)
                {
                    using var stream = await response.Content.ReadAsStreamAsync(ct);
                    return parse(JsonNode.Parse(stream));
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            catch (Exception ex) when (ex is IOException or HttpRequestException or JsonException or ArgumentException) { }
            return AccountQuota.Fail(Unavailable);
        }
    }

    internal static AccountQuota ParseSummary(JsonNode? data)
    {
        var root = data as JsonObject;
        var groups = (root?["response"] as JsonObject)?["groups"] as JsonArray ?? root?["groups"] as JsonArray;
        return ParseGroups(groups, desktop: true);
    }

    internal static AccountQuota ParseGroups(JsonArray? groups, bool desktop)
    {
        List<QuotaWindow> windows = [];
        if (groups == null) return AccountQuota.From(windows);
        string groupField = desktop ? "displayName" : "name";
        string fractionField = desktop ? "remainingFraction" : "remaining_fraction";
        string resetField = desktop ? "resetTime" : "reset_time";
        foreach (var group in groups.OfType<JsonObject>())
        {
            if (group["buckets"] is not JsonArray buckets) continue;
            string? groupName = group[groupField].Text();
            string? pool = groupName switch
            {
                "Gemini Models" => "Gemini", "Claude and GPT models" => "Claude/GPT", _ => desktop ? null : groupName
            };
            if (!desktop && pool == null) continue;
            foreach (var bucket in buckets.OfType<JsonObject>())
            {
                string? name = desktop ? DesktopBucketName(bucket, pool) : WindowName(pool, bucket["window"].Text());
                if (name == null || ReadWindow(name, bucket, fractionField, resetField) is not { } reading) continue;
                if (desktop) AddReportedWindow(windows, reading);
                else windows.Add(reading);
            }
        }
        return AccountQuota.From(windows);
    }

    private static string? DesktopBucketName(JsonObject bucket, string? pool)
    {
        string? name = bucket["bucketId"].Text() switch
        {
            "gemini-5h" => "Gemini 5h", "gemini-weekly" => "Gemini Week",
            "3p-5h" => "Claude/GPT 5h", "3p-weekly" => "Claude/GPT Week", _ => null
        };
        if (name != null || pool == null) return name;
        string? window = bucket["window"].Text();
        return window is "5h" or "weekly" ? WindowName(pool, window) : null;
    }

    private static string? WindowName(string? pool, string? window) =>
        pool != null && window != null ? pool + " " + (window == "weekly" ? "Week" : window) : null;

    internal static AccountQuota ParseStatus(JsonNode? data)
    {
        List<QuotaWindow> windows = [];
        var status = (data as JsonObject)?["userStatus"] as JsonObject;
        var plan = ((status?["planStatus"] as JsonObject)?["planInfo"] as JsonObject)?["planName"].Text()?.Trim();
        if (string.IsNullOrEmpty(plan)) plan = (status?["userTier"] as JsonObject)?["name"].Text()?.Trim();

        // Legacy model quotas carry a fraction and a reset time without a window duration.
        var config = status?["cascadeModelConfigData"] as JsonObject;
        if (config?["clientModelConfigs"] is JsonArray models)
        {
            foreach (var model in models.OfType<JsonObject>())
            {
                if (model["quotaInfo"] is not JsonObject quota || QuotaIsDisabled(model) ||
                    model["label"].Text() is not string label) continue;
                string? name = null;
                if (label.StartsWith("Gemini", StringComparison.OrdinalIgnoreCase) &&
                    !label.Contains("Argon", StringComparison.OrdinalIgnoreCase)) name = "Gemini";
                else if (label.StartsWith("Claude", StringComparison.OrdinalIgnoreCase) ||
                    label.StartsWith("GPT", StringComparison.OrdinalIgnoreCase)) name = "Claude/GPT";
                if (name != null && ReadWindow(name, quota, "remainingFraction", "resetTime") is { } reading)
                    AddReportedWindow(windows, reading);
            }
        }

        return AccountQuota.From(windows) with
        {
            Tier = string.IsNullOrEmpty(plan) ? null : plan,
            Email = AccountIdentity.Address(status?["email"])
        };
    }

    private static QuotaWindow? ReadWindow(string name, JsonObject quota, string fractionField, string resetField) =>
        !QuotaIsDisabled(quota) && quota[fractionField].Number() is double remaining && remaining is >= 0 and <= 1
            ? new(name, 100 * (1 - remaining), quota[resetField].Timestamp()) : null;

    private static void AddReportedWindow(List<QuotaWindow> windows, QuotaWindow reading)
    {
        int i = windows.FindIndex(window => window.Name == reading.Name);
        if (i < 0) windows.Add(reading);
        else if (reading.UsedPercent > windows[i].UsedPercent ||
            (reading.UsedPercent == windows[i].UsedPercent && windows[i].ResetsAt == null && reading.ResetsAt != null))
            windows[i] = reading;
    }

    private static bool QuotaIsDisabled(JsonObject quota) =>
        (quota["disabled"] is JsonValue disabled && disabled.TryGetValue(out bool dis) && dis) ||
        (quota["enabled"] is JsonValue enabled && enabled.TryGetValue(out bool en) && !en);
}
