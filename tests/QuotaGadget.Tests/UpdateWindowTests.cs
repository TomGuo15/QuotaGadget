using Microsoft.Win32;
using Xunit;
using static QuotaGadget.Native;

namespace QuotaGadget.Tests;

public partial class WindowTests
{
    [Fact]
    public void FirstCliCheckHonorsLaunchGraceAndLastPass()
    {
        var now = DateTimeOffset.UnixEpoch;
        Assert.Equal(now.AddMinutes(1), MainWindow.FirstCliCheck(null, now));
        Assert.Equal(now.AddMinutes(1), MainWindow.FirstCliCheck(now.AddHours(-13), now));
        Assert.Equal(now.AddHours(4), MainWindow.FirstCliCheck(now.AddHours(-8), now));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void AutomaticCheckStaysQuietUnlessUpgradedAndSchedulesItsNextPass(bool busy, bool upgraded) => OnDispatcher(() =>
    {
        using var dir = new TempDirectory();
        var path = dir.PathFor("accounts.json");
        var reply = new TaskCompletionSource<CliUpdateResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        var window = new MainWindow(configPath: path, updateClis: _ => { calls++; return reply.Task; });
        var begin = DateTimeOffset.UtcNow;
        window.Show();
        try
        {
            void Timer() => SendMessageW(window.Handle, 0x113, 5, 0);
            MenuItem Toggle() => window.Menu(-1).Single(m => m.Label == "Auto-update CLIs");
            MenuItem Update() => window.Menu(-1).Single(m => m.Label == "Update CLIs now");
            Assert.Equal("\uE895", Toggle().Glyph);
            Assert.Equal("\uE896", Update().Glyph);
            Assert.True(Toggle().Checked);
            Toggle().Invoke();
            Assert.False(Config.Load(path).AutoUpdateClis);
            Timer();
            Assert.Equal(0, calls);
            Toggle().Invoke();
            SendMessageW(window.Handle, 0x7b, 0, -1);
            nint menu = GetWindow(window.Handle, 6);
            Assert.NotEqual(window.Handle, menu);
            uint[] CommandPixels() => ReadPixels(menu, 38, 282, 94, 32);
            var enabled = CommandPixels();
            Timer();
            Assert.Null(window.Layout.CliStatus);
            Assert.False(Update().Enabled);
            Assert.False(enabled.SequenceEqual(CommandPixels()));
            Update().Invoke();
            Timer();
            Assert.Equal(1, calls);
            reply.SetResult(new(busy, upgraded, upgraded ? "Updated agy 1.2.3 → 1.2.4" : "No upgrades"));
            PumpUntil(() => Update().Enabled);
            Assert.Equal(enabled, CommandPixels());
            SendMessageW(menu, 0x100, 27, 0);
            Assert.Equal(upgraded ? "Updated agy 1.2.3 → 1.2.4" : null, window.Layout.CliStatus);
            int hours = busy ? 1 : 12;
            Assert.InRange(window.NextCliCheckUtc, begin.AddHours(hours), DateTimeOffset.UtcNow.AddHours(hours));
            var last = Config.Load(path).LastCliUpdateUtc;
            if (busy) Assert.Null(last);
            else Assert.InRange(last!.Value, begin, DateTimeOffset.UtcNow);
            SendMessageW(window.Handle, 0x113, 4, 0);
            Assert.Null(window.Layout.CliStatus);
        }
        finally { CloseWindow(window); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ManualUpdateShowsProgressAndResultEvenWithAutoUpdateOff(bool compact) => OnDispatcher(() =>
    {
        using var dir = new TempDirectory();
        var reply = new TaskCompletionSource<CliUpdateResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var config = new Config { AutoUpdateClis = false, Collapsed = compact };
        var window = new MainWindow(config, dir.PathFor("accounts.json"), updateClis: _ => reply.Task);
        window.Show();
        try
        {
            var initialHeight = window.Layout.Height;
            window.Menu(-1).Single(m => m.Label == "Update CLIs now").Invoke();
            Assert.Equal("Updating CLIs…", window.Layout.CliStatus);
            Assert.True(window.Layout.Height > initialHeight);
            reply.SetResult(new(false, false, "CLIs up to date"));
            PumpUntil(() => window.Layout.CliStatus == "CLIs up to date");
            SendMessageW(window.Handle, 0x113, 4, 0);
            Assert.Null(window.Layout.CliStatus);
            Assert.Equal(initialHeight, window.Layout.Height);
        }
        finally { CloseWindow(window); }
    });

    [Fact]
    public void CompactStatusRestoresTheEdgeOriginUnlessDragged() => OnDispatcher(() =>
    {
        using var dir = new TempDirectory();
        var path = dir.PathFor("accounts.json");
        var config = new Config { Accounts = [new(Provider.Codex, "fixture")], Collapsed = true };
        var window = new MainWindow(config, path, updateClis: _ =>
            Task.FromResult(new CliUpdateResult(false, false, "CLIs up to date")));
        window.Show();
        try
        {
            var hwnd = window.Handle;
            var monitor = new MonitorInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf<MonitorInfo>() };
            Assert.NotEqual(0, GetMonitorInfoW(MonitorFromWindow(hwnd, 2), ref monitor));
            Rect Bounds()
            {
                Assert.NotEqual(0, GetWindowRect(hwnd, out var bounds));
                return bounds;
            }

            var compact = Bounds();
            SetWindowPos(hwnd, 0, monitor.Work.Right - compact.Width, monitor.Work.Bottom - compact.Height, 0, 0, 0x15);
            var edge = Bounds();
            foreach (bool drag in new[] { false, true })
            {
                window.Menu(-1).Single(item => item.Label == "Update CLIs now").Invoke();
                Assert.Equal("CLIs up to date", window.Layout.CliStatus);
                var widened = Bounds();
                Assert.True(widened.Left < edge.Left && widened.Top < edge.Top);
                Assert.Equal(edge.Right, widened.Right);
                Assert.Equal(edge.Bottom, widened.Bottom);
                if (drag)
                {
                    Mouse(window, 0x201, 3, 12);
                    Mouse(window, 0x200, -17, -8);
                    Mouse(window, 0x202, 3, 12);
                    Assert.True(Bounds().Left < widened.Left && Bounds().Top < widened.Top);
                    widened = Bounds();
                }
                SendMessageW(hwnd, 0x113, 4, 0);
                Assert.Null(window.Layout.CliStatus);
                var restored = Bounds();
                Assert.Equal((edge.Width, edge.Height), (restored.Width, restored.Height));
                Assert.Equal(drag ? (widened.Left, widened.Top) : (edge.Left, edge.Top), (restored.Left, restored.Top));
            }
        }
        finally { CloseWindow(window); }
    });

    [Fact]
    public void StartupMigrationAndMenuOnlyWriteTheProvidedRegistryKey() => OnDispatcher(() =>
    {
        using var dir = new TempDirectory();
        string testKey = @"Software\QuotaGadget.Tests-" + Guid.NewGuid().ToString("N");
        var window = new MainWindow(new Config { StartWithWindows = true },
            dir.PathFor("accounts.json"), startupKey: testKey);
        using var key = Registry.CurrentUser.CreateSubKey(testKey);
        try
        {
            key.SetValue("QuotaGadget", $"\"{Environment.ProcessPath}\"");
            window.Show();
            Assert.Equal($"\"{Environment.ProcessPath}\" --startup", key.GetValue("QuotaGadget"));
            window.Menu(-1).Single(m => m.Label == "Start with Windows").Invoke();
            Assert.Null(key.GetValue("QuotaGadget"));
        }
        finally
        {
            CloseWindow(window);
            key.Close();
            Registry.CurrentUser.DeleteSubKey(testKey);
        }
    });
}
