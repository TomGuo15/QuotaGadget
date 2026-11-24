using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using static QuotaGadget.Native;

namespace QuotaGadget;

internal sealed partial class MainWindow
{
    private const string ClassName = "QuotaGadget.Window";
    private const uint DispatchWork = 0x8001, InitialRefresh = 0x8002;
    private const int RefreshTimer = 1, ClockTimer = 2, SpinTimer = 3, CliStatusTimer = 4, CliUpdateTimer = 5;
    private const uint Popup = 0x80000000, ToolWindow = 0x80;
    [ThreadStatic] private static MainWindow? current;
    private readonly Painter painter = new();
    private readonly WindowContext dispatcher = new();
    private nint hwnd;
    private PopupWindow? floating;
    private Point hoverPoint;
    private int dpi = 96, scroll, selected = -1;
    private Hit hover = new(Part.Surface);
    private Hit pressed;
    private bool captured, dragged;
    private bool keyboardSelection;
    private Point start;
    private Rect original;
    private Point? compactStatusOrigin;
    internal nint Handle => hwnd;

    [STAThread]
    private static int Main(string[] args)
    {
        using var instance = new Mutex(true, @"Local\QuotaGadget.Desktop", out bool first);
        if (!first) return 0;
        SetProcessDpiAwarenessContext(-4); // PER_MONITOR_AWARE_V2, before creating any HWND
        try
        {
            var window = new MainWindow();
            window.Show(args.Contains("--startup"));
            while (GetMessageW(out var message, 0, 0, 0) > 0)
            {
                TranslateMessage(message);
                DispatchMessageW(message);
            }
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException)
        {
            MessageBoxW(0, "Could not load accounts.json. Check the file in %APPDATA%\\QuotaGadget and reopen the gadget.", "QuotaGadget", 16);
            return 1;
        }
    }

    private sealed class WindowContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> work = new();
        internal nint Window;
        public override void Post(SendOrPostCallback callback, object? state)
        {
            work.Enqueue((callback, state));
            PostMessageW(Window, DispatchWork, 0, 0);
        }
        internal void Drain()
        {
            while (work.TryDequeue(out var item)) item.Callback(item.State);
        }
    }

    internal unsafe void Show(bool startup = false)
    {
        current = this;
        var module = RegisterWindowClass(ClassName, 8, &WindowProc);
        dpi = (int)GetDpiForSystem();
        int left = config.Left.HasValue ? Px(config.Left.Value) : GetSystemMetrics(0) - Px(Math.Max(Layout.Width, layout.WindowWidth) + 20);
        int top = Px(config.Top ?? 20);
        left = Math.Clamp(left, GetSystemMetrics(76), Math.Max(GetSystemMetrics(76), GetSystemMetrics(76) + GetSystemMetrics(78) - Px(layout.WindowWidth)));
        top = Math.Clamp(top, GetSystemMetrics(77), Math.Max(GetSystemMetrics(77), GetSystemMetrics(77) + GetSystemMetrics(79) - Px(60)));
        hwnd = CreateWindowExW(ToolWindow | 0x80000 | (config.AlwaysOnTop ? 8u : 0u), ClassName, "QuotaGadget",
            Popup, left, top, Px(layout.WindowWidth), Px(layout.Height), 0, 0, module, 0);
        if (hwnd == 0) throw new Win32Exception("Could not create gadget window.");
        dispatcher.Window = hwnd;
        SynchronizationContext.SetSynchronizationContext(dispatcher);
        dpi = (int)GetDpiForWindow(hwnd);
        SetRoundedFrame(hwnd);
        Render();
        AddTrayIcon();
        if (config.StartWithWindows && startupKey != null) MigrateStartup();
        if (!(startup && config.StartWithWindows && config.Hidden && trayAdded))
        {
            ShowWindow(hwnd, 4);
            if (config.Hidden) SetHidden(false);
        }
        UpdateWindow(hwnd);
        SetTimer(hwnd, RefreshTimer, 300000, 0);
        SetTimer(hwnd, ClockTimer, 30000, 0);
        NextCliCheckUtc = FirstCliCheck(config.LastCliUpdateUtc, DateTimeOffset.UtcNow);
        ScheduleCliCheck();
        PostMessageW(hwnd, InitialRefresh, 0, 0);
    }

    private int Px(double logical) => ToPixels(logical, dpi);
    private int Logical(int px) => (int)Math.Floor(FromPixels(px, dpi));
    private void Arrange(List<Card> cards)
    {
        Rect bounds = default;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        bool onMonitor = hwnd != 0 && GetWindowRect(hwnd, out bounds) != 0 &&
            GetMonitorInfoW(MonitorFromWindow(hwnd, 2), ref info) != 0;
        var origin = new Point(bounds.Left, bounds.Top);
        if (!config.Collapsed) compactStatusOrigin = null;
        else if (onMonitor && cliStatus != null && (!layout.Collapsed || layout.CliStatus == null))
            compactStatusOrigin = origin;
        bool restoring = onMonitor && !captured && cliStatus == null && compactStatusOrigin.HasValue;
        if (restoring) origin = compactStatusOrigin!.Value;
        int maxHeight = onMonitor ? Math.Max(120, Logical(info.Work.Height) - 40) : 1000;
        int available = info.Work.Right - origin.X;
        int maxWidth = onMonitor ? Math.Max(0, Logical(available)) : Layout.Width;
        if (onMonitor && Px(maxWidth + 1) <= available) maxWidth++;
        layout = new(cards, status, waiting, maxHeight, config.Collapsed, maxWidth, cliStatus);
        scroll = Math.Clamp(scroll, 0, layout.MaxScroll);
        if (hwnd == 0) return;

        foreach (var card in cards)
        {
            if (card.Tier is { } tier) card.TierWidth = Math.Min(112, painter.Measure(tier, dpi, 11, 600) + 10);
            card.LabelTruncated = painter.Measure(card.Label, dpi, 11, 600) > card.LabelRight - 32;
        }
        int width = Px(layout.WindowWidth), height = Px(layout.Height);
        int left = origin.X, top = origin.Y;
        if (onMonitor && !captured && (!layout.Collapsed || cliStatus != null || restoring))
        {
            left = Math.Clamp(left, info.Work.Left, Math.Max(info.Work.Left, info.Work.Right - width));
            top = Math.Clamp(top, info.Work.Top, Math.Max(info.Work.Top, info.Work.Bottom - height));
        }
        bool moved = left != bounds.Left || top != bounds.Top;
        if (moved || width != bounds.Width || height != bounds.Height)
        {
            if (SetWindowPos(hwnd, 0, left, top, width, height, 0x14) == 0) return;
            if (moved && cliStatus == null && !restoring)
                SavePosition(left, top);
        }
        if (restoring) compactStatusOrigin = null;
        InvalidateRect(hwnd, 0, 0);
    }

    private void SavePosition(int left, int top)
    {
        // Desktop coordinates must use the same scale at save and launch.
        double desktopScale = GetDpiForSystem() / DefaultDpi;
        config.Left = left / desktopScale;
        config.Top = top / desktopScale;
        Save();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint WindowProc(nint window, uint msg, nuint wp, nint lp) =>
        current is { } app && app.hwnd == window ? app.Message(window, msg, wp, lp) : DefWindowProcW(window, msg, wp, lp);

    private nint Message(nint window, uint msg, nuint wp, nint lp)
    {
        var point = new Point((short)lp, (short)(lp >> 16));
        if (taskbarCreated != 0 && msg == taskbarCreated)
        {
            AddTrayIcon();
            return 0;
        }
        switch (msg)
        {
            case TrayCallback when (uint)(lp >> 16) == TrayId:
                TrayMessage((ushort)lp, new Point((short)wp, (short)(wp >> 16)));
                return 0;
            case 0x0f: // WM_PAINT
                var dc = BeginPaint(hwnd, out var ps);
                try { Paint(dc, hwnd); } finally { EndPaint(hwnd, ps); }
                return 0;
            case 0x318: Paint((nint)wp); return 0; // WM_PRINTCLIENT
            case 0x14: return 1; // the buffer covers the client area
            case DispatchWork: dispatcher.Drain(); return 0;
            case InitialRefresh: RefreshAll(); return 0;
            case 0x113: // WM_TIMER
                if (closing) return 0;
                if (wp == RefreshTimer) RefreshAll();
                if (wp == ClockTimer)
                {
                    if (!trayAdded) AddTrayIcon();
                    Render();
                }
                if (wp == CliUpdateTimer && config.AutoUpdateClis) StartCliUpdate(manual: false);
                if (wp == CliStatusTimer)
                {
                    KillTimer(hwnd, CliStatusTimer);
                    cliStatus = null;
                    Render();
                }
                if (wp == SpinTimer) InvalidateRect(hwnd, 0, 0);
                return 0;
            case 0x2e0: // WM_DPICHANGED
                dpi = (int)(wp & 0xffff);
                var suggested = Marshal.PtrToStructure<Rect>(lp);
                SetWindowPos(hwnd, 0, suggested.Left, suggested.Top, Px(layout.WindowWidth), Px(layout.Height), 0x14);
                Render();
                return 0;
            case 0x200: MouseMove(point); return 0;
            case 0x201:
                HideTip();
                keyboardSelection = false;
                pressed = HitAt(point);
                if (pressed.Card >= 0) selected = pressed.Card;
                start = point;
                ClientToScreen(hwnd, ref start);
                GetWindowRect(hwnd, out original);
                captured = true;
                dragged = false;
                SetCapture(hwnd);
                InvalidateRect(hwnd, 0, 0);
                return 0;
            case 0x202: MouseUp(point); return 0;
            case 0x203: // WM_LBUTTONDBLCLK
                if (Logical(point.Y) < Layout.HeaderBottom && HitAt(point).Part is Part.Surface or Part.Row)
                    ToggleCollapsed();
                return 0;
            case 0x215:
                captured = false;
                pressed = default;
                if (cliStatus == null && compactStatusOrigin.HasValue) Render();
                else InvalidateRect(hwnd, 0, 0);
                return 0;
            case 0x2a3: hover = new(Part.Surface); HideTip(); InvalidateRect(hwnd, 0, 0); return 0;
            case 0x2a1: ShowTip(); return 0; // WM_MOUSEHOVER
            case 0x20a:
                scroll = Math.Clamp(scroll - (short)(wp >> 16) * 48 / 120, 0, layout.MaxScroll);
                HideTip();
                ScreenToClient(hwnd, ref point);
                hover = HitAt(point);
                InvalidateRect(hwnd, 0, 0);
                return 0;
            case 0x7b: // WM_CONTEXTMENU, screen coordinates or keyboard
                if (lp == -1) { point = new(0, Px(Layout.HeaderBottom)); ClientToScreen(hwnd, ref point); }
                else
                {
                    var client = point;
                    ScreenToClient(hwnd, ref client);
                    if (HitAt(client).Card is >= 0 and var i) selected = i;
                }
                OpenMenu(point);
                return 0;
            case 0x100 when wp == 9 && config.Accounts.Count > 0:
                selected = (selected + 1) % config.Accounts.Count;
                keyboardSelection = true;
                if (layout.Collapsed)
                {
                    var (left, width) = layout.SummaryCell(selected);
                    int visible = Math.Min(width, layout.SummaryViewport);
                    scroll = Math.Clamp(scroll, left + visible - layout.SummaryViewport, left);
                    HideTip();
                    InvalidateRect(hwnd, 0, 0);
                    return 0;
                }
                int y = layout.Cards.Take(selected).Sum(card => card.Height);
                int bodyHeight = layout.BodyBottom - Layout.HeaderBottom;
                scroll = Math.Clamp(scroll, Math.Max(0, y + Card.HeaderHeight - bodyHeight), y);
                InvalidateRect(hwnd, 0, 0);
                return 0;
            case 0x10: Close(); return 0;
            case 2:
                floating?.Close();
                RemoveTrayIcon();
                painter.Dispose();
                hwnd = 0;
                current = null;
                PostQuitMessage(0);
                return 0;
            default: return DefWindowProcW(window, msg, wp, lp);
        }
    }

    private void Paint(nint dc, nint layer = 0)
    {
        GetClientRect(hwnd, out var area);
        double angle = refreshing ? (Environment.TickCount64 % 900) * Math.Tau / 900 : 0;
        var highlight = keyboardSelection ? new Hit(Part.Header, selected) : hover;
        painter.Draw(layer, dc, area, dpi, layout, scroll, updated, highlight, pressed.Part, angle);
    }
    private Hit HitAt(Point p) => layout.HitTest(Logical(p.X), Logical(p.Y), scroll);

    private void MouseMove(Point p)
    {
        hoverPoint = p;
        var hit = HitAt(p);
        if (hover != hit || keyboardSelection)
        {
            keyboardSelection = false;
            HideTip();
            hover = hit;
            InvalidateRect(hwnd, 0, 0);
        }
        var track = new MouseTrack { Size = Marshal.SizeOf<MouseTrack>(), Flags = 3, Window = hwnd, HoverTime = 350 };
        TrackMouseEvent(ref track);
        if (!captured) return;
        ClientToScreen(hwnd, ref p);
        int dx = p.X - start.X, dy = p.Y - start.Y;
        dragged |= Math.Abs(dx) >= Px(4) || Math.Abs(dy) >= Px(4);
        if (dragged && pressed.Part is Part.Surface or Part.Row
            && SetWindowPos(hwnd, 0, original.Left + dx, original.Top + dy, 0, 0, 0x15) != 0)
            compactStatusOrigin = null;
    }

    private void MouseUp(Point p)
    {
        if (!captured) return;
        var down = pressed;
        var hit = HitAt(p);
        ReleaseCapture();
        if (dragged)
        {
            if (down.Part == Part.Header && hit.Card >= 0 && down.Card < config.Accounts.Count)
                MoveAccount(config.Accounts[down.Card], hit.Card);
            if (down.Part is Part.Surface or Part.Row)
            {
                GetWindowRect(hwnd, out var r);
                if (r.Left != original.Left || r.Top != original.Top)
                {
                    SavePosition(r.Left, r.Top);
                    if (layout.Collapsed) Render();
                }
            }
        }
        else if (hit.Part == down.Part)
        {
            if (hit.Part == Part.Refresh) RefreshAll();
            if (hit.Part == Part.Collapse) ToggleCollapsed();
            if (hit.Part == Part.Cancel) loginCancellation?.Cancel();
            if (hit.Part == Part.Menu) { ClientToScreen(hwnd, ref p); OpenMenu(p); }
        }
    }

    private void ToggleCollapsed()
    {
        config.Collapsed = !config.Collapsed;
        if (!Save()) config.Collapsed = !config.Collapsed;
        HideTip();
        hover = new(Part.Surface);
        scroll = 0;
        Render();
    }

    private void ShowTip()
    {
        if (captured || floating != null || hover.Tip == null || HitAt(hoverPoint).Tip is not { } text) return;
        var anchor = hoverPoint;
        ClientToScreen(hwnd, ref anchor);
        anchor.Y += Px(18);
        floating = new PopupWindow(hwnd, painter, () => floating = null, lines: text.Split('\n'));
        floating.Show(anchor);
    }

    private void HideTip()
    {
        if (floating?.IsTip == true) floating.Close();
    }
}
