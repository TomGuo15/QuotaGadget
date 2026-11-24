using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace QuotaGadget;

internal sealed class AntigravityQuota(TimeProvider? clock = null, Func<string>? findCli = null,
    Func<string, string, CancellationToken, Task<string?>>? runCli = null,
    Func<CancellationToken, Task<AccountQuota>>? fetchDesktop = null)
{
    private readonly Func<string, string, CancellationToken, Task<string?>> readCli = runCli ?? Run;
    private Version? installed;
    private DateTimeOffset nextTierLookup;
    private string? tier;
    private string? tierEmail;

    internal async Task<AccountQuota> FetchAsync(string? email, CancellationToken ct)
    {
        AccountQuota cli;
        var gate = Login.CliGate(Provider.Antigravity);
        await gate.WaitAsync(ct);
        try { cli = await TryFetchCliAsync(email, ct); }
        finally { gate.Release(); }

        ct.ThrowIfCancellationRequested();
        if (cli.Error == null) return cli;
        var desktop = await (fetchDesktop ?? AntigravityDesktop.FetchAsync)(ct);
        ct.ThrowIfCancellationRequested();
        return desktop.Error == null ? desktop : cli;
    }

    internal void InvalidateVersion() => installed = null;

    private async Task<AccountQuota> TryFetchCliAsync(string? email, CancellationToken ct)
    {
        string? exe = null;
        try
        {
            exe = findCli?.Invoke() ?? Login.FindCli(Provider.Antigravity);
            return await FetchCliAsync(exe, email, ct);
        }
        catch (Win32Exception ex) when (exe == null && ex.NativeErrorCode == 2)
        { return AccountQuota.Fail("Open Antigravity or install agy"); }
        catch (Win32Exception) { return AccountQuota.Fail("Install agy and check PATH"); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { return AccountQuota.Fail("Quota request timed out"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
            JsonException or FormatException or InvalidOperationException or ArgumentException or OverflowException)
        { return AccountQuota.Fail("Check login or quota data"); }
    }

    private async Task<AccountQuota> FetchCliAsync(string exe, string? email, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(90));
        if (installed == null)
        {
            var version = await readCli(exe, "--version", deadline.Token);
            Version.TryParse(version?.Trim(), out installed);
        }
        if (installed == null || installed < new Version(1, 1, 11))
            return AccountQuota.Fail("Update agy for quota reports");
        var usage = readCli(exe, "-p /usage --output-format json --log-file NUL", deadline.Token);
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        bool accountChanged = !string.Equals(email, tierEmail, StringComparison.OrdinalIgnoreCase);
        if (installed >= new Version(1, 4, 0) && (now >= nextTierLookup || accountChanged))
        {
            if (accountChanged) tier = null;
            tierEmail = email;
            nextTierLookup = now.AddHours(6);
            var lookup = LookupTier(exe, deadline.Token);
            await Task.WhenAll(usage, lookup);
            tier = await lookup;
        }
        var report = await usage;
        if (report == null) return AccountQuota.Fail("Check agy login or connection");
        var quota = Parse(JsonNode.Parse(report) ?? throw new JsonException());
        return quota.Error == null ? quota with { Tier = tier, Email = email } : quota;
    }

    private static async Task<string?> LookupTier(string exe, CancellationToken ct)
    {
        using var tierTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        tierTimeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            return await ReadTier(exe, tierTimeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException) { return null; }
    }

    private static async Task<string?> ReadTier(string exe, CancellationToken ct)
    {
        var dir = Directory.CreateTempSubdirectory("QuotaGadget-");
        try
        {
            var start = Login.HiddenCli(exe, "acp --log-file NUL --include-default-customizations=false --disable-telemetry");
            start.WorkingDirectory = dir.FullName;
            // session/new saves conversation history under the CLI home, independently of cwd.
            start.Environment["USERPROFILE"] = dir.FullName;
            start.Environment.Remove("ANTIGRAVITY_GEMINI_DIR");
            start.Environment.Remove("ANTIGRAVITY_APP_DATA_DIR");
            using var process = Login.StartHidden(start, ct);
            try
            {
                if (await Login.InitializeAcp(process, ct) == null) return null;
                var session = new JsonObject
                {
                    ["jsonrpc"] = "2.0", ["id"] = 2, ["method"] = "session/new",
                    ["params"] = new JsonObject { ["cwd"] = dir.FullName, ["mcpServers"] = new JsonArray() }
                };
                var reply = await Login.AcpRequest(process, 2, session.ToJsonString(), ct);
                process.StandardInput.Close();
                await process.WaitForExitAsync(ct);
                return ReportedTier(reply?["result"] as JsonObject);
            }
            finally { await Login.StopCli(process); }
        }
        finally { dir.Delete(recursive: true); }
    }

    internal static string? ReportedTier(JsonObject? result)
    {
        var meta = result?["_meta"] as JsonObject;
        var provider = meta?["antigravity"] as JsonObject;
        var tier = provider?["tier"] as JsonObject;
        string? id = tier?["id"].Text();
        if (id is not null && id.StartsWith("g1-", StringComparison.Ordinal) &&
            id.EndsWith("-tier", StringComparison.Ordinal) && id.Length > 8)
        {
            string plan = id[3..^5];
            if (plan is "free" or "plus" or "pro" or "ultra" or "ultra-lite")
                return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(plan.Replace('-', ' '));
        }

        string? name = tier?["name"].Text()?.Trim();
        const string prefix = "Google AI ";
        if (name?.StartsWith(prefix, StringComparison.Ordinal) == true) name = name[prefix.Length..];
        return name switch
        {
            "Ultra (5x)" => "Ultra 5×", "Ultra (20x)" => "Ultra 20×",
            null or "" or "Google AI" => null,
            _ => name
        };
    }

    private static Task<string?> Run(string exe, string arguments, CancellationToken ct) =>
        Login.RunCommand(Login.HiddenCli(exe, arguments), ct, captureOutput: true);

    internal static AccountQuota Parse(JsonNode data)
    {
        var groups = data["status"].Text() == "SUCCESS" && data["command"]?["name"].Text() == "usage"
            ? data["command"]?["data"]?["groups"] as JsonArray : null;
        return AntigravityDesktop.ParseGroups(groups, desktop: false);
    }
}
