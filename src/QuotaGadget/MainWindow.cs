using System.ComponentModel;
using System.Net;
using System.Runtime;
using System.Text.Json;
using static QuotaGadget.Native;

namespace QuotaGadget;

internal sealed partial class MainWindow
{
    private readonly Config config;
    private readonly string configPath;
    private readonly HttpMessageHandler? handler;
    private HttpClient? http;
    private HttpClient Http => http ??= new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(25) };
    private readonly string cachePath;
    private readonly Dictionary<Account, CachedQuota> cache;
    private readonly Dictionary<Account, string> errors = [];
    private readonly AntigravityQuota antigravity = new();
    private readonly Dictionary<Account, AccountIdentity> identities = [];
    private readonly Dictionary<Account, Task> pending = [];
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? loginCancellation;
    private Task loginTask = Task.CompletedTask;
    private bool closing;

    private bool refreshing;
    private string updated = "";
    private string? status;
    private bool waiting;
    private Layout layout = null!;
    internal bool Refreshing => refreshing;
    internal Layout Layout => layout;
    internal string Updated => updated;

    private MainWindow() : this(startupKey: RunKey)
    {
        updateClis = new CliUpdater(antigravity).RunAsync;
    }

    internal MainWindow(Config? settings = null, string? configPath = null, HttpMessageHandler? handler = null,
        string? startupKey = null, Func<CancellationToken, Task<CliUpdateResult>>? updateClis = null,
        Func<List<Account>>? discoverDefaults = null)
    {
        this.startupKey = startupKey;
        this.updateClis = updateClis;
        this.configPath = Path.GetFullPath(configPath ?? Config.FilePath);
        string home = Path.GetDirectoryName(this.configPath)!;
        Directory.CreateDirectory(home);
        cachePath = Path.Combine(home, "cache.json");
        config = settings ?? Config.Load(this.configPath);
        if (settings == null && !File.Exists(this.configPath) && (discoverDefaults != null || configPath == null))
        {
            config.Accounts = discoverDefaults == null ? Config.DiscoverDefaultAccounts() : discoverDefaults();
            if (config.Accounts.Count > 0) Save();
        }
        cache = QuotaCache.Load(cachePath, config.Accounts);
        if (cache.Count > 0) updated = cache.Values.Max(entry => entry.FetchedAt).LocalDateTime.ToString("HH:mm");
        this.handler = handler;
        Render();
    }

    private void SetStatus(string? message, bool waiting = false)
    {
        status = message;
        this.waiting = waiting;
        Render();
    }

    private async Task StartLogin(Provider provider)
    {
        if (loginCancellation != null || closing) return;
        var account = new Account(provider, provider == Provider.Antigravity ? "" :
            Path.Combine(Config.Profiles, Guid.NewGuid().ToString("N")));
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel.Token);
        deadline.CancelAfter(TimeSpan.FromMinutes(10));
        loginCancellation = cancel;
        SetStatus("Waiting for sign-in in your browser…", waiting: true);
        bool added = false;
        try
        {
            bool success = await Login.RunAsync(account, deadline.Token);
            deadline.Token.ThrowIfCancellationRequested();
            if (success) added = Add(account);
            else SetStatus($"{provider} sign-in failed. Try again.");
        }
        catch (OperationCanceledException)
        { SetStatus(cancel.IsCancellationRequested ? "Sign-in cancelled." : "Sign-in timed out. Try again."); }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        { SetStatus($"Could not complete sign-in. Check that the {provider} CLI is installed and on PATH."); }
        finally
        {
            loginCancellation = null;
            if (!added && Config.IsOwned(account.ProfileDir) && Directory.Exists(account.ProfileDir))
            {
                try
                {
                    Config.CheckOwnedPath(account.ProfileDir);
                    Directory.Delete(account.ProfileDir, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                { SetStatus("Could not remove the unused sign-in profile."); }
            }
        }
    }

    private bool Add(Account a)
    {
        string? email = AccountIdentity.Read(a).Email;
        if (config.Accounts.Any(other => other.Provider == a.Provider &&
            (string.Equals(other.ProfileDir, a.ProfileDir, StringComparison.OrdinalIgnoreCase) ||
             (email != null && string.Equals(email, AccountIdentity.Read(other).Email ?? identities.GetValueOrDefault(other).Email,
                 StringComparison.OrdinalIgnoreCase)))))
        {
            SetStatus("This account is already shown.");
            return false;
        }
        SetStatus(null);
        config.Accounts.Add(a);
        if (!Save()) { config.Accounts.Remove(a); return false; }
        Render();
        if (!refreshing) RefreshAll();
        else StartFetch(a);
        return true;
    }
    private void Remove(Account a)
    {
        int index = config.Accounts.IndexOf(a);
        config.Accounts.RemoveAt(index);
        if (Save())
        {
            if (cache.Remove(a)) SaveCache();
            errors.Remove(a);
            identities.Remove(a);
        }
        else config.Accounts.Insert(index, a);
        Render();
    }
    private bool Save(bool notify = true)
    {
        try { config.Save(configPath); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (notify) Notice("Could not save accounts.json. Check the gadget folder permissions.");
            return false;
        }
    }
    private void Notice(string message) => MessageBoxW(hwnd, message, "QuotaGadget", 0x40);

    private void MoveAccount(Account account, int destination)
    {
        var list = config.Accounts;
        int origin = list.IndexOf(account);
        if (origin < 0 || destination < 0 || destination >= list.Count || origin == destination) return;
        list.RemoveAt(origin);
        list.Insert(destination, account);
        if (!Save())
        {
            list.RemoveAt(destination);
            list.Insert(origin, account);
        }
        Render();
        selected = config.Accounts.IndexOf(account);
    }

    private async void RefreshAll()
    {
        if (closing || refreshing) return;
        refreshing = true;
        SetTimer(hwnd, SpinTimer, 60, 0);
        try
        {
            foreach (var account in config.Accounts) StartFetch(account);
            // An account added while this batch is running starts its own fetch immediately.
            while (pending.Count > 0)
            {
                var batch = pending.ToArray();
                await Task.WhenAll(batch.Select(entry => entry.Value));
                foreach (var (account, task) in batch)
                    if (pending[account] == task) pending.Remove(account);
            }
            if (!closing)
            {
                updated = DateTime.Now.ToString("HH:mm");
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            }
        }
        finally
        {
            refreshing = false;
            if (hwnd != 0)
            {
                KillTimer(hwnd, SpinTimer);
                InvalidateRect(hwnd, 0, 0);
            }
        }
    }

    private void StartFetch(Account account)
    {
        var identity = AccountIdentity.Read(account);
        // The IDE may be signed in as a different user from the Antigravity CLI.
        if (account.Provider != Provider.Antigravity) SetIdentity(account, identity);
        if (!pending.TryGetValue(account, out var task) || task.IsCompleted) pending[account] = Fetch(account, identity.Email);
    }

    internal void SetIdentity(Account account, AccountIdentity identity)
    {
        identities[account] = identity;
        if (cache.TryGetValue(account, out var previous) && previous.Email != null &&
            (identity.Email != null || identity.ClaudeDesktopLogin) &&
            !string.Equals(previous.Email, identity.Email, StringComparison.OrdinalIgnoreCase))
        {
            cache.Remove(account);
            SaveCache();
        }
        if (identity.ClaudeDesktopLogin || (identity.Email != null && identity.Email != previous?.Email) ||
            (identity.Tier != null && identity.Tier != previous?.Tier)) Render();
    }

    private async Task Fetch(Account account, string? email)
    {
        AccountQuota result;
        try
        {
            result = await (account.Provider switch
            {
                Provider.Codex => new CodexQuota(Http).FetchAsync(account, lifetime.Token),
                Provider.Claude => new ClaudeQuota(Http).FetchAsync(account, lifetime.Token),
                _ => antigravity.FetchAsync(email, lifetime.Token)
            });
        }
        catch (OperationCanceledException) when (closing) { return; }
        catch (OperationCanceledException) { result = AccountQuota.Fail("Quota request timed out"); }
        catch (HttpRequestException ex)
        {
            result = AccountQuota.Fail(ex.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest => "Login expired — re-login",
                HttpStatusCode.Forbidden => "Quota access denied",
                HttpStatusCode.TooManyRequests => "Rate limited — try later",
                null => "Check your connection",
                _ => $"Quota service error ({(int)ex.StatusCode})"
            });
        }
        catch (Win32Exception) { result = AccountQuota.Fail("Install agy and check PATH"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
            JsonException or FormatException or InvalidOperationException or ArgumentException or OverflowException)
        { result = AccountQuota.Fail("Check login or quota data"); }
        if (!closing && config.Accounts.Contains(account)) DisplayQuota(account, result);
    }

    internal void DisplayQuota(Account account, AccountQuota result)
    {
        if (result.Error is { } error) errors[account] = error;
        else
        {
            errors.Remove(account);
            if (account.Provider == Provider.Antigravity) identities[account] = new(result.Email, result.Tier);
            var identity = identities.GetValueOrDefault(account);
            cache[account] = new(account, result.Windows.ToArray(), identity.Tier ?? result.Tier, identity.Email,
                DateTimeOffset.UtcNow, result.Resets);
            SaveCache();
        }
        Render();
    }

    private void SaveCache()
    {
        try { QuotaCache.Save(cachePath, cache.Values); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { SetStatus("Could not save the last quota update to disk."); }
    }

    private string Header(Account account)
    {
        var identity = identities.GetValueOrDefault(account);
        return identity.Email ?? (identity.ClaudeDesktopLogin ? "Claude Desktop" : cache.GetValueOrDefault(account)?.Email)
            ?? $"{account.Provider} account";
    }

    private void Render()
    {
        var now = DateTimeOffset.UtcNow;
        var cards = new List<Card>(config.Accounts.Count);
        foreach (var account in config.Accounts)
        {
            var entry = cache.GetValueOrDefault(account);
            cards.Add(new(account, Header(account), identities.GetValueOrDefault(account).Tier ?? entry?.Tier,
                errors.GetValueOrDefault(account) ?? (entry == null ? "Loading quota…" : null),
                QuotaRow.Build(account.Provider, entry?.Windows ?? [], now), entry != null &&
                    (entry.IsStale(now) || account.Provider == Provider.Antigravity && errors.ContainsKey(account)),
                account.Provider == Provider.Codex && entry?.Resets is { Count: > 0, Expiries: not null } resets ? resets : null));
        }
        Arrange(cards);
        if (floating?.IsTip == true) HideTip();
        if (floating?.IsTray == true) floating.UpdateCards(cards);
    }

    internal static string Countdown(DateTimeOffset? reset, DateTimeOffset now)
    {
        if (reset == null) return "";
        var time = reset.Value - now;
        if (time <= TimeSpan.Zero) return "reset due";
        string duration = time.Days > 0 ? $"{time.Days}d {time.Hours}h" :
            time.Hours > 0 ? $"{time.Hours}h {time.Minutes}m" : $"{Math.Ceiling(time.TotalMinutes)}m";
        return duration;
    }

    private async void Close()
    {
        if (closing) return;
        closing = true;
        lifetime.Cancel();
        await Task.WhenAll(pending.Values.Append(loginTask).Append(updateTask));
        if (http != null) http.Dispose();
        else handler?.Dispose();
        lifetime.Dispose();
        DestroyWindow(hwnd);
    }
}
