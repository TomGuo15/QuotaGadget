using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace QuotaGadget;

internal static class Login
{
    internal const string AcpInitialize = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":1,"clientCapabilities":{}}}""";

    private static readonly SemaphoreSlim CodexCli = new(1, 1);
    private static readonly SemaphoreSlim ClaudeCli = new(1, 1);
    private static readonly SemaphoreSlim AgCli = new(1, 1);

    internal static SemaphoreSlim CliGate(Provider provider) => provider switch
    {
        Provider.Codex => CodexCli, Provider.Claude => ClaudeCli, _ => AgCli
    };

    internal static string FindCli(Provider provider) => FindCli(provider, Environment.GetEnvironmentVariable);

    internal static string FindCli(Provider provider, Func<string, string?> readEnvironment)
    {
        string name = provider switch { Provider.Codex => "codex", Provider.Claude => "claude", _ => "agy" };
        return FindOnPath(name, readEnvironment("PATH")) ?? FindInstalledCli(provider, readEnvironment)
            ?? throw new Win32Exception(2);
    }

    internal static string FindCli(string name) =>
        FindOnPath(name, Environment.GetEnvironmentVariable("PATH")) ?? throw new Win32Exception(2);

    private static string? FindOnPath(string name, string? pathValue)
    {
        foreach (string entry in (pathValue ?? "").Split(Path.PathSeparator))
        {
            string dir = entry.Trim().Trim('"');
            if (!Path.IsPathFullyQualified(dir)) continue;
            string path = Path.Combine(dir, name + ".exe");
            if (File.Exists(path)) return path;
            path = Path.Combine(dir, name + ".cmd");
            if (File.Exists(path)) return path;
        }
        return null;
    }

    private static string? FindInstalledCli(Provider provider, Func<string, string?> readEnvironment)
    {
        var desktop = DesktopCliRoot(provider, readEnvironment);
        return provider switch
        {
            Provider.Codex => ExistingFile(desktop, @"bin\codex.exe")
                ?? ExistingFile(readEnvironment("APPDATA"), @"npm\codex.cmd"),
            Provider.Claude => ExistingFile(readEnvironment("USERPROFILE"), @".local\bin\claude.exe")
                ?? ExistingFile(readEnvironment("LOCALAPPDATA"), @"Programs\claude\claude.exe")
                ?? FindClaudeDesktopCli(desktop),
            _ => ExistingFile(readEnvironment("LOCALAPPDATA"), @"agy\bin\agy.exe")
        };
    }

    private static string? ExistingFile(string? root, string relativePath)
    {
        if (root == null || !Path.IsPathFullyQualified(root)) return null;
        string path = Path.Combine(root, relativePath);
        return File.Exists(path) ? path : null;
    }

    private static string? FindClaudeDesktopCli(string? desktop)
    {
        if (desktop == null) return null;
        string root = Path.Combine(desktop, "claude-code");
        if (!Directory.Exists(root)) return null;
        try
        {
            return Directory.EnumerateFiles(root, "claude.exe", new EnumerationOptions
                { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
                .OrderByDescending(path => Version.TryParse(
                    Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar, 2)[0], out var version) ? version : null)
                .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }
        catch (IOException) { return null; } // A Desktop update can remove a version directory during enumeration.
    }

    private static string? DesktopCliRoot(Provider provider, Func<string, string?> readEnvironment)
    {
        var (variable, directory) = provider switch
        {
            Provider.Codex => ("LOCALAPPDATA", @"OpenAI\Codex"),
            Provider.Claude => ("APPDATA", "Claude"),
            _ => (null, null)
        };
        if (variable == null || readEnvironment(variable) is not { } root || !Path.IsPathFullyQualified(root))
            return null;
        return Path.Combine(root, directory!);
    }

    internal static bool IsDesktopManagedCli(string cli, Func<string, string?> readEnvironment)
    {
        if (!Path.IsPathFullyQualified(cli)) return false;
        string path = Path.GetFullPath(cli);
        return IsUnder(DesktopCliRoot(Provider.Codex, readEnvironment)) ||
            IsUnder(DesktopCliRoot(Provider.Claude, readEnvironment));

        bool IsUnder(string? root) => root != null && path.StartsWith(
            Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    internal static ProcessStartInfo HiddenCli(string cli, string arguments, bool startInJob = false)
    {
        var start = new ProcessStartInfo(cli, arguments)
        {
            WorkingDirectory = Config.Home, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        if (startInJob || cli.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            start.FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            // Wait for the parent to assign the shell to its job before starting any child.
            string gate = startInJob ? "set /p QuotaGadgetStart= && " : "";
            string input = startInJob ? " <nul" : "";
            start.Arguments = $"/d /v:off /s /c \"{gate}\"%QuotaGadgetCli%\" {arguments}{input}\"";
            start.Environment["QuotaGadgetCli"] = cli;
        }
        return start;
    }

    internal static async Task<bool> RunAsync(Account account, CancellationToken ct)
    {
        var gate = CliGate(account.Provider);
        await gate.WaitAsync(ct);
        try { return await RunLoginAsync(account, ct); }
        finally { gate.Release(); }
    }

    private static async Task<bool> RunLoginAsync(Account account, CancellationToken ct)
    {
        string arguments = account.Provider switch
        {
            Provider.Codex => "login",
            Provider.Claude => "auth login --claudeai",
            Provider.Antigravity => "acp --log-file NUL",
            _ => throw new ArgumentOutOfRangeException(nameof(account))
        };
        var start = HiddenCli(FindCli(account.Provider), arguments);
        if (account.Provider == Provider.Antigravity)
        {
            const string authenticate = """{"jsonrpc":"2.0","id":2,"method":"authenticate","params":{"methodId":"oauth-personal"}}""";
            using var process = StartHidden(start, ct);
            try
            {
                var init = await InitializeAcp(process, ct);
                return init?["authMethods"] is JsonArray methods &&
                    methods.OfType<JsonObject>().Any(method => method["id"].Text() == "oauth-personal") &&
                    await AcpRequest(process, 2, authenticate, ct) != null;
            }
            finally { await StopCli(process); }
        }

        Config.CheckOwnedPath(account.ProfileDir);
        Directory.CreateDirectory(account.ProfileDir);
        start.Environment[account.Provider == Provider.Codex ? "CODEX_HOME" : "CLAUDE_CONFIG_DIR"] = account.ProfileDir;
        return await RunCommand(start, ct, captureOutput: false) != null;
    }

    internal static async Task<string?> RunCommand(ProcessStartInfo start, CancellationToken ct, bool captureOutput)
    {
        using var process = StartHidden(start, ct);
        try
        {
            process.StandardInput.Close();
            var output = captureOutput ? process.StandardOutput.ReadToEndAsync(ct) : null;
            var stdout = (Task?)output ?? process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, ct);
            var stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null, ct);
            await process.WaitForExitAsync(ct);
            await Task.WhenAll(stdout, stderr).WaitAsync(ct);
            ct.ThrowIfCancellationRequested();
            if (process.ExitCode != 0) return null;
            return output == null ? "" : await output;
        }
        finally { await StopCli(process); }
    }

    internal static Process StartHidden(ProcessStartInfo start, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Process.Start(start)!;
    }

    internal static async Task<JsonObject?> InitializeAcp(Process process, CancellationToken ct)
    {
        process.BeginErrorReadLine();
        var init = await AcpRequest(process, 1, AcpInitialize, ct);
        return init?["result"] as JsonObject;
    }

    internal static async Task StopCli(Process process)
    {
        if (process.HasExited) return;
        try { process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) when (process.HasExited) { }
        await process.WaitForExitAsync();
    }

    internal static async Task<JsonObject?> AcpRequest(Process process, int id, string request, CancellationToken ct)
    {
        try
        {
            await process.StandardInput.WriteLineAsync(request.AsMemory(), ct);
            while (await process.StandardOutput.ReadLineAsync(ct) is { } line)
            {
                if (TryAcpReply(line, id, out var reply)) return reply;
            }
        }
        catch (IOException) { ct.ThrowIfCancellationRequested(); } // The CLI can exit between requests.
        return null;
    }

    // A matching error ends the request, while unrelated messages can precede its reply.
    internal static bool TryAcpReply(string line, int id, out JsonObject? successfulReply)
    {
        successfulReply = null;
        JsonObject? reply;
        try { reply = JsonNode.Parse(line) as JsonObject; }
        catch (JsonException) { return false; }
        if (reply == null || reply.ContainsKey("method") || reply["id"].Number() != id) return false;
        if (!reply.ContainsKey("error") && reply.ContainsKey("result")) successfulReply = reply;
        return true;
    }
}
