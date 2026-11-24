using System.Runtime.InteropServices;
using Xunit;
using static QuotaGadget.Native;

namespace QuotaGadget.Tests;

public partial class WindowTests
{
    [Theory]
    [InlineData(96)]
    [InlineData(120)]
    [InlineData(144)]
    [InlineData(192)]
    public void GadgetFrameAndFiguresMatchTheExpectedPixels(int dpi)
    {
        const uint background = 0x202020;
        int Px(int dip) => (int)Math.Round(dip * dpi / 96d);
        Rect Box(int x, int y, int w, int h) => new(Px(x), Px(y), Px(x + w), Px(y + h));
        var account = new Account(Provider.Codex, "fixture");
        var card = new Card(account, "alex.g@example.test", null, null,
            [new("5h", Single: new("5h", 19, "1d 7h", "", null)),
             new("Wk", Single: new("Week", 13, "", "", null)),
             new("Day", Single: new("Day", 100, "", "", null))], false);
        var layout = new Layout([card], null, false, 600);
        var area = Box(0, 0, layout.WindowWidth, layout.Height);
        using var painter = new Painter();

        var frame = Enumerable.Repeat(background, area.Width * area.Height).ToArray();
        Painter.ApplyFrame(frame, area.Width, area.Height, 8 * dpi / 96d);
        Assert.Equal(0u, frame[0]);
        Assert.Equal(0u, frame[area.Width - 1]);
        Assert.Equal(0u, frame[^area.Width]);
        Assert.Equal(0u, frame[^1]);
        Assert.Equal(0xff3d3d3du, frame[area.Width / 2]);
        Assert.Equal(0xec1e1e1eu, frame[area.Width + area.Width / 2]);
        Assert.Contains(frame.Take(Px(8)), pixel => pixel >> 24 is > 0 and < 255);
        Assert.All(frame.Take(Px(8)), pixel => Assert.True((pixel & 255) <= pixel >> 24));

        WithPaintSurface(area, dc =>
        {
            SetDCBrushColor(dc, background);
            FillRect(dc, area, GetStockObject(18));
            var corner = new Rect(0, 0, Px(1), Px(1));
            SetDCBrushColor(dc, 0x604020);
            FillRect(dc, corner, GetStockObject(18));
            painter.Draw(0, dc, area, dpi, layout, 0, "18:21", new(Part.Surface), Part.Surface, 0);
            Assert.Equal(0x604020u, GetPixel(dc, 0, 0));

            int row = Layout.HeaderBottom + Card.HeaderHeight;
            CheckNaturalText(dc, Box(64, 6, 60, 28), dpi, "18:21", 11, 400, 0xa0a0a0, background);
            CheckNaturalText(dc, Box(176, row, 32, 16), dpi, "19%", 11, 400, 0xf2f2f2, background, 2);
            CheckNaturalText(dc, Box(176, row + 16, 32, 16), dpi, "13%", 11, 400, 0xf2f2f2, background, 2);
            CheckNaturalText(dc, Box(176, row + 32, 32, 16), dpi, "100%", 11, 400, 0xf2f2f2, background, 2);
            CheckNaturalText(dc, Box(208, row, Layout.Right - 208, 16), dpi, "1d 7h", 11, 400, 0xa0a0a0, background, 2);
        });

        // Even the minimum compact cell has room for all four glyphs.
        Assert.InRange(painter.Measure("100%", dpi, 10, 600), 1, 27);
        Assert.InRange(painter.Measure("Quota", dpi, 16, 600), 1, 54);
    }

    [Theory]
    [InlineData(96)]
    [InlineData(120)]
    [InlineData(144)]
    [InlineData(192)]
    public void TrayTextFitsAndPopupBorderUsesActualClientPixels(int dpi)
    {
        const int width = PopupWindow.WideTrayWidth;
        const uint border = 0x3d3d3d, background = 0x2c2c2c;
        int Px(int dip) => (int)Math.Round(dip * dpi / 96d);
        Rect Box(int x, int y, int w, int h) => new(Px(x), Px(y), Px(x + w), Px(y + h));
        Assert.Equal(40, PopupWindow.TrayRowHeight);
        var client = new Rect(0, 0, Px(width) - 1, Px(96) - 1);
        var now = DateTimeOffset.Now;
        var ag = new Account(Provider.Antigravity, "");
        var codex = new Account(Provider.Codex, "fixture");
        var gravity = new Card(ag, "alex.g@example.com", null, null,
            QuotaRow.Build(ag.Provider, [new("Claude/GPT Week", 0, now.AddDays(2))], now), false);
        var regular = new Card(codex, "alex.g@example.test", null, null,
            QuotaRow.Build(codex.Provider, [new("Week", 0, now.AddDays(2))], now), false);
        using var painter = new Painter();
        Assert.InRange(painter.Measure("Resets Thu 14:30", dpi), 1, 108);
        Assert.InRange(painter.Measure(regular.Label, dpi, 12, 600), 1, PopupWindow.TrayWidth - 122);

        WithPaintSurface(client, dc =>
        {
            painter.BeginPopup(dc, dpi, client);
            try
            {
                painter.TrayRow(gravity, 8, width);
                painter.TrayRow(regular, 8 + PopupWindow.TrayRowHeight, width);
            }
            finally { painter.EndPopup(); }
            for (int x = 0; x < client.Right; x++)
            {
                Assert.Equal(border, GetPixel(dc, x, 0));
                Assert.Equal(border, GetPixel(dc, x, client.Bottom - 1));
            }
            for (int y = 0; y < client.Bottom; y++)
            {
                Assert.Equal(border, GetPixel(dc, 0, y));
                Assert.Equal(border, GetPixel(dc, client.Right - 1, y));
            }
            Assert.Equal(background, GetPixel(dc, client.Right - 2, client.Bottom - 2));

            CheckNaturalText(dc, Box(34, 10, 124, 18), dpi, "Antigravity", 12, 600, 0xf2f2f2, background);
            CheckNaturalText(dc, Box(34, 28, 124, 18), dpi, gravity.Label, 12, 400, 0xa0a0a0, background);
            CheckNaturalText(dc, Box(width - 122, 10, 112, 18), dpi, "Claude/GPT 100%", 12, 600, 0xb7d880, background);
            CheckNaturalText(dc, Box(width - 84, 50, 74, 18), dpi, "100% left", 12, 600, 0xb7d880, background, 2);
        });
    }

    [Theory]
    [InlineData("tooltip")]
    [InlineData("collapsed")]
    [InlineData("tray")]
    public void AntigravityQaBothPoolsReachEverySummary(string surface) => OnDispatcher(() =>
    {
        using var fixture = new TempDirectory("QuotaGadget dual-pool QA ");
        var configPath = fixture.PathFor("accounts.json");
        var account = new Account(Provider.Antigravity, "");
        var now = DateTimeOffset.Now;
        var entry = new CachedQuota(account,
            [new("Gemini 5h", 90, now.AddHours(2)), new("Gemini Week", 0, now.AddDays(3)),
             new("Claude/GPT 5h", 95, now.AddHours(3)), new("Claude/GPT Week", 65, now.AddDays(4))],
            "Pro", "antigravity@example.test", now);
        QuotaCache.Save(fixture.PathFor("cache.json"), [entry]);
        var window = new MainWindow(new Config { Accounts = [account] }, configPath);
        window.Show();
        try
        {
            Click(window, Layout.Width - Layout.Margin - 70, 20);
            Assert.True(window.Layout.Collapsed);
            Mouse(window, 0x200, 20, 16);
            if (surface == "tooltip")
            {
                SendMessageW(window.Handle, 0x2a1, 0, 0);
                var tip = GetWindow(window.Handle, 6);
                Assert.NotEqual(window.Handle, tip);
                Assert.NotEqual(0, IsWindowVisible(tip));
                Assert.Equal("antigravity@example.test\nGemini 5h 10% · Wk 100%\nClaude/GPT 5h 5% · Wk 35%",
                    window.Layout.HitTest(20, 16, 0).Tip);
            }
            else if (surface == "collapsed")
            {
                // Sample only the numeric text. The provider icon has its own colors.
                AssertBothPoolColors(ReadPixels(window, Layout.Margin + 18, 4, window.Layout.SummaryWidth - 18, 28));
            }
            else
            {
                SendMessageW(window.Handle, MainWindow.TrayCallback, 200u | (400u << 16), 0x10406);
                var tray = GetWindow(window.Handle, 6);
                Assert.NotEqual(window.Handle, tray);
                Assert.NotEqual(0, IsWindowVisible(tray));
                GetClientRect(tray, out var bounds);
                int width = (int)(bounds.Width * 96d / GetDpiForWindow(tray));
                AssertBothPoolColors(ReadPixels(tray, 34, 8, width - 44, 16));
            }
        }
        finally { CloseWindow(window); }
    });

    [Fact]
    public void PoolSummariesPairIndependentWeeklyQuotasWithTheirResets()
    {
        var now = DateTimeOffset.UtcNow;
        var account = new Account(Provider.Antigravity, "");
        var rows = QuotaRow.Build(account.Provider, [new("Claude/GPT Week", 20, now.AddDays(5)),
            new("Gemini Week", 40, now.AddDays(2)), new("Gemini 5h", 95, now.AddHours(1)),
            new("Claude/GPT 5h", 90, now.AddHours(3))], now);
        var card = new Card(account, "fixture", null, null, rows, false);
        Assert.Equal("fixture\nGemini 5h 5% · Wk 60%\nClaude/GPT 5h 10% · Wk 80%", card.SummaryTip);
        Assert.Equal("60%", card.Gemini!.Summary!.Percent);
        Assert.Equal("80%", card.ClaudeGpt!.Summary!.Percent);
        Assert.Equal(now.AddDays(2), Painter.TrayReset(card.Gemini.Summary, card.Gemini.Bars, now));
        Assert.Equal(now.AddDays(5), Painter.TrayReset(card.ClaudeGpt.Summary, card.ClaudeGpt.Bars, now));

        var fiveHoursOnly = card with { Rows = QuotaRow.Build(account.Provider,
            [new("Gemini 5h", 95, now.AddHours(1)), new("Claude/GPT 5h", 90, now.AddHours(3))], now) };
        Assert.Equal("5%", fiveHoursOnly.Gemini!.Summary!.Percent);
        Assert.Equal("10%", fiveHoursOnly.ClaudeGpt!.Summary!.Percent);
        Assert.Equal(now.AddHours(1), Painter.TrayReset(fiveHoursOnly.Gemini.Summary, fiveHoursOnly.Gemini.Bars, now));
        Assert.Equal(now.AddHours(3), Painter.TrayReset(fiveHoursOnly.ClaudeGpt.Summary, fiveHoursOnly.ClaudeGpt.Bars, now));

        var codex = new Card(new(Provider.Codex, "fixture"), "next", null, null, [], false);
        var layout = new Layout([card, codex], null, false, 600, collapsed: true, maxWidth: 160);
        int right = layout.ButtonX(Part.Refresh);
        int boundary = Layout.Margin + layout.SummaryCell(1).Left - layout.MaxScroll;
        Assert.True(layout.MaxScroll > 0);
        Assert.Equal(0, layout.HitTest(boundary - 1, 16, layout.MaxScroll).Card);
        Assert.Equal(1, layout.HitTest(boundary, 16, layout.MaxScroll).Card);
        Assert.Equal(1, layout.HitTest(right - 1, 16, layout.MaxScroll).Card);
        Assert.Equal(Part.Refresh, layout.HitTest(right, 16, layout.MaxScroll).Part);
    }

    [Fact]
    public void CompactPoolWidthFitsBothFullPercentagesAtTheMonitorEdge() => OnDispatcher(() =>
    {
        using var fixture = new TempDirectory("QuotaGadget compact pool width ");
        var ag = new Account(Provider.Antigravity, "");
        var accounts = new List<Account> { ag, new(Provider.Codex, "fixture-a"),
            new(Provider.Codex, "fixture-b"), new(Provider.Claude, "fixture-c") };
        var now = DateTimeOffset.UtcNow;
        QuotaCache.Save(fixture.PathFor("cache.json"),
            [new(ag, [new("Gemini Week", 0, now.AddDays(1)), new("Claude/GPT Week", 0, now.AddDays(2))],
                null, "fixture", now)]);
        var window = new MainWindow(new Config { Accounts = accounts }, fixture.PathFor("accounts.json"));
        try
        {
            window.Show();
            var hwnd = window.Handle;
            var dpi = GetDpiForWindow(hwnd);
            int Px(int dip) => (int)Math.Round(dip * dpi / 96d);
            var monitor = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            Assert.NotEqual(0, GetMonitorInfoW(MonitorFromWindow(hwnd, 2), ref monitor));
            SetWindowPos(hwnd, 0, monitor.Work.Right - Px(Layout.Width), monitor.Work.Top + Px(20), 0, 0, 0x15);
            Click(window, window.Layout.ButtonX(Part.Collapse) + 14, 15);
            var layout = window.Layout;
            Assert.True(layout.Collapsed);
            GetWindowRect(hwnd, out var bounds);
            Assert.True(bounds.Right <= monitor.Work.Right);
            int cellWidth = layout.SummaryCell(0).Width;
            Assert.InRange(cellWidth, 1, layout.SummaryViewport);
            Assert.Equal(accounts.Count - 1, layout.HitTest(layout.ButtonX(Part.Refresh) - 1, 15, layout.MaxScroll).Card);

            // Both full labels fit with a visible gap.
            using var painter = new Painter();
            foreach (int scale in new[] { (int)dpi, 96, 101, 108, 120, 144, 168, 192, 240, 384 })
            {
                int gemini = painter.Measure("G 100%", scale, 12, 600);
                int claude = painter.Measure("C 100%", scale, 12, 600);
                Assert.True(gemini + 2 <= Layout.PoolColumnWidth, $"Gemini label needs {gemini} DIPs at {scale} DPI");
                Assert.True(Layout.PoolTextOffset + Layout.PoolColumnWidth + claude + 2 <= cellWidth,
                    $"Claude/GPT label needs {claude} DIPs at {scale} DPI");
            }
        }
        finally { CloseWindow(window); }
    });

    [Fact]
    public void ExpandedBareClaudeGptPoolKeepsItsFullLabelAndSingleBar() => OnDispatcher(() =>
    {
        using var painter = new Painter();
        Assert.Equal(3, Layout.FiveX - Layout.Margin - Layout.PoolNameWidth);
        foreach (int dpi in new[] { 96, 120, 144, 192 })
            Assert.InRange(painter.Measure("Claude/GPT", dpi, 11), 1, Layout.PoolNameWidth);

        using var fixture = new TempDirectory("QuotaGadget expanded pool label ");
        var account = new Account(Provider.Antigravity, "");
        var window = new MainWindow(new Config { Accounts = [account] }, fixture.PathFor("accounts.json"));
        try
        {
            window.Show();
            window.DisplayQuota(account, new([new("Claude/GPT Week", 0, null)]));
            int y = Layout.HeaderBottom + Card.HeaderHeight;
            uint[] LabelPixels() => ReadPixels(window, Layout.Margin, y, Layout.FiveX - Layout.Margin, 16);
            var groupedLabel = LabelPixels();
            Assert.Contains(groupedLabel, pixel => pixel != 0x202020);

            window.DisplayQuota(account, new([new("Claude/GPT", 0, null)]));
            const uint mint = 0xb7d880;
            Assert.NotEqual(mint, ReadPixels(window, Layout.FiveX - 6, y + 8, 1, 1)[0]);
            Assert.Equal(mint, ReadPixels(window, Layout.FiveX + 2, y + 8, 1, 1)[0]);
            Assert.Equal(groupedLabel, LabelPixels());
        }
        finally { CloseWindow(window); }
    });

    private static void AssertBothPoolColors(uint[] pixels)
    {
        int amber = pixels.Count(p => (p & 0xffffff) == 0x71bbed);
        int green = pixels.Count(p => (p & 0xffffff) == 0xb7d880);
        Assert.True(amber > 0 && green > 0, $"Expected amber Claude/GPT 35% and green Gemini 100% text; amber pixels={amber}, green pixels={green}.");
    }

    private static void CheckNaturalText(nint dc, Rect box, int dpi, string text, int size, int weight,
        uint color, uint background, uint align = 0)
    {
        uint[] Pixels() => Enumerable.Range(box.Top, box.Height)
            .SelectMany(y => Enumerable.Range(box.Left, box.Width).Select(x => GetPixel(dc, x, y))).ToArray();
        var actual = Pixels();
        var font = CreateFontW(-(int)Math.Round(size * dpi / 96d), 0, 0, 0, weight, 0, 0, 0, 1, 0, 0, 4, 0, "Segoe UI Variable Text");
        var original = SelectObject(dc, font);
        try
        {
            GetTextExtentPoint32W(dc, text, text.Length, out var ink);
            Assert.InRange(ink.X, 1, box.Width);
            Assert.InRange(ink.Y, 1, box.Height);
            SetDCBrushColor(dc, background);
            FillRect(dc, box, GetStockObject(18));
            SetBkMode(dc, 1);
            SetTextColor(dc, color);
            var reference = box;
            DrawTextW(dc, text, -1, ref reference, 0x824 | align);
            var expected = Pixels();
            Assert.Contains(expected, pixel => pixel != background);
            Assert.Equal(expected, actual);
        }
        finally
        {
            SelectObject(dc, original);
            DeleteObject(font);
        }
    }
}
