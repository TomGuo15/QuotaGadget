using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static QuotaGadget.Native;

namespace QuotaGadget;

internal sealed class PopupWindow(nint owner, Painter painter, Action closed, List<MenuItem>? items = null,
    string[]? lines = null, List<Card>? cards = null, PopupWindow? parent = null)
{
    private const string ClassName = "QuotaGadget.Popup";
    private const int Padding = 8, Row = 32, Divider = 9;
    internal const int TrayRowHeight = 40, TrayWidth = 264, WideTrayWidth = 400;
    [ThreadStatic] private static Dictionary<nint, PopupWindow>? windows;
    private nint hwnd;
    private PopupWindow? child;
    private int dpi, width, height, contentHeight, scroll;
    private int hot = -1;
    private MenuItem? pressed;
    private int[] rowTops = [];
    private Point anchor;
    private bool above;
    private PopupWindow Root => parent?.Root ?? this;
    internal bool IsMenu => items != null;
    internal bool IsTip => lines != null;
    internal bool IsTray => cards != null;

    internal unsafe void Show(Point point, bool above = false)
    {
        anchor = point;
        this.above = above;
        var module = RegisterWindowClass(ClassName, 0x20000, &Proc);
        bool activate = IsMenu && parent == null;
        hwnd = CreateWindowExW(0x88u | (activate ? 0u : 0x08000000u), ClassName, null, 0x80000000,
            point.X, point.Y, 1, 1, owner, 0, module, 0);
        if (hwnd == 0) { closed(); return; }
        (windows ??= [])[hwnd] = this;
        SetRoundedFrame(hwnd);
        Place();
        if (AnimateWindow(hwnd, 100, activate ? 0xa0000u : 0x80000u) == 0)
            ShowWindow(hwnd, activate ? 5 : 4);
        if (activate)
        {
            SetForegroundWindow(hwnd);
            SetFocus(hwnd);
            SetCapture(hwnd);
        }
    }

    internal void UpdateCards(List<Card> updated)
    {
        cards = updated;
        Place();
        InvalidateRect(hwnd, 0, 0);
    }

    internal void SetCommandEnabled(string label, bool enabled)
    {
        if (items == null) return;
        int index = items.FindIndex(item => item.Label == label);
        if (index < 0 || items[index].Enabled == enabled) return;
        items[index] = items[index] with { Enabled = enabled };
        InvalidateRect(hwnd, 0, 0);
    }

    // Popup placement and hit testing apply the DPI ratio before rounding the coordinate.
    private int Px(int logical) => (int)Math.Round(logical * (dpi / DefaultDpi));
    private int Logical(int physical) => (int)Math.Floor(physical * (DefaultDpi / dpi));

    internal static Rect Fit(Rect work, Point origin, int w, int h)
    {
        w = Math.Min(work.Width, w);
        h = Math.Min(work.Height, h);
        int left = Math.Clamp(origin.X, work.Left, work.Right - w);
        int top = Math.Clamp(origin.Y, work.Top, work.Bottom - h);
        return new(left, top, left + w, top + h);
    }

    private void Place()
    {
        dpi = (int)GetDpiForWindow(parent?.hwnd ?? hwnd);
        int w;
        if (items != null)
        {
            w = Math.Max(184, items.Max(item => painter.MenuWidth(item, dpi)));
            rowTops = new int[items.Count];
            int y = Padding;
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i].Separator) y += Divider;
                rowTops[i] = y;
                y += Row;
            }
            contentHeight = y + Padding;
        }
        else if (lines != null)
        {
            w = lines.Max(line => painter.Measure(line, dpi)) + 20;
            contentHeight = 16 + lines.Length * 18;
        }
        else
        {
            w = cards!.Count == 0 ? 100 : TrayWidth;
            if (cards.Any(card => card.IsAntigravity)) w = WideTrayWidth;
            contentHeight = 2 * Padding + Math.Max(1, cards.Count) * TrayRowHeight;
        }
        var monitor = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        GetMonitorInfoW(parent != null ? MonitorFromWindow(parent.hwnd, 2) : MonitorFromPoint(anchor, 2), ref monitor);
        var p = anchor;
        int popupWidth = Px(w);
        if (parent != null && p.X + popupWidth > monitor.Work.Right)
        {
            GetWindowRect(parent.hwnd, out var r);
            p.X = r.Left - popupWidth + 1;
        }
        if (IsTray) p.X -= popupWidth / 2;
        if (above) p.Y -= Px(contentHeight + 8);
        var box = Fit(monitor.Work, p, popupWidth, Px(contentHeight));
        width = (int)Math.Ceiling(FromPixels(box.Width, dpi));
        height = (int)Math.Ceiling(FromPixels(box.Height, dpi));
        scroll = Math.Clamp(scroll, 0, Math.Max(0, contentHeight - height));
        SetWindowPos(hwnd, -1, box.Left, box.Top, box.Width, box.Height, 0x10);
    }

    private int ItemAt(int y)
    {
        for (int i = 0; i < items!.Count; i++)
        {
            int top = rowTops[i] - scroll;
            if (y >= top && y < top + Row) return i;
        }
        return -1;
    }

    private void Choose(int index)
    {
        if (hot == index) return;
        child?.Close();
        hot = index;
        if (index >= 0)
        {
            int top = rowTops[index];
            scroll = Math.Clamp(scroll, Math.Max(0, top + Row + Padding - height), top - Padding);
        }
        InvalidateRect(hwnd, 0, 0);
    }

    private void OpenChild(bool keyboard)
    {
        if (hot < 0 || items![hot] is not { Enabled: true, Children: { Count: > 0 } submenu }) return;
        if (child == null)
        {
            GetWindowRect(hwnd, out var r);
            child = new PopupWindow(hwnd, painter, () => child = null, items: submenu, parent: this);
            child.Show(new(r.Right - 1, r.Top + Px(rowTops[hot] - scroll - Padding)));
        }
        if (keyboard && child != null)
        {
            int selected = submenu.FindIndex(item => item.Enabled && item.Checked);
            child.Choose(selected >= 0 ? selected : submenu.FindIndex(item => item.Enabled));
        }
    }

    private void Invoke()
    {
        if (hot < 0 || !items![hot].Enabled) return;
        var item = items[hot];
        if (item.Children != null) OpenChild(true);
        else { Root.Close(); item.Invoke(); }
    }

    private void Key(int key)
    {
        if (child != null) { child.Key(key); return; }
        switch (key)
        {
            case 38: case 40:
                int count = items!.Count;
                int index = hot < 0 && key == 38 ? 0 : hot;
                for (int i = 0; i < count; i++)
                {
                    index = (index + (key == 40 ? 1 : -1) + count) % count;
                    if (items[index].Enabled) { Choose(index); break; }
                }
                break;
            case 39: OpenChild(true); break;
            case 37: if (parent != null) Close(); break;
            case 13: Invoke(); break;
            case 27: Close(); break;
        }
    }

    private PopupWindow? At(Point screen)
    {
        if (child?.At(screen) is { } deeper) return deeper;
        GetWindowRect(hwnd, out var r);
        return screen.X >= r.Left && screen.X < r.Right && screen.Y >= r.Top && screen.Y < r.Bottom ? this : null;
    }

    private void Mouse(uint msg, Point client)
    {
        ClientToScreen(hwnd, ref client);
        var panel = Root.At(client);
        if (panel == null)
        {
            if (msg is 0x201 or 0x204) Root.Close();
            return;
        }
        ScreenToClient(panel.hwnd, ref client);
        panel.Pointer(msg, panel.ItemAt(panel.Logical(client.Y)));
    }

    private void Pointer(uint msg, int index)
    {
        var item = index < 0 ? null : items![index];
        if (msg == 0x201) Root.pressed = item;
        if (index < 0) return;
        Choose(index);
        if (msg == 0x202 && ReferenceEquals(Root.pressed, item))
        {
            Root.pressed = null;
            Invoke();
        }
        else if (msg == 0x200) OpenChild(false);
    }

    internal void Close()
    {
        if (hwnd == 0) return;
        child?.Close();
        nint window = hwnd;
        windows!.Remove(window);
        hwnd = 0;
        if (IsMenu && parent == null) ReleaseCapture();
        DestroyWindow(window);
        closed();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint Proc(nint window, uint msg, nuint wp, nint lp) =>
        windows?.GetValueOrDefault(window) is { } popup ? popup.Message(msg, wp, lp) : DefWindowProcW(window, msg, wp, lp);

    private nint Message(uint msg, nuint wp, nint lp)
    {
        switch (msg)
        {
            case 15:
                var dc = BeginPaint(hwnd, out var ps);
                Draw(dc);
                EndPaint(hwnd, ps);
                return 0;
            case 0x2e0: child?.Close(); Place(); InvalidateRect(hwnd, 0, 0); return 0;
            case 0x318: Draw((nint)wp); return 0;
            case 0x14: return 1;
            case 0x21 when !IsMenu || parent != null: return 3; // MA_NOACTIVATE
            case 0x84 when IsTip: return -1; // transparent to hit testing
            case 0x100 when IsMenu: Root.Key((int)wp); return 0;
            case 0x200 or 0x201 or 0x202 or 0x204 when IsMenu:
                Mouse(msg, new((short)lp, (short)(lp >> 16)));
                return 0;
            case 0x20a:
                var panel = Root.At(new((short)lp, (short)(lp >> 16))) ?? this;
                panel.child?.Close();
                int step = panel.IsTray ? TrayRowHeight : Row;
                panel.scroll = Math.Clamp(panel.scroll - (short)(wp >> 16) * step / 120, 0, Math.Max(0, panel.contentHeight - panel.height));
                InvalidateRect(panel.hwnd, 0, 0);
                return 0;
            case 0x08 or 0x1f or 0x215 when IsMenu && parent == null: Close(); return 0;
            case 0x201 when !IsMenu: Close(); return 0;
            default: return DefWindowProcW(hwnd, msg, wp, lp);
        }
    }

    private void Draw(nint dc)
    {
        GetClientRect(hwnd, out var client);
        painter.BeginPopup(dc, dpi, client);
        if (lines != null)
            for (int i = 0; i < lines.Length; i++) painter.PopupLine(lines[i], i, width);
        else if (items != null)
            for (int i = 0; i < items.Count; i++)
            {
                int y = rowTops[i] - scroll;
                if (y + Row > 0 && y - Divider < height) painter.MenuRow(items[i], i == hot, y, width);
            }
        else if (cards!.Count == 0) painter.PopupLine("QuotaGadget", 0, width);
        else
            for (int i = 0; i < cards.Count; i++)
            {
                int y = Padding + i * TrayRowHeight - scroll;
                if (y + TrayRowHeight > 0 && y < height) painter.TrayRow(cards[i], y, width);
            }
        painter.EndPopup();
    }
}
