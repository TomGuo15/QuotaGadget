using System.Net;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Xunit;
using static QuotaGadget.Native;

namespace QuotaGadget.Tests;

[Collection("CLI path")]
public partial class WindowTests
{
    [Fact]
    public void DesktopClaudeLabelReflectsDefaultHistoryAndProfileMetadata()
    {
        using var dir = new TempDirectory();
        var profile = dir.CreateSubdirectory(".claude");
        var account = new Account(Provider.Claude, profile.FullName);
        var config = new Config { Accounts = [account] };
        var window = new MainWindow(config, dir.PathFor("accounts.json"));
        var desktop = dir.CreateSubdirectory("Claude");
        File.WriteAllText(Path.Combine(desktop.FullName, "plan-usage-history.json"), "{}");
        void RefreshIdentity() => window.SetIdentity(account, AccountIdentity.Read(account, _ => profile.FullName,
            key => key == "APPDATA" ? dir.FullName : null));
        RefreshIdentity();
        Assert.Equal("Claude Desktop", Assert.Single(window.Layout.Cards).Label);
        File.WriteAllText(dir.PathFor(".claude.json"), """{"oauthAccount":{"emailAddress":"fixture@example.test"}}""");
        RefreshIdentity();
        Assert.Equal("fixture@example.test", Assert.Single(window.Layout.Cards).Label);
    }

    [Fact]
    public void UnavailableIdeKeepsTheLastPoolReadingAndIdentityDimmed()
    {
        using var dir = new TempDirectory();
        var account = new Account(Provider.Antigravity, "");
        var window = new MainWindow(new Config { Accounts = [account] }, dir.PathFor("accounts.json"));
        var quota = new AccountQuota([new("Gemini", 75, null)], Tier: "Pro", Email: "ide@example.test");
        window.DisplayQuota(account, quota);
        var card = Assert.Single(window.Layout.Cards);
        Assert.Equal("ide@example.test", card.Label);
        Assert.Equal("Pro", card.Tier);
        Assert.Equal("25%", card.Gemini?.Summary?.Percent);
        Assert.Contains("Gemini 25%", card.SummaryTip);
        Assert.False(card.Stale);
        window.DisplayQuota(account, AccountQuota.Fail(AntigravityDesktop.Unavailable));
        card = Assert.Single(window.Layout.Cards);
        Assert.True(card.Stale);
        Assert.Equal("25%", card.Gemini?.Summary?.Percent);
        Assert.Equal("ide@example.test", card.Label);
        Assert.Equal(AntigravityDesktop.Unavailable, card.Error);
    }

    [Fact]
    public void FirstLaunchCreatesCliWorkingDirectoryBeforeAddingAnAccount()
    {
        using var fixture = new TempDirectory("QuotaGadget first launch ");
        var home = fixture.PathFor("QuotaGadget");
        var window = new MainWindow(configPath: Path.Combine(home, "accounts.json"));
        Assert.Empty(window.Layout.Cards);
        Assert.True(Directory.Exists(home));
        Assert.Empty(Directory.EnumerateFileSystemEntries(home));
    }

    [Fact]
    public void FirstLaunchSavesDiscoveredAccountsAndReopenKeepsThemRemoved()
    {
        using var fixture = new TempDirectory("QuotaGadget first launch ");
        string path = fixture.PathFor("accounts.json");
        var codex = new Account(Provider.Codex, Config.DefaultDir(Provider.Codex));
        var antigravity = new Account(Provider.Antigravity, "");
        int discoveries = 0;
        List<Account> Discover() { discoveries++; return [codex, antigravity]; }
        var window = new MainWindow(configPath: path, discoverDefaults: Discover);
        Assert.Equal(new[] { codex, antigravity }, window.Layout.Cards.Select(card => card.Account));
        Assert.True(File.Exists(path));
        Assert.Equal(new[] { codex, antigravity }, Config.Load(path).Accounts);

        Remove(window, Provider.Codex);
        window = new MainWindow(configPath: path, discoverDefaults: Discover);
        Assert.Equal(antigravity, Assert.Single(window.Layout.Cards).Account);

        Remove(window, Provider.Antigravity);
        Assert.True(File.Exists(path));
        Assert.Empty(Config.Load(path).Accounts);
        window = new MainWindow(configPath: path, discoverDefaults: Discover);
        Assert.Empty(window.Layout.Cards);
        Assert.Equal(1, discoveries);

        static void Remove(MainWindow window, Provider provider) => window.Menu(-1)
            .Single(item => item.Label == "Remove account").Children!.Single(item => item.Icon == provider).Invoke();
    }

    [Fact]
    public void FirstLaunchWithNoLoginsLeavesDiscoveryAvailableAfterScheduledCliCheck() => OnDispatcher(() =>
    {
        using var fixture = new TempDirectory("QuotaGadget first launch ");
        string path = fixture.PathFor("accounts.json");
        int checks = 0;
        var window = new MainWindow(configPath: path, discoverDefaults: () => [],
            updateClis: _ => { checks++; return Task.FromResult(new CliUpdateResult(false, false, "CLIs up to date")); });
        try
        {
            Assert.Empty(window.Layout.Cards);
            Assert.False(File.Exists(path));
            window.Show();
            SendMessageW(window.Handle, 0x113, 5, 0);
            Assert.Equal(1, checks);
            Assert.False(File.Exists(path));

            var account = new Account(Provider.Antigravity, "");
            var reopened = new MainWindow(configPath: path, discoverDefaults: () => [account]);
            Assert.Equal(account, Assert.Single(reopened.Layout.Cards).Account);
            Assert.Equal(account, Assert.Single(Config.Load(path).Accounts));
        }
        finally { CloseWindow(window); }
    });

    [Fact]
    public void LeavingWindowClearsFirstCardHover() => OnDispatcher(() =>
    {
        var account = new Account(Provider.Codex, "fixture");
        using var dir = new TempDirectory("QuotaGadget hover ");
        var window = new MainWindow(new Config { Accounts = [account] }, dir.PathFor("accounts.json"));
        uint Background() => ReadPixels(window, 250, 42, 1, 1)[0];

        window.Show();
        try
        {
            uint initial = Background();
            Mouse(window, 0x200, 70, 42);
            uint hovered = Background();
            SendMessageW(window.Handle, 0x2a3, 0, 0); // WM_MOUSELEAVE
            Assert.Equal(new uint[] { 0x202020, 0x333333, 0x202020 }, new[] { initial, hovered, Background() });
        }
        finally { CloseWindow(window); }
    });

    [Fact]
    public void LayoutHitsGroupedBarsAndClipsScrolledCards()
    {
        var now = DateTimeOffset.UtcNow;
        var five = now.AddHours(4);
        var week = now.AddDays(3);
        var a = new Account(Provider.Antigravity, "");
        var rows = QuotaRow.Build(a.Provider, [new("Gemini 5h", 0, five), new("Gemini Week", 91, week),
            new("Claude/GPT Week", 45, week)], now);
        var card = new Card(a, "Fixture", "Pro", null, rows, false);
        var layout = new Layout([card, card, card, card], "Waiting for sign-in…", true, 150);
        Assert.Equal(2, rows.Count);
        Assert.Equal("100%", rows[0].FiveHour!.Percent);
        Assert.Equal("9%", rows[0].Week!.Percent);
        int y = Layout.HeaderBottom + card.RowsOffset;
        Assert.Contains(five.LocalDateTime.ToString("ddd HH:mm"), layout.HitTest(Layout.FiveX, y, 0).Tip);
        Assert.Contains(week.LocalDateTime.ToString("ddd HH:mm"), layout.HitTest(Layout.WeekX, y, 0).Tip);
        Assert.Null(layout.HitTest(Layout.FiveX, y + 16, 0).Tip);
        Assert.Equal(1, layout.HitTest(40, Layout.HeaderBottom, card.Height).Card);
        Assert.Equal(Part.Cancel, layout.HitTest(245, layout.BodyBottom + 8, layout.MaxScroll).Part);
        Assert.Equal(Part.Refresh, layout.HitTest(Layout.Right - 48, 14, 0).Part);
        Assert.Equal(Part.Menu, layout.HitTest(Layout.Right - 24, 14, 0).Part);
        Assert.Equal(0xedbb71, Painter.BarColor(20));
        Assert.Equal(0xf78d8f, Painter.BarColor(19.9));
        Assert.Equal(0x80d8b7, Painter.BarColor(50));
    }

    [Fact]
    public void CompactRowAndResetBadgeSharePaintAndHitGeometry()
    {
        var now = DateTimeOffset.UtcNow;
        var a = new Account(Provider.Codex, "a");
        var ag = new Account(Provider.Antigravity, "");
        var codex = new Card(a, "fixture@example.test", "Pro", null, QuotaRow.Build(a.Provider,
            [new("5h", 78, now), new("Week", 34, now)], now), false, new(2, [now.AddDays(7)]));
        var other = new Card(ag, "second", null, null, QuotaRow.Build(ag.Provider,
            [new("Gemini Week", 97, now), new("Claude/GPT Week", 15, now)], now), false);
        var expanded = new Layout([codex, other], null, false, 600);
        Assert.Contains("2 resets\n", expanded.HitTest(Layout.Right - 5, Layout.HeaderBottom + 8, 0).Tip);
        Assert.Null(expanded.HitTest(Layout.Right - 5, Layout.HeaderBottom + codex.Height + 8, 0).Tip);
        var row = new Layout([codex, other], null, false, 600, true);
        Assert.Equal(36, row.Height);
        Assert.Equal(0, row.MaxScroll);
        var hit = row.HitTest(Layout.Margin + 6, 15, 0);
        Assert.Equal($"{codex.Label}\nWeek 66% · 5h 22%", hit.Tip);
        Assert.Equal(1, row.HitTest(Layout.Margin + row.SummaryCell(1).Left, 15, 0).Card);
        Assert.Equal(Part.Refresh, row.HitTest(row.WindowWidth - Layout.Margin - 40, 15, 0).Part);
        Assert.Equal(Part.Collapse, row.HitTest(row.WindowWidth - Layout.Margin - 10, 15, 0).Part);
        Assert.Equal("22%", codex.Lowest!.Percent);
        Assert.Equal(now, Painter.TrayReset(codex.Lowest, codex.Bars, now));
    }

    [Fact]
    public void CollapsingAtTheRightEdgeKeepsFiveAccountsAndBothButtonsOnScreen() => OnDispatcher(() =>
    {
        using var dir = new TempDirectory("QuotaGadget compact ");
        var accounts = Enumerable.Range(0, 5).Select(_ =>
            new Account(Provider.Codex, Path.Combine(Config.Profiles, Guid.NewGuid().ToString("N")))).ToList();
        var path = dir.PathFor("accounts.json");
        var window = new MainWindow(new Config { Accounts = accounts }, path);
        try
        {
            window.Show();
            nint hwnd = window.Handle;
            var monitor = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            Assert.NotEqual(0, GetMonitorInfoW(MonitorFromWindow(hwnd, 2), ref monitor));
            int Px(int dip) => (int)Math.Round(dip * GetDpiForWindow(hwnd) / 96d);
            SetWindowPos(hwnd, 0, monitor.Work.Right - Px(Layout.Width + 20), monitor.Work.Top + Px(20), 0, 0, 0x15);
            GetWindowRect(hwnd, out var expanded);
            Click(window, 205, 15);
            GetWindowRect(hwnd, out var row);
            var layout = window.Layout;
            Assert.Equal((expanded.Left, expanded.Top), (row.Left, row.Top));
            Assert.Equal(Px(36), row.Height);
            Assert.True(row.Right <= monitor.Work.Right);
            Assert.True(Config.Load(path).Collapsed);
            Assert.Equal(0, layout.MaxScroll);
            for (int i = 0; i < accounts.Count; i++)
                Assert.Equal(i, layout.HitTest(Layout.Margin + layout.SummaryCell(i).Left, 15, 0).Card);
            Assert.Empty(window.Updated);
            Click(window, layout.ButtonX(Part.Refresh) + 12, 15);
            Assert.NotEmpty(window.Updated);
            Click(window, layout.ButtonX(Part.Collapse) + 12, 15);
            Assert.False(Config.Load(path).Collapsed);
            GetWindowRect(hwnd, out var restored);
            Assert.Equal(expanded, restored);
        }
        finally { CloseWindow(window); }
    });

    [Fact]
    public void ExpandingADraggedRowFitsItsMonitorAndPersistsTheDisplayedPosition() => OnDispatcher(() =>
    {
        using var dir = new TempDirectory("QuotaGadget expand ");
        var path = dir.PathFor("accounts.json");
        var account = new Account(Provider.Codex, Path.Combine(Config.Profiles, Guid.NewGuid().ToString("N")));
        var window = new MainWindow(new Config { Accounts = [account] }, path);
        try
        {
            window.Show();
            nint hwnd = window.Handle;
            var monitor = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            Assert.NotEqual(0, GetMonitorInfoW(MonitorFromWindow(hwnd, 2), ref monitor));
            GetWindowRect(hwnd, out var expanded);
            var work = monitor.Work;
            double scale = GetDpiForSystem() / 96d;
            foreach (var (right, bottom) in new[] { (true, false), (false, true), (true, true), (false, false) })
            {
                Click(window, window.Layout.ButtonX(Part.Collapse) + 12, 15);
                GetWindowRect(hwnd, out var row);
                int left = right ? work.Right - row.Width : work.Left + 20;
                int top = bottom ? work.Bottom - row.Height : work.Top + 20;
                Mouse(window, 0x201, 3, 12);
                double dpiScale = GetDpiForWindow(hwnd) / 96d;
                int x = (int)(3 * dpiScale) + left - row.Left;
                int y = (int)(12 * dpiScale) + top - row.Top;
                SendMessageW(hwnd, 0x200, 1, (ushort)x | (y << 16));
                Mouse(window, 0x202, 3, 12);
                GetWindowRect(hwnd, out var placed);
                Assert.Equal((left, top), (placed.Left, placed.Top));

                Click(window, window.Layout.ButtonX(Part.Collapse) + 12, 15);
                GetWindowRect(hwnd, out var restored);
                Assert.Equal((expanded.Width, expanded.Height), (restored.Width, restored.Height));
                Assert.Equal(right ? work.Right - expanded.Width : left, restored.Left);
                Assert.Equal(bottom ? work.Bottom - expanded.Height : top, restored.Top);
                var saved = Config.Load(path);
                Assert.False(saved.Collapsed);
                Assert.Equal(restored.Left / scale, saved.Left);
                Assert.Equal(restored.Top / scale, saved.Top);
            }
        }
        finally { CloseWindow(window); }
    });

    [Fact]
    public void WheelAndTabReachTheLastCompactAccountWithoutMovingTheControls() => OnDispatcher(() =>
    {
        using var dir = new TempDirectory("QuotaGadget overflow ");
        var accounts = Enumerable.Range(0, 12).Select(_ =>
            new Account(Provider.Codex, Path.Combine(Config.Profiles, Guid.NewGuid().ToString("N")))).ToList();
        QuotaCache.Save(dir.PathFor("cache.json"), accounts.Select((account, i) =>
            new CachedQuota(account, [new("Week", i * 9, null)], null, $"Account {i + 1}", DateTimeOffset.UtcNow)));
        var window = new MainWindow(new Config { Accounts = accounts }, dir.PathFor("accounts.json"));
        try
        {
            window.Show();
            Click(window, 205, 15);
            var row = window.Layout;
            Assert.True(row.MaxScroll > 0);
            int refresh = row.ButtonX(Part.Refresh);
            int cellWidth = row.SummaryCell(accounts.Count - 1).Width;
            uint[] LastCell() => ReadPixels(window, refresh - cellWidth, 7, cellWidth, 20);
            var initial = LastCell();
            SendMessageW(window.Handle, 0x20a, unchecked((uint)(-120 * 20 << 16)), 0);
            var last = LastCell();
            Assert.False(initial.SequenceEqual(last));
            Assert.StartsWith("Account 12\nWeek 1%", row.HitTest(refresh - 1, 15, row.MaxScroll).Tip);
            Assert.Equal(Part.Refresh, row.HitTest(refresh, 15, row.MaxScroll).Part);
            SendMessageW(window.Handle, 0x100, 9, 0);
            Assert.Equal(initial, LastCell());
            for (int i = 1; i < accounts.Count; i++) SendMessageW(window.Handle, 0x100, 9, 0);
            Assert.Equal(last, LastCell());
            Click(window, refresh + 36, 15);
            Assert.False(window.Layout.Collapsed);
        }
        finally { CloseWindow(window); }
    });

    [Fact]
    [Trait("Category", "Interactive")]
    public void WindowMessagesToggleLayoutVisibilityAndRestoreTheShellIcon() => OnDispatcher(() =>
    {
        using var dir = new TempDirectory("QuotaGadget tray ");
        var path = dir.PathFor("accounts.json");
        var window = new MainWindow(configPath: path);
        window.Show();
        var hwnd = window.Handle;
        void TrayClick() => SendMessageW(hwnd, MainWindow.TrayCallback, 0, (nint)(MainWindow.TrayId << 16 | 0x400));
        var icon = new NotifyIconData { Size = (uint)Marshal.SizeOf<NotifyIconData>(), Window = hwnd, Id = MainWindow.TrayId };
        try
        {
            GetWindowRect(hwnd, out var before);
            Click(window, 205, 15);
            GetWindowRect(hwnd, out var after);
            Assert.Equal((before.Left, before.Top), (after.Left, after.Top));
            Assert.True(after.Width < before.Width && after.Height < before.Height);
            Assert.True(Config.Load(path).Collapsed);
            Mouse(window, 0x203, 3, 12);
            Assert.False(Config.Load(path).Collapsed);

            Assert.NotEqual(0, Shell_NotifyIconW(2, ref icon));
            Assert.Equal(0, Shell_NotifyIconW(1, ref icon));
            SendMessageW(hwnd, RegisterWindowMessageW("TaskbarCreated"), 0, 0);
            Assert.NotEqual(0, Shell_NotifyIconW(1, ref icon));
            TrayClick();
            Assert.NotEqual(0, IsWindowVisible(hwnd));
            Assert.Equal(hwnd, GetActiveWindow());
            TrayClick();
            Assert.Equal(0, IsWindowVisible(hwnd));
            Assert.True(Config.Load(path).Hidden);
            TrayClick();
            Assert.NotEqual(0, IsWindowVisible(hwnd));
            window.Menu(-1).Single(m => m.Label == "Hide").Invoke();
            Assert.Equal(0, IsWindowVisible(hwnd));
            window.Menu(-1, tray: true).Single(m => m.Label == "Show gadget").Invoke();
            Assert.False(Config.Load(path).Hidden);
        }
        finally { CloseWindow(window); }
        Assert.Equal(0, Shell_NotifyIconW(1, ref icon));
    });

    [Theory]
    [Trait("Category", "Interactive")]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void ManualLaunchOpensVisibleAndStartupRestoresHidden(bool startup, bool visible) => OnDispatcher(() =>
    {
        using var dir = new TempDirectory("QuotaGadget startup ");
        var path = dir.PathFor("accounts.json");
        new Config { StartWithWindows = true, Hidden = true, Collapsed = true }.Save(path);
        var window = new MainWindow(configPath: path);
        try
        {
            window.Show(startup);
            Assert.Equal(visible, IsWindowVisible(window.Handle) != 0);
            Assert.Equal(!visible, Config.Load(path).Hidden);
            Assert.True(window.Layout.Collapsed);
        }
        finally { CloseWindow(window); }
    });

    [DllImport("user32")]
    private static extern nint GetActiveWindow();

    [Fact]
    public void RefreshAnimatesAndPublishesEachReplyAndStopsAfterAnError() => OnDispatcher(() =>
    {
        using var fixture = new TempDirectory("QuotaGadget refresh ");
        using var first = new OwnedProfile(Provider.Codex);
        using var second = new OwnedProfile(Provider.Codex);
        var accounts = new List<Account> { first.Account, second.Account };
        for (int i = 0; i < accounts.Count; i++)
            File.WriteAllText(Path.Combine(accounts[i].ProfileDir, "auth.json"),
                $$$"""{"tokens":{"access_token":"fixture","account_id":"{{{i}}}"}}""");
        var path = fixture.PathFor("cache.json");
        var saved = new CachedQuota(accounts[1], [new("Week", 66, null)], "Pro", "cached@example.test", DateTimeOffset.UtcNow.AddHours(-1));
        QuotaCache.Save(path, [saved]);
        int requestCount = 0;
        TaskCompletionSource<HttpResponseMessage>[] replies =
            [new(TaskCreationOptions.RunContinuationsAsynchronously), new(TaskCreationOptions.RunContinuationsAsynchronously)];
        var handler = new MockHttpHandler((request, ct) =>
        {
            requestCount++;
            int index = int.Parse(request.Headers.GetValues("ChatGPT-Account-Id").Single());
            return replies[index].Task.WaitAsync(ct);
        });
        var configPath = fixture.PathFor("accounts.json");
        new Config { Accounts = accounts }.Save(configPath);
        var window = new MainWindow(configPath: configPath, handler: handler);
        try
        {
            window.Show();
            PumpUntil(() => requestCount == 2);
            Assert.True(window.Refreshing);
            Assert.Equal(saved.FetchedAt.LocalDateTime.ToString("HH:mm"), window.Updated);
            Assert.True(window.Layout.Cards[1].Stale);
            Assert.Equal(saved.Email, window.Layout.Cards[1].Label);
            Assert.Equal("34%", window.Layout.Cards[1].Rows[0].Single!.Percent);
            var firstFrame = RefreshPixels(window);
            PumpUntil(() => !RefreshPixels(window).SequenceEqual(firstFrame));
            Click(window, 230, 20);
            Assert.Equal(2, requestCount);
            replies[0].SetResult(MockHttpHandler.Json("""{"rate_limit":{"primary_window":{"used_percent":21,"limit_window_seconds":18000}}}"""));
            PumpUntil(() => window.Layout.Cards[0].Rows.FirstOrDefault()?.Single?.Percent == "79%");
            Assert.True(window.Refreshing);
            replies[1].SetResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            PumpUntil(() => !window.Refreshing);
            Assert.Equal("Quota service error (503)", window.Layout.Cards[1].Error);
            Mouse(window, 0x200, 40, 15);
            var idle = RefreshPixels(window);
            Thread.Sleep(110);
            Assert.Equal(idle, RefreshPixels(window));
            Mouse(window, 0x200, 230, 20);
            var hovered = RefreshPixels(window);
            Assert.False(hovered.SequenceEqual(idle));
            Mouse(window, 0x201, 230, 20);
            Assert.False(RefreshPixels(window).SequenceEqual(hovered));
            Mouse(window, 0x202, 40, 15);
            Mouse(window, 0x200, 80, Layout.HeaderBottom + window.Layout.Cards[0].RowsOffset + 6);
            SendMessageW(window.Handle, 0x2a1, 0, 0);
            var tooltip = GetWindow(window.Handle, 6); // GW_ENABLEDPOPUP
            Assert.NotEqual(window.Handle, tooltip);
            Assert.NotEqual(0, IsWindowVisible(tooltip));
            SendMessageW(window.Handle, 0x2a3, 0, 0);
            Assert.Equal(0, IsWindowVisible(tooltip));
            var cache = QuotaCache.Load(path, accounts);
            Assert.Equal(21, Assert.Single(cache[accounts[0]].Windows).UsedPercent);
            Assert.Equal(saved.FetchedAt, cache[accounts[1]].FetchedAt);
            Assert.DoesNotContain("fixture", File.ReadAllText(path));
            Assert.DoesNotContain("token", File.ReadAllText(path));
        }
        finally
        {
            CloseWindow(window);
            PumpUntil(() => window.Handle == 0);
        }
    });

    public enum LoginResult { Added, Duplicate, Cancelled, Failed }

    [Theory]
    [InlineData(LoginResult.Added)]
    [InlineData(LoginResult.Duplicate)]
    [InlineData(LoginResult.Cancelled)]
    [InlineData(LoginResult.Failed)]
    public void BrowserLoginOnlyKeepsCompletedDistinctProfiles(LoginResult result) => OnDispatcher(() =>
    {
        using var fixture = new TempDirectory("QuotaGadget login & shim ");
        using var profile = new OwnedProfile(Provider.Claude);
        var account = profile.Account;
        string? previousPath = Environment.GetEnvironmentVariable("PATH");
        var metadata = profile.PathFor(".claude.json");
        var marker = fixture.PathFor("started.txt");
        string configPath = fixture.PathFor("accounts.json");
        string? created = null;
        try
        {
            File.WriteAllText(metadata, result == LoginResult.Duplicate
                ? """{"oauthAccount":{"emailAddress":"Fixture@example.test"}}"""
                : """{"oauthAccount":{"emailAddress":"other@example.test"}}""");
            fixture.WriteScript("claude.cmd", """
                @echo off
                if not "%*"=="auth login --claudeai" exit /b 1
                >"%~dp0started.txt" echo %CLAUDE_CONFIG_DIR%
                >"%CLAUDE_CONFIG_DIR%\.claude.json" echo {"oauthAccount":{"emailAddress":"fixture@example.test"}}
                """ + (result switch
                {
                    LoginResult.Cancelled => "\r\n\"%SystemRoot%\\System32\\ping.exe\" -n 61 127.0.0.1 >nul",
                    LoginResult.Failed => "\r\nexit /b 1",
                    _ => ""
                }));
            Environment.SetEnvironmentVariable("PATH", fixture.FullName + Path.PathSeparator + previousPath);
            var config = new Config { Accounts = [account] };
            var window = new MainWindow(config, configPath);
            window.Show();
            window.Menu(-1).Single(m => m.Label == "Add account").Children!.Single(m => m.Label == "Claude").Invoke();
            Assert.True(window.Layout.Waiting);
            PumpUntil(() => File.Exists(marker) && new FileInfo(marker).Length > 0);
            created = File.ReadAllText(marker).Trim();
            Assert.True(Config.IsOwned(created));
            Assert.NotEqual(account.ProfileDir, created);
            if (result == LoginResult.Cancelled) Click(window, 244, window.Layout.BodyBottom + 9);
            PumpUntil(() => !window.Layout.Waiting);
            if (result == LoginResult.Added)
            {
                Assert.Equal(config.Accounts, Config.Load(configPath).Accounts);
                Assert.Equal("fixture@example.test", AccountIdentity.Read(config.Accounts[1]).Email);
                Assert.True(Directory.Exists(created));
                Assert.Null(window.Layout.Status);
            }
            else
            {
                string expected = result switch
                {
                    LoginResult.Cancelled => "Sign-in cancelled.",
                    LoginResult.Duplicate => "This account is already shown.",
                    _ => "Claude sign-in failed. Try again."
                };
                Assert.Equal(expected, window.Layout.Status);
                Assert.Equal(account, Assert.Single(config.Accounts));
                Assert.False(File.Exists(configPath));
                Assert.False(Directory.Exists(created));
            }
            Assert.True(File.Exists(metadata));
            CloseWindow(window);
            PumpUntil(() => window.Handle == 0);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", previousPath);
            if (created != null && Config.IsOwned(created) && Directory.Exists(created))
            {
                Config.CheckOwnedPath(created);
                File.Delete(Path.Combine(created, ".claude.json"));
                Directory.Delete(created);
            }
        }
    });

    [Fact]
    public void ClickDoesNotSaveAndMenuAndDragPersistOrder() => OnDispatcher(() =>
    {
        using var dir = new TempDirectory();
        var path = dir.PathFor("accounts.json");
        Account Fixture() => new(Provider.Codex, Path.Combine(Config.Profiles, Guid.NewGuid().ToString("N")));
        var a = Fixture();
        var b = Fixture();
        var window = new MainWindow(new Config { Accounts = [a, b] }, path);
        try
        {
            window.Show();
            Click(window, 40, 15);
            Assert.False(File.Exists(path));
            var menu = window.Menu(0);
            var up = menu.Single(m => m.Label == "Move account").Children![0].Children!.Single(m => m.Label == "Move up");
            Assert.False(up.Enabled);
            up.Invoke();
            Assert.False(File.Exists(path));
            menu.Single(m => m.Label == "Move account").Children![0].Children!.Single(m => m.Label == "Move down").Invoke();
            Assert.Equal(new[] { b, a }, Config.Load(path).Accounts);
            Assert.False(window.Menu(1).Single(m => m.Label == "Move account").Children![1].Children!.Single(m => m.Label == "Move down").Enabled);
            int first = Layout.HeaderBottom + 6, second = first + window.Layout.Cards[0].Height;
            Mouse(window, 0x201, 34, second);
            Mouse(window, 0x200, 34, first);
            Mouse(window, 0x202, 34, first);
            Assert.Equal(new[] { a, b }, Config.Load(path).Accounts);
            GetWindowRect(window.Handle, out var before);
            Mouse(window, 0x201, 40, 15);
            Mouse(window, 0x200, 50, 25);
            Mouse(window, 0x202, 40, 15);
            GetWindowRect(window.Handle, out var after);
            Assert.True(after.Left > before.Left && after.Top > before.Top);
            Assert.NotNull(Config.Load(path).Left);
            var top = window.Menu(0).Single(m => m.Label == "Always on top");
            Assert.True(top.Checked);
            top.Invoke();
            Assert.False(Config.Load(path).AlwaysOnTop);
            Assert.False(window.Menu(0).Single(m => m.Label == "Always on top").Checked);
        }
        finally
        {
            CloseWindow(window);
            PumpUntil(() => window.Handle == 0);
        }
    });

    private static void CloseWindow(MainWindow window)
    {
        if (window.Handle != 0) SendMessageW(window.Handle, 0x10, 0, 0);
    }

    private static void Mouse(MainWindow window, uint msg, int x, int y)
    {
        double scale = GetDpiForWindow(window.Handle) / 96d;
        nint point = (ushort)(int)(x * scale) | ((int)(y * scale) << 16);
        SendMessageW(window.Handle, msg, msg == 0x202 ? 0u : 1u, point);
    }

    private static void Click(MainWindow window, int x, int y)
    {
        Mouse(window, 0x201, x, y);
        Mouse(window, 0x202, x, y);
    }

    [DllImport("user32")]
    private static extern nint GetWindow(nint hwnd, int command);
    [DllImport("user32")]
    private static extern int IsWindowVisible(nint hwnd);
    [DllImport("gdi32")]
    private static extern uint GetPixel(nint dc, int x, int y);
    [DllImport("user32")]
    private static extern nint SendMessageW(nint hwnd, uint msg, nuint wp, nint lp);
    [DllImport("gdi32")]
    private static extern nint CreateCompatibleBitmap(nint dc, int width, int height);

    private static uint[] RefreshPixels(MainWindow window) => ReadPixels(window, 219, 9, 24, 24);

    private static uint[] ReadPixels(MainWindow window, int left, int top, int width, int height) =>
        ReadPixels(window.Handle, left, top, width, height);

    private static uint[] ReadPixels(nint hwnd, int left, int top, int width, int height)
    {
        GetClientRect(hwnd, out var bounds);
        var pixels = new uint[width * height];
        WithPaintSurface(bounds, dc =>
        {
            SetDCBrushColor(dc, 0x202020);
            FillRect(dc, bounds, GetStockObject(18));
            SendMessageW(hwnd, 0x318, (nuint)dc, 4);
            double scale = GetDpiForWindow(hwnd) / 96d;
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    pixels[y * width + x] = GetPixel(dc, (int)((left + x) * scale), (int)((top + y) * scale));
        }, hwnd);
        return pixels;
    }

    private static void WithPaintSurface(Rect area, Action<nint> draw, nint hwnd = 0)
    {
        var screen = GetDC(hwnd);
        var dc = CreateCompatibleDC(screen);
        var bitmap = CreateCompatibleBitmap(screen, area.Width, area.Height);
        var old = SelectObject(dc, bitmap);
        try { draw(dc); }
        finally
        {
            SelectObject(dc, old);
            DeleteObject(bitmap);
            DeleteDC(dc);
            ReleaseDC(hwnd, screen);
        }
    }

    [DllImport("user32")]
    private static extern int PeekMessageW(out Native.Message message, nint hwnd, uint min, uint max, uint remove);

    private static void PumpUntil(Func<bool> done)
    {
        var timeout = System.Diagnostics.Stopwatch.StartNew();
        while (!done() && timeout.Elapsed < TimeSpan.FromSeconds(8))
        {
            while (PeekMessageW(out var message, 0, 0, 0, 1) != 0)
            {
                TranslateMessage(message);
                DispatchMessageW(message);
            }
            Thread.Sleep(5);
        }
        Assert.True(done());
    }

    private static void OnDispatcher(Action test)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                test();
            }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}
