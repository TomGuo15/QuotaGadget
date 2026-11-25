using System.ComponentModel;

namespace QuotaGadget;

internal readonly record struct CliUpdateResult(bool AllInUse, bool Upgraded, string Message);

internal sealed class CliUpdater(AntigravityQuota antigravity, Func<Provider, string>? findCli = null,
    Func<Provider, bool>? isInUse = null, TimeSpan? timeout = null, Func<string>? findNpm = null,
    Func<string, string?>? readEnvironment = null)
{
    private static readonly Provider[] Providers = [Provider.Codex, Provider.Claude, Provider.Antigravity];

    internal async Task<CliUpdateResult> RunAsync(CancellationToken ct)
    {
        List<string> skipped = [];
        int unchanged = 0, upgraded = 0, failed = 0;
        bool desktopManaged = false;
        string? upgrade = null;
        var environment = readEnvironment ?? Environment.GetEnvironmentVariable;
        foreach (var provider in Providers)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                string cli;
                try { cli = findCli?.Invoke(provider) ?? Login.FindCli(provider, environment); }
                catch (Win32Exception ex) when (ex.NativeErrorCode == 2) { continue; }
                if (Login.IsDesktopManagedCli(cli, environment)) { desktopManaged = true; continue; }

                var gate = Login.CliGate(provider);
                if (!gate.Wait(0, ct)) { skipped.Add(DisplayName(provider)); continue; }
                try
                {
                    if (IsInUse(provider)) { skipped.Add(DisplayName(provider)); continue; }
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(120));
                    string? before = await ReadVersionAsync(cli, provider, deadline.Token).ConfigureAwait(false);
                    if (before == null) { failed++; continue; }
                    if (IsInUse(provider)) { skipped.Add(DisplayName(provider)); continue; }
                    // An interrupted update may already have replaced agy on disk.
                    if (provider == Provider.Antigravity) antigravity.InvalidateVersion();
                    if (!await UpdateAsync(cli, provider, deadline.Token).ConfigureAwait(false)) { failed++; continue; }
                    string? after = await ReadVersionAsync(cli, provider, deadline.Token).ConfigureAwait(false);
                    if (after == null) { failed++; continue; }
                    int change = CompareVersions(after, before);
                    if (change == 0) unchanged++;
                    else if (change < 0) failed++;
                    else
                    {
                        upgraded++;
                        upgrade = $"Updated {DisplayName(provider)} {before} → {after}";
                    }
                }
                finally { gate.Release(); }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { failed++; }
            catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
            { failed++; }
        }

        ct.ThrowIfCancellationRequested();
        List<string> parts = [];
        if (upgraded > 0) parts.Add(upgraded == 1 && skipped.Count == 0 && failed == 0 ? upgrade! : $"Updated {upgraded}");
        if (skipped.Count > 0) parts.Add($"{(parts.Count == 0 ? "Skipped" : "skipped")} {string.Join(", ", skipped)} (in use)");
        if (failed > 0) parts.Add($"{(parts.Count == 0 ? "Failed" : "failed")} {failed}");
        if (unchanged > 0 && (skipped.Count > 0 || failed > 0)) parts.Add($"{unchanged} up to date");
        if (parts.Count == 0)
            parts.Add(unchanged > 0 ? "CLIs up to date" : desktopManaged ? "Desktop apps manage these CLIs" : "No CLIs found");
        return new(skipped.Count > 0 && unchanged == 0 && upgraded == 0 && failed == 0, upgraded > 0, string.Join(" · ", parts));
    }

    private bool IsInUse(Provider provider) => (isInUse ?? CliProcesses.IsInUse)(provider);

    private static string DisplayName(Provider provider) => provider switch
    {
        Provider.Codex => "Codex", Provider.Claude => "Claude", _ => "agy"
    };

    private async Task<bool> UpdateAsync(string cli, Provider provider, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(cli)!;
        // On Windows the Codex npm launcher keeps its native dependency open while running `codex update`.
        bool npm = provider == Provider.Codex && cli.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) &&
            File.Exists(Path.Combine(directory, "node_modules", "@openai", "codex", "bin", "codex.js"));
        var start = npm
            ? Login.HiddenCli(findNpm?.Invoke() ?? Login.FindCli("npm"), "install -g @openai/codex@latest", startInJob: true)
            : Login.HiddenCli(cli, "update", startInJob: true);
        if (npm) start.Environment["npm_config_prefix"] = directory;
        start.WorkingDirectory = directory;
        var (exit, _) = await CliProcesses.RunAsync(start, captureVersion: false, ct).ConfigureAwait(false);
        return exit == 0;
    }

    private static async Task<string?> ReadVersionAsync(string cli, Provider provider, CancellationToken ct)
    {
        var start = Login.HiddenCli(cli, "--version", startInJob: true);
        start.WorkingDirectory = Path.GetDirectoryName(cli)!;
        var (exit, output) = await CliProcesses.RunAsync(start, captureVersion: true, ct).ConfigureAwait(false);
        return exit == 0 ? ParseVersion(provider, output.AsSpan()) : null;
    }

    internal static string? ParseVersion(Provider provider, ReadOnlySpan<char> output)
    {
        foreach (var line in output.EnumerateLines())
        {
            var version = line.Trim();
            const string codexPrefix = "codex-cli ";
            const string claudeSuffix = " (Claude Code)";
            if (provider == Provider.Codex && version.StartsWith(codexPrefix, StringComparison.OrdinalIgnoreCase))
                version = version[codexPrefix.Length..];
            if (provider == Provider.Claude && version.EndsWith(claudeSuffix, StringComparison.OrdinalIgnoreCase))
                version = version[..^claudeSuffix.Length];

            int suffix = version.IndexOfAny('-', '+');
            if (!Version.TryParse(suffix < 0 ? version : version[..suffix], out var numeric) || numeric.Build < 0 ||
                version.Length > 64 || suffix == version.Length - 1) continue;
            foreach (char c in version)
            {
                if (char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '+') continue;
                version = default;
                break;
            }
            if (!version.IsEmpty) return version.ToString();
        }
        return null;
    }

    private static int CompareVersions(ReadOnlySpan<char> after, ReadOnlySpan<char> before)
    {
        if (after.SequenceEqual(before)) return 0;
        int order = TakeCoreVersion(ref after).CompareTo(TakeCoreVersion(ref before));
        if (order != 0) return order;
        if (after.IsEmpty || before.IsEmpty) return after.IsEmpty.CompareTo(before.IsEmpty);
        while (!after.IsEmpty && !before.IsEmpty)
        {
            var aId = Take(ref after, '.');
            var bId = Take(ref before, '.');
            bool aNumeric = aId.IndexOfAnyExceptInRange('0', '9') < 0;
            bool bNumeric = bId.IndexOfAnyExceptInRange('0', '9') < 0;
            if (aNumeric != bNumeric) return bNumeric.CompareTo(aNumeric);
            order = aNumeric ? aId.Length.CompareTo(bId.Length) : 0;
            if (order == 0) order = aId.SequenceCompareTo(bId);
            if (order != 0) return order;
        }
        return before.IsEmpty.CompareTo(after.IsEmpty);
    }

    private static Version TakeCoreVersion(ref ReadOnlySpan<char> version)
    {
        int metadata = version.IndexOf('+');
        if (metadata >= 0) version = version[..metadata];
        return Version.Parse(Take(ref version, '-'));
    }

    private static ReadOnlySpan<char> Take(ref ReadOnlySpan<char> rest, char separator)
    {
        int end = rest.IndexOf(separator);
        var part = end < 0 ? rest : rest[..end];
        rest = end < 0 ? default : rest[(end + 1)..];
        return part;
    }
}
