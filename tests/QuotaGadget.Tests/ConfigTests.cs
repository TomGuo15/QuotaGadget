using Xunit;

namespace QuotaGadget.Tests;

public class ConfigTests
{
    [Theory]
    [InlineData(true, false, false, false, nameof(Provider.Codex))]
    [InlineData(false, true, false, false, nameof(Provider.Claude))]
    [InlineData(false, false, true, true, nameof(Provider.Antigravity))]
    [InlineData(false, false, true, false, null)]
    [InlineData(false, false, false, true, null)]
    public void DiscoversEachDefaultLoginIndependently(bool hasCodex, bool hasClaude, bool hasEmail,
        bool hasCli, string? expected)
    {
        using var fixture = new TempDirectory("QuotaGadget discovery ");
        string DefaultDir(Provider provider) => Path.Combine(fixture.FullName, provider.ToString());
        string codex = Directory.CreateDirectory(DefaultDir(Provider.Codex)).FullName;
        string claude = Directory.CreateDirectory(DefaultDir(Provider.Claude)).FullName;
        if (hasCodex) File.WriteAllText(Path.Combine(codex, "auth.json"), "{}");
        if (hasClaude) File.WriteAllText(Path.Combine(claude, ".credentials.json"), "{}");

        var discovered = Config.DiscoverDefaultAccounts(DefaultDir, () => hasEmail ? "fixture@example.test" : null,
            () => hasEmail ? hasCli : throw new InvalidOperationException("CLI probed without credentials."), _ => null);

        if (expected != null)
        {
            var provider = Enum.Parse<Provider>(expected);
            Assert.Equal(new Account(provider, provider == Provider.Antigravity ? "" : DefaultDir(provider)),
                Assert.Single(discovered));
        }
        else Assert.Empty(discovered);
    }

    [Fact]
    public void FirstLaunchFindsDesktopSourcesWithoutCliCredentials()
    {
        using var root = new TempDirectory();
        string Env(string key) => Path.Combine(root.FullName, key);
        string Profile(Provider provider) => Path.Combine(root.FullName, provider.ToString());
        List<Account> Discover() => Config.DiscoverDefaultAccounts(Profile, () => null, () => false, Env);
        Assert.Empty(Discover());
        var claude = Directory.CreateDirectory(Path.Combine(Env("APPDATA"), "Claude"));
        File.WriteAllText(Path.Combine(claude.FullName, "plan-usage-history.json"), "{}");
        var expected = new Account(Provider.Claude, Profile(Provider.Claude));
        Assert.Equal(expected, Assert.Single(Discover()));

        var dirs = new[] { Path.Combine(Env("USERPROFILE"), @".gemini\antigravity\daemon"),
            Path.Combine(Env("LOCALAPPDATA"), @"Antigravity\daemon"), Path.Combine(Env("APPDATA"), @"Antigravity\daemon") };
        var stamp = DateTime.UtcNow;
        for (int i = 0; i < 12; i++)
        {
            string dir = Directory.CreateDirectory(dirs[i % 3]).FullName;
            string file = Path.Combine(dir, $"hub_{i}.json");
            File.WriteAllText(file, "{}");
            File.SetLastWriteTimeUtc(file, stamp.AddMinutes(i));
        }
        File.WriteAllText(Path.Combine(dirs[0], "other.json"), "{}");
        Assert.Equal(Enumerable.Range(2, 10).Reverse().Select(i => $"hub_{i}.json"),
            AntigravityDesktop.HubFiles(Env).Select(f => f.Name));
        Assert.Equal(new[] { expected, new Account(Provider.Antigravity, "") }, Discover());
    }

    [Fact]
    public void QuotaCacheRestoresOnlyConfiguredAccountsWithUsableWindows()
    {
        using var dir = new TempDirectory("QuotaGadget cached quota ");
        var path = dir.PathFor("cache.json");
        var account = new Account(Provider.Antigravity, "");
        var now = DateTimeOffset.UtcNow;
        var entry = new CachedQuota(account, [new("Gemini Week", 19, now.AddDays(2))], "Pro", "fixture@example.test", now);
        QuotaCache.Save(path, [entry, entry with { Account = new(Provider.Codex, "removed") }]);
        var saved = Assert.Single(QuotaCache.Load(path, [account])).Value;
        Assert.Equal(entry.Email, saved.Email);
        Assert.Equal(entry.Tier, saved.Tier);
        Assert.Equal(entry.FetchedAt, saved.FetchedAt);
        Assert.Equal(entry.Windows.Single(), saved.Windows.Single());
        Assert.False(saved.IsStale(now.AddMinutes(15)));
        Assert.True(saved.IsStale(now.AddMinutes(15).AddTicks(1)));
        QuotaCache.Save(path, [entry with { Windows = [new("Week", 120, null)] }]);
        Assert.Empty(QuotaCache.Load(path, [account]));
        File.WriteAllText(path, "[{");
        Assert.Empty(QuotaCache.Load(path, [account]));
    }

    [Fact]
    public void RoundTripKeepsAccountOrderPositionAndToggles()
    {
        using var temp = new TempDirectory("QuotaGadget-");
        var path = temp.PathFor("accounts.json");
        var cfg = Config.Load(path);
        Assert.Empty(cfg.Accounts);
        Assert.True(cfg.AlwaysOnTop);
        cfg.Accounts.Add(new(Provider.Codex, Config.DefaultDir(Provider.Codex)));
        cfg.Accounts.Add(new(Provider.Claude, Path.Combine(Config.Profiles, Guid.NewGuid().ToString("N"))));
        cfg.Accounts.Add(new(Provider.Antigravity, ""));
        cfg.Left = -150;
        cfg.Top = 75.5;
        cfg.AlwaysOnTop = false;
        cfg.Save(path);
        cfg.StartWithWindows = true;
        cfg.AutoUpdateClis = false;
        cfg.LastCliUpdateUtc = DateTimeOffset.UtcNow;
        cfg.Save(path);
        var loaded = Config.Load(path);
        Assert.Equal(cfg.Accounts.ToArray(), loaded.Accounts.ToArray());
        Assert.Equal((-150d, 75.5d, false, true), (loaded.Left, loaded.Top, loaded.AlwaysOnTop, loaded.StartWithWindows));
        Assert.False(loaded.AutoUpdateClis);
        Assert.Equal(cfg.LastCliUpdateUtc, loaded.LastCliUpdateUtc);
        Assert.Contains("\"autoUpdateClis\": false", File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(temp.FullName));
    }

    [Fact]
    public void LoadsLegacyAccountsWithLabels()
    {
        string path = Path.GetTempFileName();
        try
        {
            string dir = Config.DefaultDir(Provider.Codex);
            File.WriteAllText(path, $$"""
                {"accounts":[{"provider":"codex","label":"Default","profileDir":{{System.Text.Json.JsonSerializer.Serialize(dir)}}},
                             {"provider":"antigravity","label":"Current login","profileDir":""}]}
                """);
            var config = Config.Load(path);
            Assert.Equal(new Account[] { new(Provider.Codex, dir), new(Provider.Antigravity, "") }, config.Accounts);
            Assert.True(config.AlwaysOnTop);
            Assert.True(config.AutoUpdateClis);
            Assert.Null(config.LastCliUpdateUtc);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void DefaultLoginAndPathsOutsideProfilesCannotBeOwned()
    {
        Assert.False(Config.IsOwned(Config.DefaultDir(Provider.Codex)));
        Assert.Throws<InvalidDataException>(() => Config.CheckOwnedPath(Config.DefaultDir(Provider.Claude)));
        Assert.False(Config.IsOwned(Path.Combine(Config.Profiles, "..", Guid.NewGuid().ToString("N"))));
    }

    [Theory]
    [InlineData(123, "2h 3m")]
    [InlineData(1601, "1d 2h")]
    [InlineData(-1, "reset due")]
    public void CountdownUsesTheActualResetTime(int minutes, string expected)
    {
        var now = DateTimeOffset.UnixEpoch;
        Assert.Equal(expected, MainWindow.Countdown(now.AddMinutes(minutes), now));
        Assert.Equal("", MainWindow.Countdown(null, now));
    }
}
