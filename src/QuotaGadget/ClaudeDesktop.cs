using System.Text.Json;
using System.Text.Json.Nodes;

namespace QuotaGadget;

internal static class ClaudeDesktop
{
    private const string FileName = "plan-usage-history.json";
    private const int MaxHistoryBytes = 4 * 1024 * 1024;

    internal static IEnumerable<string> HistoryFiles() => HistoryFiles(Environment.GetEnvironmentVariable);

    internal static IEnumerable<string> HistoryFiles(Func<string, string?> readEnvironment)
    {
        List<string> files = [];
        if (readEnvironment("APPDATA") is { } roaming && Path.IsPathFullyQualified(roaming))
            Add(Path.Combine(roaming, "Claude"));
        if (readEnvironment("LOCALAPPDATA") is { } local && Path.IsPathFullyQualified(local))
        {
            try
            {
                foreach (var package in Directory.EnumerateDirectories(Path.Combine(local, "Packages"), "Claude_*"))
                    Add(Path.Combine(package, "LocalCache", "Roaming", "Claude"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return files;

        void Add(string root)
        {
            string path = Path.Combine(root, FileName);
            if (File.Exists(path)) files.Add(path);
        }
    }

    internal static async Task<AccountQuota> ReadAsync(IEnumerable<string> paths, DateTimeOffset now, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        bool found = false;
        double nowMs = (now - DateTimeOffset.UnixEpoch).TotalMilliseconds;
        List<QuotaWindow>? latestWindows = null;
        double latestTime = double.NegativeInfinity;
        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested();
            found = true;
            try
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, bufferSize: 1,
                    options: FileOptions.Asynchronous | FileOptions.SequentialScan);
                long length = stream.Length;
                if (length is 0 or > MaxHistoryBytes) continue;
                var bytes = new byte[(int)length];
                await stream.ReadExactlyAsync(bytes, ct);
                if (stream.Length != length) continue;

                int offset = bytes.AsSpan().StartsWith(System.Text.Encoding.UTF8.Preamble) ? 3 : 0;
                using var history = JsonDocument.Parse(bytes.AsMemory(offset));
                if (LatestSample(history.RootElement) is not { } latest || latest.Time <= latestTime ||
                    latest.Time - nowMs > TimeSpan.FromMinutes(1).TotalMilliseconds ||
                    string.IsNullOrWhiteSpace(Scalar(latest.Sample, "org").Text())) continue;

                var usage = Field(latest.Sample, "u");
                List<QuotaWindow> windows = [];
                if (Scalar(usage, "fh").Number() is double shortUsed && shortUsed is >= 0 and <= 100)
                    windows.Add(new("5h", shortUsed, null));
                if (Scalar(usage, "sd").Number() is double weeklyUsed && weeklyUsed is >= 0 and <= 100)
                    windows.Add(new("Week", weeklyUsed, null));
                if (windows.Count == 0) continue;
                latestTime = latest.Time;
                latestWindows = windows;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { }
        }
        if (latestWindows == null) return AccountQuota.Fail(found ? "Quota unavailable" : "Sign in to load quota");
        return nowMs - latestTime > TimeSpan.FromHours(2).TotalMilliseconds
            ? AccountQuota.Fail("Open Claude Desktop to refresh") : AccountQuota.From(latestWindows);
    }

    private static (double Time, JsonElement Sample)? LatestSample(JsonElement history)
    {
        if (Scalar(history, "version") is not { } version || !version.TryGetValue(out int number) ||
            number is not (1 or 2) || Field(history, "samples") is not { ValueKind: JsonValueKind.Array } samples)
            return null;

        double latestTime = double.NegativeInfinity;
        JsonElement latest = default;
        foreach (var sample in samples.EnumerateArray())
        {
            var timestamp = Field(sample, "t");
            if (timestamp.ValueKind != JsonValueKind.Number || !timestamp.TryGetDouble(out double time) ||
                !double.IsFinite(time) || time <= latestTime) continue;
            latest = sample;
            latestTime = time;
        }
        return latest.ValueKind == JsonValueKind.Object ? (latestTime, latest) : null;
    }

    // Look up only the requested UTF-8 key, retaining the last occurrence of repeated fields.
    private static JsonElement Field(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var value) ? value : default;

    private static JsonValue? Scalar(JsonElement obj, string name)
    {
        var field = Field(obj, name);
        return field.ValueKind is JsonValueKind.String or JsonValueKind.Number ? JsonValue.Create(field) : null;
    }
}
