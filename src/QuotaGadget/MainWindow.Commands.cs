using Microsoft.Win32;
using static QuotaGadget.Native;

namespace QuotaGadget;

internal sealed record MenuItem(string Label, Action? Command = null, bool Enabled = true, bool Checked = false,
    List<MenuItem>? Children = null, string Glyph = "", Provider? Icon = null, bool Separator = false)
{
    internal void Invoke() { if (Enabled) Command?.Invoke(); }
}

internal sealed partial class MainWindow
{
    internal const uint TrayCallback = 0x8003, TrayId = 1;
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string UpdateClisLabel = "Update CLIs now";
    private readonly string? startupKey;
    private readonly uint taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
    private nint trayIcon;
    private bool trayAdded, hideOnNextTrayClick;

    private readonly Func<CancellationToken, Task<CliUpdateResult>>? updateClis;
    private Task updateTask = Task.CompletedTask;
    private string? cliStatus;
    internal DateTimeOffset NextCliCheckUtc { get; private set; }

    internal List<MenuItem> Menu(int index, bool tray = false)
    {
        List<MenuItem> add = [], import = [], remove = [], move = [];
        foreach (var provider in Enum.GetValues<Provider>())
        {
            bool agy = provider == Provider.Antigravity;
            bool available = loginCancellation == null && (!agy || config.Accounts.All(a => a.Provider != provider));
            add.Add(new(provider.ToString(), () => loginTask = StartLogin(provider), available, Icon: provider));
            var account = new Account(provider, agy ? "" : Config.DefaultDir(provider));
            import.Add(new(provider.ToString(), () => Add(account), available && config.Accounts.All(a =>
                !string.Equals(a.ProfileDir, account.ProfileDir, StringComparison.OrdinalIgnoreCase)) &&
                Config.CanImport(account, Environment.GetEnvironmentVariable), Icon: provider));
        }
        foreach (var account in config.Accounts)
        {
            string label = Header(account);
            remove.Add(new(label, () => Remove(account), Icon: account.Provider));
            int position = move.Count;
            move.Add(new(label, Icon: account.Provider, Checked: position == index, Children: [
                new("Move up", () => MoveAccount(account, config.Accounts.IndexOf(account) - 1), position > 0, Glyph: "\uE74A"),
                new("Move down", () => MoveAccount(account, config.Accounts.IndexOf(account) + 1), position + 1 < config.Accounts.Count, Glyph: "\uE74B")
            ]));
        }
        return [
            new("Refresh now", RefreshAll, !refreshing, Glyph: "\uE72C"),
            new("Add account", Enabled: loginCancellation == null, Children: add, Glyph: "\uE710", Separator: true),
            new("Import existing login", Enabled: loginCancellation == null, Children: import, Glyph: "\uE8B5"),
            new("Remove account", Enabled: remove.Count > 0, Children: remove, Glyph: "\uE738"),
            new("Move account", Enabled: move.Count > 1, Children: move, Glyph: "\uE8CB"),
            new("Always on top", ToggleTop, Checked: config.AlwaysOnTop, Glyph: "\uE718", Separator: true),
            new("Start with Windows", ToggleStartup, startupKey != null, Checked: config.StartWithWindows, Glyph: "\uE7E8"),
            new("Auto-update CLIs", ToggleAutoUpdate, Checked: config.AutoUpdateClis, Glyph: "\uE895"),
            new(UpdateClisLabel, () => StartCliUpdate(manual: true), updateClis != null && updateTask.IsCompleted, Glyph: "\uE896"),
            new(tray ? (config.Hidden ? "Show gadget" : "Hide gadget") : "Hide", () => SetHidden(!config.Hidden),
                trayAdded || config.Hidden, Glyph: config.Hidden ? "\uE890" : "\uED1A", Separator: true),
            new("Exit", Close, Glyph: "\uE8BB")
        ];
    }

    private void OpenMenu(Point screen, bool tray = false, bool above = false)
    {
        floating?.Close();
        floating = new PopupWindow(hwnd, painter, () => floating = null, items: Menu(tray ? -1 : selected, tray));
        floating.Show(screen, above);
    }

    private void ToggleTop()
    {
        config.AlwaysOnTop = !config.AlwaysOnTop;
        if (!Save()) config.AlwaysOnTop = !config.AlwaysOnTop;
        SetWindowPos(hwnd, config.AlwaysOnTop ? -1 : -2, 0, 0, 0, 0, 0x13);
    }

    private void ToggleStartup()
    {
        if (startupKey == null) return;
        bool enabled = !config.StartWithWindows;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(startupKey);
            if (enabled) key.SetValue("QuotaGadget", StartupCommand);
            else key.DeleteValue("QuotaGadget", false);
            config.StartWithWindows = enabled;
            if (!Save())
            {
                config.StartWithWindows = !enabled;
                if (enabled) key.DeleteValue("QuotaGadget", false);
                else key.SetValue("QuotaGadget", StartupCommand);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { Notice("Could not change the Windows startup entry."); }
    }

    private static string StartupCommand => $"\"{Environment.ProcessPath}\" --startup";

    private void MigrateStartup()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(startupKey!, writable: true);
            if (string.Equals(key?.GetValue("QuotaGadget") as string, $"\"{Environment.ProcessPath}\"",
                StringComparison.OrdinalIgnoreCase)) key!.SetValue("QuotaGadget", StartupCommand);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { SetStatus("Could not update the Windows startup entry."); }
    }

    internal static DateTimeOffset FirstCliCheck(DateTimeOffset? last, DateTimeOffset now)
    {
        var first = now.AddMinutes(1);
        if (last == null || last <= first.AddHours(-12)) return first;
        return (last > now ? now : last.Value).AddHours(12);
    }

    private void ToggleAutoUpdate()
    {
        config.AutoUpdateClis = !config.AutoUpdateClis;
        if (!Save()) config.AutoUpdateClis = !config.AutoUpdateClis;
        if (updateTask.IsCompleted) ScheduleCliCheck();
    }

    private void ScheduleCliCheck()
    {
        if (updateClis == null) return;
        KillTimer(hwnd, CliUpdateTimer);
        if (config.AutoUpdateClis)
        {
            var delay = NextCliCheckUtc - DateTimeOffset.UtcNow;
            SetTimer(hwnd, CliUpdateTimer, (uint)Math.Clamp(delay.TotalMilliseconds, 1, TimeSpan.FromHours(12).TotalMilliseconds), 0);
        }
    }

    private void StartCliUpdate(bool manual)
    {
        if (!closing && updateClis != null && updateTask.IsCompleted)
            updateTask = UpdateClis(manual);
    }

    private async Task UpdateClis(bool manual)
    {
        KillTimer(hwnd, CliUpdateTimer);
        floating?.SetCommandEnabled(UpdateClisLabel, false);
        if (manual)
        {
            KillTimer(hwnd, CliStatusTimer);
            cliStatus = "Updating CLIs…";
            Render();
        }
        try
        {
            var result = await updateClis!(lifetime.Token);
            if (closing) return;
            floating?.SetCommandEnabled(UpdateClisLabel, true);
            var now = DateTimeOffset.UtcNow;
            NextCliCheckUtc = now.AddHours(result.AllInUse ? 1 : 12);
            ScheduleCliCheck();
            if (!result.AllInUse)
            {
                config.LastCliUpdateUtc = now;
                // Keep first-launch discovery available until an account or user setting is saved.
                if (File.Exists(configPath)) Save(notify: false);
            }
            if (manual || result.Upgraded)
            {
                cliStatus = result.Message;
                SetTimer(hwnd, CliStatusTimer, 8000, 0);
                Render();
            }
        }
        catch (OperationCanceledException) when (closing) { }
    }

    private unsafe NotifyIconData TrayData()
    {
        var data = new NotifyIconData { Size = (uint)sizeof(NotifyIconData), Window = hwnd, Id = TrayId,
            Flags = 1 | 2 | 4, Callback = TrayCallback, Icon = trayIcon }; // NIF_MESSAGE | NIF_ICON | NIF_TIP
        "QuotaGadget".AsSpan().CopyTo(new Span<char>(data.Tip, 127));
        return data;
    }

    private void AddTrayIcon()
    {
        if (trayIcon == 0) trayIcon = Painter.LoadIcon("Gadget", GetSystemMetrics(49)); // SM_CXSMICON
        var data = TrayData();
        trayAdded = Shell_NotifyIconW(0, ref data) != 0 || Shell_NotifyIconW(1, ref data) != 0;
        if (trayAdded)
        {
            data.Version = 4;
            trayAdded = Shell_NotifyIconW(4, ref data) != 0;
            if (!trayAdded) Shell_NotifyIconW(2, ref data);
        }
    }

    private unsafe (Point Point, bool Above) TrayAnchor(Point reported)
    {
        var id = new IconIdentifier { Size = (uint)sizeof(IconIdentifier), Window = hwnd, Id = TrayId };
        var monitor = new MonitorInfo { Size = sizeof(MonitorInfo) };
        if (Shell_NotifyIconGetRect(id, out var bounds) == 0 && (bounds.Width > 0 || bounds.Height > 0))
        {
            GetMonitorInfoW(MonitorFromPoint(new(bounds.Left, bounds.Top), 2), ref monitor);
            return TrayIconAnchor(bounds, monitor.Work);
        }

        if (reported.X != 0 || reported.Y != 0) return (reported, true);
        if (GetCursorPos(out var cursor) != 0 && (cursor.X != 0 || cursor.Y != 0)) return (cursor, true);
        if (GetMonitorInfoW(MonitorFromPoint(default, 1), ref monitor) == 0)
            return (new Point(Math.Max(1, GetSystemMetrics(0) - 8), Math.Max(1, GetSystemMetrics(1) - 8)), true);
        return (new Point(monitor.Work.Right - 8, monitor.Work.Bottom - 8), true);
    }

    internal static (Point Point, bool Above) TrayIconAnchor(Rect icon, Rect work)
    {
        bool above = icon.Top + icon.Height / 2 >= work.Top + work.Height / 2;
        return (new Point(icon.Left + icon.Width / 2, above ? icon.Top : icon.Bottom), above);
    }

    private void TrayMessage(ushort message, Point reported)
    {
        switch (message)
        {
            case 0x400: // NIN_SELECT
            case 0x401: // NIN_KEYSELECT
                floating?.Close();
                TrayClick();
                break;
            case 0x7b: // WM_CONTEXTMENU has no guaranteed coordinates in v4
                var (menuAnchor, menuAbove) = TrayAnchor(reported);
                OpenMenu(menuAnchor, tray: true, above: menuAbove);
                break;
            case 0x406: // NIN_POPUPOPEN
                if (floating?.IsMenu == true) break;
                floating?.Close();
                floating = new PopupWindow(hwnd, painter, () => floating = null, cards: layout.Cards);
                var (flyoutAnchor, flyoutAbove) = TrayAnchor(reported);
                floating.Show(flyoutAnchor, flyoutAbove);
                break;
            case 0x407: // NIN_POPUPCLOSE
                if (floating?.IsTray == true) floating.Close();
                break;
        }
    }

    private void RemoveTrayIcon()
    {
        if (trayIcon == 0) return;
        var data = TrayData();
        Shell_NotifyIconW(2, ref data);
        DestroyIcon(trayIcon);
        trayIcon = 0;
        trayAdded = false;
    }

    private void TrayClick()
    {
        SetHidden(!config.Hidden && hideOnNextTrayClick);
        hideOnNextTrayClick = !config.Hidden;
    }

    private void SetHidden(bool hidden)
    {
        HideTip();
        ShowWindow(hwnd, hidden ? 0 : 5);
        if (!hidden)
        {
            SetForegroundWindow(hwnd);
            SetFocus(hwnd);
        }
        hideOnNextTrayClick = false;
        if (config.Hidden == hidden) return;
        config.Hidden = hidden;
        Save();
    }
}
