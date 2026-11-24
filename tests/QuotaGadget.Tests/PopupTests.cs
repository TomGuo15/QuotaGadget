using System.Runtime.InteropServices;
using Xunit;
using static QuotaGadget.Native;

namespace QuotaGadget.Tests;

public partial class WindowTests
{
    [Fact]
    public void TooltipsUseLocalDatesAndOnlyExpandTruncatedLabels()
    {
        var now = new DateTimeOffset(new DateTime(2026, 12, 1, 8, 0, 0, DateTimeKind.Local));
        var dates = new[] { 2, 13, 14, 15, 16 }.Select(day => now.AddDays(day)).ToArray();
        Assert.Equal("5 resets\nDec 3 · Dec 14 · Dec 15 · +2", new SavedResets(5, dates).Tooltip);
        Assert.Equal("1 reset", new SavedResets(1, []).Tooltip);
        Assert.Equal("15% left · resets Mon 08:00", QuotaBar.From(new("Week", 85, now.AddDays(6)), now).Tip);
        Assert.Equal("15% left · resets Dec 7", QuotaBar.From(new("Week", 85, now.AddDays(6).AddMinutes(1)), now).Tip);
        Assert.Equal("15% left", QuotaBar.From(new("Week", 85, null), now).Tip);

        var account = new Account(Provider.Codex, "fixture");
        var rows = QuotaRow.Build(account.Provider, [new("Day", 80, now.AddDays(-1)),
            new("Week", 85, now.AddDays(3)), new("5h", 12, now.AddHours(1))], now);
        var card = new Card(account, new string('x', 80) + "@example.test", "Pro", "Check your connection", rows, false);
        var layout = new Layout([card], null, false, 600);
        card.TierWidth = 32;
        int y = Layout.HeaderBottom + 8;
        Assert.Null(layout.HitTest(40, y, 0).Tip);
        card.LabelTruncated = true;
        Assert.Equal(card.Label, layout.HitTest(40, y, 0).Tip);
        Assert.Null(layout.HitTest(Layout.Right - 10, y, 0).Tip);
        Assert.Equal("Offline", layout.HitTest(40, y + 19, 0).Tip);
        Assert.Equal("Sign in again", (card with { Error = "Login expired — re-login" }).ErrorTip);
        Assert.Equal(now.AddDays(3), Painter.TrayReset(card.Summary, card.Bars, now));
    }

    [Theory]
    [InlineData("Codex")]
    [InlineData("Claude")]
    public void AccountSummaryPairsWeeklyQuotaWithItsReset(string providerName)
    {
        var provider = Enum.Parse<Provider>(providerName);
        var now = new DateTimeOffset(2026, 12, 1, 8, 0, 0, TimeSpan.Zero);
        var fiveHour = new QuotaWindow("5h", 90, now.AddHours(1));
        var week = new QuotaWindow("Week", 30, now.AddDays(3));
        var card = new Card(new(provider, "fixture"), "fixture", null, null,
            QuotaRow.Build(provider, [fiveHour, week], now), false);
        Assert.Equal("70%", card.Summary!.Percent);
        Assert.Equal(week.ResetsAt, Painter.TrayReset(card.Summary, card.Bars, now));

        var onlyFiveHour = card with { Rows = QuotaRow.Build(provider, [fiveHour], now) };
        Assert.Equal("10%", onlyFiveHour.Summary!.Percent);
        Assert.Equal(fiveHour.ResetsAt, Painter.TrayReset(onlyFiveHour.Summary, onlyFiveHour.Bars, now));

        foreach (DateTimeOffset? unavailable in new DateTimeOffset?[] { null, now.AddMinutes(-1) })
        {
            var missingReset = card with { Rows = QuotaRow.Build(provider, [fiveHour, week with { ResetsAt = unavailable }], now) };
            Assert.Equal("70%", missingReset.Summary!.Percent);
            Assert.Null(Painter.TrayReset(missingReset.Summary, missingReset.Bars, now));
        }
    }

    [Fact]
    public void PopupFitsNegativeMonitorCoordinatesAndOversizedContent()
    {
        var work = new Rect(-1920, -240, 0, 800);
        Assert.Equal(new Rect(-300, 700, 0, 800), PopupWindow.Fit(work, new(-5, 790), 300, 100));
        Assert.Equal(new Rect(-1920, -240, -1620, -140), PopupWindow.Fit(work, new(-1930, -400), 300, 100));
        Assert.Equal(work, PopupWindow.Fit(work, new(-900, 400), 2000, 2000));
    }

    [Fact]
    public void CheckedSubmenuReservesWidthAndClipsLongLabelsBeforeBothMarkers()
    {
        const int width = 184, height = 88, dpi = 96;
        var item = new MenuItem("", Checked: true, Children: [new("Move up")]);
        var longLabel = item with { Label = new string('M', 80) };
        using var painter = new Painter();
        int naturalWidth = painter.MenuWidth(longLabel, dpi);
        Assert.Equal(12, naturalWidth - painter.MenuWidth(longLabel with { Checked = false }, dpi));
        Assert.True(naturalWidth > width);

        var area = new Rect(0, 0, width, height);
        WithPaintSurface(area, dc =>
        {
            painter.BeginPopup(dc, dpi, area);
            try
            {
                painter.MenuRow(item, false, 8, width);
                painter.MenuRow(longLabel, false, 48, width);
            }
            finally { painter.EndPopup(); }

            uint[] RowPixels(int top, int left, int right) => Enumerable.Range(top, 32)
                .SelectMany(y => Enumerable.Range(left, right - left).Select(x => GetPixel(dc, x, y))).ToArray();
            uint background = GetPixel(dc, width - 6, 24);
            Assert.Contains(RowPixels(48, 38, width - 50), pixel => pixel != background);
            Assert.Contains(RowPixels(8, width - 42, width - 26), pixel => pixel != background);
            Assert.Contains(RowPixels(8, width - 26, width - 10), pixel => pixel != background);
            Assert.Equal(RowPixels(8, width - 50, width - 10), RowPixels(48, width - 50, width - 10));
        });
    }

    [Fact]
    public void TrayIconAnchorOpensTowardTheWorkAreaOnEitherTaskbarEdge()
    {
        var work = new Rect(-1920, -120, 0, 960);
        Assert.Equal((new Point(-508, -124), false),
            MainWindow.TrayIconAnchor(new(-520, -148, -496, -124), work));
        Assert.Equal((new Point(-508, 964), true),
            MainWindow.TrayIconAnchor(new(-520, 964, -496, 988), work));
    }

    [Fact]
    [Trait("Category", "Interactive")]
    public void MenuKeysSkipDisabledItemsDispatchOnceAndDismissTheCascade() => OnDispatcher(() =>
    {
        using var dir = new TempDirectory("QuotaGadget menus ");
        var path = dir.PathFor("accounts.json");
        var window = new MainWindow(configPath: path);
        window.Show();
        try
        {
            nint menu = 0;
            void Open()
            {
                SendMessageW(window.Handle, 0x7b, 0, -1);
                menu = GetWindow(window.Handle, 6);
                Assert.NotEqual(window.Handle, menu);
                Assert.NotEqual(0, IsWindowVisible(menu));
            }
            void Key(int key) => SendMessageW(menu, 0x100, (nuint)key, 0);
            Open();
            Key(40); Key(40); Key(39);
            nint sub = GetWindow(menu, 6);
            Assert.NotEqual(menu, sub);
            Assert.NotEqual(0, IsWindowVisible(sub));
            Key(37);
            Assert.Equal(0, IsWindowVisible(sub));
            Key(40); Key(40); // Import, then Always on top (Remove and Move disabled)
            Key(13);
            Assert.False(Config.Load(path).AlwaysOnTop);
            Assert.Equal(0, IsWindowVisible(menu));

            Open();
            Key(38); Key(38); Key(13); // Up wraps to Exit, then Hide
            Assert.True(Config.Load(path).Hidden);
            window.Menu(-1, tray: true).Single(item => item.Label == "Show gadget").Invoke();
            Open();
            SendMessageW(menu, 0x201, 1, -1); // click outside the root
            Assert.Equal(0, IsWindowVisible(menu));
            Open();
            SendMessageW(menu, 0x08, 0, 0); // WM_KILLFOCUS
            Assert.Equal(0, IsWindowVisible(menu));
            Open();
            Key(27);
            Assert.Equal(0, IsWindowVisible(menu));
        }
        finally { CloseWindow(window); }
    });

    [Fact]
    [Trait("Category", "Interactive")]
    public void VersionFourTrayHoverDoesNotActivateOrDismissAMenu() => OnDispatcher(() =>
    {
        using var dir = new TempDirectory("QuotaGadget flyout ");
        var window = new MainWindow(configPath: dir.PathFor("accounts.json"));
        window.Show();
        try
        {
            var hwnd = window.Handle;
            void Notify(int msg, int id = 1, nuint coords = 0) =>
                SendMessageW(hwnd, MainWindow.TrayCallback, coords, (id << 16) | msg);
            var before = GetWindow(hwnd, 6);
            Notify(0x406, 2);
            Assert.Equal(before, GetWindow(hwnd, 6));
            var active = GetActiveWindow();
            Notify(0x406);
            var flyout = GetWindow(hwnd, 6);
            Assert.NotEqual(hwnd, flyout);
            Assert.NotEqual(0, IsWindowVisible(flyout));
            Assert.Equal(active, GetActiveWindow());
            var icon = new IconIdentifier { Size = (uint)Marshal.SizeOf<IconIdentifier>(), Window = hwnd, Id = MainWindow.TrayId };
            var monitor = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (Shell_NotifyIconGetRect(icon, out var bounds) == 0 && (bounds.Width > 0 || bounds.Height > 0))
            {
                Assert.NotEqual(0, GetWindowRect(flyout, out var r));
                Assert.NotEqual(0, GetMonitorInfoW(MonitorFromPoint(new(bounds.Left, bounds.Top), 2), ref monitor));
                Assert.Equal(Math.Clamp(bounds.Left + bounds.Width / 2 - r.Width / 2, monitor.Work.Left,
                    monitor.Work.Right - r.Width), r.Left);
                if (bounds.Top + bounds.Height / 2 >= monitor.Work.Top + monitor.Work.Height / 2)
                    Assert.True(r.Bottom <= bounds.Top);
                else
                    Assert.True(r.Top >= bounds.Bottom);
            }
            Notify(0x407);
            Assert.Equal(0, IsWindowVisible(flyout));
            var hide = window.Menu(-1).Single(item => item.Label == "Hide");
            Assert.Equal("\uED1A", hide.Glyph);
            Assert.Equal("\uED1A", window.Menu(-1, tray: true).Single(item => item.Label == "Hide gadget").Glyph);
            hide.Invoke();
            Assert.Equal(0, IsWindowVisible(hwnd));
            Notify(0x406);
            Assert.NotEqual(0, IsWindowVisible(GetWindow(hwnd, 6)));
            Notify(0x7b);
            var menu = GetWindow(hwnd, 6);
            Notify(0x407);
            Assert.NotEqual(hwnd, menu);
            Assert.NotEqual(0, IsWindowVisible(menu));
            SendMessageW(menu, 0x100, 27, 0);
            Assert.Equal(0, IsWindowVisible(menu));

            var iconData = new NotifyIconData { Size = (uint)Marshal.SizeOf<NotifyIconData>(), Window = hwnd, Id = MainWindow.TrayId };
            Shell_NotifyIconW(2, ref iconData);
            Assert.NotEqual(0, GetMonitorInfoW(MonitorFromWindow(hwnd, 2), ref monitor));
            int x = monitor.Work.Left + monitor.Work.Width / 2;
            int y = monitor.Work.Top + monitor.Work.Height / 2;
            Notify(0x406, coords: (ushort)x | ((nuint)(ushort)y << 16));
            flyout = GetWindow(hwnd, 6);
            Assert.NotEqual(hwnd, flyout);
            Assert.NotEqual(0, IsWindowVisible(flyout));
            Assert.NotEqual(0, GetWindowRect(flyout, out var fallback));
            Assert.Equal(x - fallback.Width / 2, fallback.Left);
            int gap = (int)Math.Round(8 * GetDpiForWindow(flyout) / 96d);
            Assert.InRange(fallback.Bottom, y - gap - 1, y - gap + 1);
        }
        finally { CloseWindow(window); }
    });
}
