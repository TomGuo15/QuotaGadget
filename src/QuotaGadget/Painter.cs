using static QuotaGadget.Native;
using static QuotaGadget.Layout;

namespace QuotaGadget;

internal sealed class Painter : IDisposable
{
    private const int Background = 0x202020, TextColor = 0xf2f2f2, Muted = 0xa0a0a0;
    private const int Border = 0x3d3d3d;
    private const int MenuLabelLeft = 38, SubmenuCheckInset = 42, TrayLineHeight = 18;
    private readonly Dictionary<(int Size, int Weight, bool Symbol), nint> fonts = [];
    private readonly Dictionary<Provider, nint> icons = [];
    private int dpi;
    private nint dc;
    private bool dim;

    private int Px(double value) => ToPixels(value, dpi);
    private Rect Bounds(int x, int y, int w, int h) => new(Px(x), Px(y), Px(x + w), Px(y + h));

    private uint Color(int rgb)
    {
        int r = rgb >> 16, g = rgb >> 8 & 255, b = rgb & 255;
        if (dim) { r = Dim(r); g = Dim(g); b = Dim(b); }
        return (uint)(r | g << 8 | b << 16);
    }

    private static int Dim(int channel) => (channel * 65 + 32 * 35) / 100;

    internal unsafe void Draw(nint window, nint target, Rect area, int newDpi, Layout layout, int scroll,
        string updated, Hit hover, Part pressed, double angle)
    {
        if (area.Width <= 0 || area.Height <= 0) return;
        SetDpi(newDpi);
        dc = CreateCompatibleDC(target);
        var info = new BitmapInfo { Size = (uint)sizeof(BitmapInfo), Width = area.Width, Height = -area.Height,
            Planes = 1, Bits = 32 };
        var bitmap = CreateDIBSection(target, info, 0, out var pixels, 0, 0);
        if (bitmap == 0) { DeleteDC(dc); dc = 0; return; }
        var original = SelectObject(dc, bitmap);
        try
        {
            SelectObject(dc, GetStockObject(18)); // DC_BRUSH
            SelectObject(dc, GetStockObject(8));  // NULL_PEN
            SetBkMode(dc, 1);
            SetDCBrushColor(dc, Color(Background));
            FillRect(dc, area, GetStockObject(18));
            int by = layout.ButtonY;
            foreach (var part in new[] { Part.Collapse, Part.Refresh, Part.Menu })
                if ((!layout.Collapsed || part != Part.Menu) && (hover.Part == part || pressed == part))
                    Fill(layout.ButtonX(part), by, 28, 28, pressed == part ? 0x454545 : 0x3a3a3a, 4);
            Arrow(layout.ButtonX(Part.Refresh) + 14, by + 14, angle);
            Label(layout.Collapsed ? "\uE70D" : "\uE70E", layout.ButtonX(Part.Collapse), by, 28, 28, 12,
                align: 1, symbol: true);
            if (layout.Collapsed)
            {
                int saved = SaveDC(dc);
                int right = Margin + layout.SummaryViewport;
                IntersectClipRect(dc, Px(Margin), 0, Px(right), Px(layout.Height));
                for (int i = layout.SummaryIndex(scroll); i < layout.Cards.Count; i++)
                {
                    var (left, width) = layout.SummaryCell(i);
                    int x = Margin + left - scroll;
                    if (x >= right) break;
                    DrawCompact(layout.Cards[i], x, by, width);
                }
                RestoreDC(dc, saved);
            }
            else DrawExpanded(layout, scroll, updated, hover);
            if (layout.CliStatus != null)
                Label(layout.CliStatus, Margin, layout.Height - 24, layout.WindowWidth - 2 * Margin, 18, 11);
            GdiFlush();
            ApplyFrame(new Span<uint>(pixels, area.Width * area.Height), area.Width, area.Height, 8 * dpi / 96d);
            const uint sourceAlpha = 0x01ff0000; // AC_SRC_OVER with constant alpha 255 and AC_SRC_ALPHA
            if (window == 0)
                AlphaBlend(target, 0, 0, area.Width, area.Height, dc, 0, 0, area.Width, area.Height, sourceAlpha);
            else
                UpdateLayeredWindow(window, 0, 0, new Point(area.Width, area.Height), dc, default, 0, sourceAlpha, 2);
        }
        finally { SelectObject(dc, original); DeleteDC(dc); DeleteObject(bitmap); dc = 0; }
    }

    internal static void ApplyFrame(Span<uint> pixels, int width, int height, double radius)
    {
        // GDI writes RGB. Supply premultiplied charcoal and preserve opaque ink.
        const byte bodyAlpha = 236, bodyChannel = 30;
        const uint body = (uint)bodyAlpha << 24 | 0x1e1e1e;
        const uint border = 0xff000000 | Border;
        for (int i = 0; i < pixels.Length; i++)
        {
            uint rgb = pixels[i] & 0xffffff;
            pixels[i] = rgb == Background ? body : 0xff000000 | rgb;
        }
        pixels[..width].Fill(border);
        pixels[^width..].Fill(border);
        for (int y = 1; y < height - 1; y++)
            pixels[y * width] = pixels[y * width + width - 1] = border;

        radius = Math.Min(radius, Math.Min(width, height) / 2d);
        int corner = (int)Math.Ceiling(radius);
        for (int y = 0; y < corner; y++)
        for (int x = 0; x < corner; x++)
        {
            double dx = radius - x - 0.5, dy = radius - y - 0.5;
            double distance = radius - Math.Sqrt(dx * dx + dy * dy);
            double inner = Math.Clamp(distance - 0.5, 0, 1);
            double stroke = Math.Clamp(distance + 0.5, 0, 1) - inner;
            uint alpha = (uint)Math.Round(255 * stroke + bodyAlpha * inner);
            uint channel = (uint)Math.Round((Border & 255) * stroke + bodyChannel * inner);
            uint pixel = alpha << 24 | channel << 16 | channel << 8 | channel;
            int top = y * width, bottom = (height - 1 - y) * width;
            pixels[top + x] = pixels[top + width - 1 - x] =
                pixels[bottom + x] = pixels[bottom + width - 1 - x] = pixel;
        }
    }

    private void DrawCompact(Card card, int x, int y, int width)
    {
        DrawIconEx(dc, Px(x), Px(y + 7), Icon(card.Account.Provider), Px(14), Px(14), 0, 0, 3);
        dim = card.Stale;
        if (card.IsAntigravity)
        {
            x += PoolTextOffset;
            SummaryValue("G ", card.Gemini?.Summary, x, y, PoolColumnWidth, 28, 12);
            SummaryValue("C ", card.ClaudeGpt?.Summary, x + PoolColumnWidth, y, PoolColumnWidth, 28, 12);
        }
        else
        {
            var (offset, size) = width switch { < 46 => (15, 10), < 50 => (16, 11), _ => (18, 12) };
            SummaryValue("", card.Summary, x + offset, y, width - offset, 28, size);
        }
        dim = false;
    }

    private void SummaryValue(string prefix, QuotaBar? bar, int x, int y, int width, int height, int size) =>
        Label(prefix + (bar?.Percent ?? "—"), x, y, width, height, size, 600,
            bar == null ? Muted : BarColor(bar.RemainingPercent), ellipsis: false);

    private void DrawExpanded(Layout layout, int scroll, string updated, Hit hover)
    {
        Label("Quota", Margin, 6, 54, 28, 16, 600, ellipsis: false);
        Label(updated, 64, 6, 60, 28, 11, color: Muted, ellipsis: false);
        Label("\uE712", layout.ButtonX(Part.Menu), layout.ButtonY, 28, 28, 16, align: 1, symbol: true);
        int saved = SaveDC(dc);
        IntersectClipRect(dc, Px(Margin), Px(HeaderBottom), Px(Right), Px(layout.BodyBottom));
        int y = HeaderBottom - scroll;
        if (layout.Cards.Count == 0) Label("Open the menu to add an account", Margin, y + 4, 254, 26, 12);
        for (int i = 0; i < layout.Cards.Count; i++)
        {
            var c = layout.Cards[i];
            if (y + c.Height > HeaderBottom && y < layout.BodyBottom) DrawCard(c, y, hover.Card == i);
            y += c.Height;
        }
        RestoreDC(dc, saved);
        if (layout.Status != null)
        {
            int w = layout.Waiting ? 204 : Right - Margin;
            Label(layout.Status, Margin, layout.BodyBottom + 4, w, layout.StatusBottom - layout.BodyBottom - 4, 11, wrap: true);
            if (layout.Waiting)
            {
                Fill(Right - 44, layout.BodyBottom + 5, 44, 22, 0x494852, 5);
                Label("Cancel", Right - 44, layout.BodyBottom + 5, 44, 22, 11, align: 1);
            }
        }
    }

    private void DrawCard(Card card, int y, bool hot)
    {
        if (hot) Fill(Margin, y, Right - Margin, card.Height, 0x333333);
        Fill(Margin, y, Right - Margin, 1, 0x2f2f2f);
        var icon = Icon(card.Account.Provider);
        DrawIconEx(dc, Px(Margin), Px(y + 4), icon, Px(16), Px(16), 0, 0, 3);
        int labelRight = Right;
        if (card.Resets is { } resets)
        {
            int width = card.ResetWidth;
            Fill(Right - width, y + 4, width, 16, 0x3a3a3a, 10);
            Label("\uE81C", Right - width + 4, y + 4, 14, 16, 11, align: 1, symbol: true);
            Label(resets.Count.ToString(), Right - width + 18, y + 4, width - 21, 16, 11, 600, align: 1);
            labelRight -= width + 4;
        }
        if (card.Tier is { } tier)
        {
            int width = card.TierWidth;
            Fill(labelRight - width, y + 4, width, 16, 0x3a3a3a, 10);
            Label(tier, labelRight - width + 4, y + 4, width - 8, 16, 11, 600, align: 1);
        }
        Label(card.Label, 32, y + 4, card.LabelRight - 32, 16, 11, 600);
        if (card.Error != null) Label(card.ErrorTip!, Margin, y + Card.HeaderHeight, Right - Margin, 16, 11, color: 0xedc698);
        y += card.RowsOffset;
        dim = card.Stale;
        foreach (var row in card.Rows)
        {
            bool low = row.FiveHour?.RemainingPercent < 20 || row.Week?.RemainingPercent < 20;
            Label(row.Name, Margin, y, card.IsAntigravity ? PoolNameWidth : 54, 16, 11, color: low ? 0xf78d8f : TextColor);
            if (row.Single is { } single)
            {
                int barX = card.IsAntigravity ? FiveX : 64;
                Bar(single, barX, y, 176 - barX);
                Label(single.Percent, 176, y, 32, 16, 11, align: 2, ellipsis: false);
                Label(single.Reset, 208, y, Right - 208, 16, 11, color: Muted, align: 2, ellipsis: false);
            }
            if (row.FiveHour is { } five) Mini(five, FiveX, y, WeekX - FiveX - 9);
            if (row.Week is { } week) Mini(week, WeekX, y, Right - WeekX);
            y += 16;
        }
        dim = false;
    }

    private void Mini(QuotaBar bar, int x, int y, int width)
    {
        Label(bar.Period, x, y, 21, 16, 11, color: Muted);
        Bar(bar, x + 21, y, width - 52);
        Label(bar.Percent, x + width - 31, y, 31, 16, 11, align: 2, ellipsis: false);
    }

    internal static int BarColor(double remaining) => remaining >= 50 ? 0x80d8b7 : remaining >= 20 ? 0xedbb71 : 0xf78d8f;

    private void Bar(QuotaBar bar, int x, int y, int width)
    {
        Fill(x, y + 5, width, 6, 0x3a3a3a, 3);
        int length = (int)Math.Round(width * bar.RemainingPercent / 100);
        if (length > 0) Fill(x, y + 5, length, 6, BarColor(bar.RemainingPercent), 3);
    }

    private void Fill(int x, int y, int w, int h, int color, int radius = 0)
    {
        var r = Bounds(x, y, w, h);
        SetDCBrushColor(dc, Color(color));
        if (radius == 0) FillRect(dc, r, GetStockObject(18));
        else RoundRect(dc, r.Left, r.Top, r.Right + 1, r.Bottom + 1, Px(radius * 2), Px(radius * 2));
    }

    internal void BeginPopup(nint target, int popupDpi, Rect client)
    {
        SetDpi(popupDpi);
        dc = target;
        SaveDC(dc);
        SelectObject(dc, GetStockObject(18));
        SelectObject(dc, GetStockObject(8));
        SetBkMode(dc, 1);
        SetDCBrushColor(dc, Color(Border));
        FillRect(dc, client, GetStockObject(18));
        var inside = new Rect(client.Left + 1, client.Top + 1, client.Right - 1, client.Bottom - 1);
        SetDCBrushColor(dc, Color(0x2c2c2c));
        FillRect(dc, inside, GetStockObject(18));
        IntersectClipRect(dc, inside.Left, inside.Top, inside.Right, inside.Bottom);
    }

    internal void EndPopup()
    {
        RestoreDC(dc, -1);
        dc = 0;
    }

    internal void PopupLine(string text, int index, int width) =>
        Label(text, 10, 8 + index * 18, width - 20, 18, 12, color: index == 0 ? TextColor : Muted);

    internal int MenuWidth(MenuItem item, int dpi) => Measure(item.Label, dpi) + MenuLabelInsets(item);

    private static int MenuLabelInsets(MenuItem item) =>
        MenuLabelLeft + (item.Checked && item.Children != null ? SubmenuCheckInset + 8 : 38);

    internal void MenuRow(MenuItem item, bool active, int y, int width)
    {
        if (item.Separator) Fill(10, y - 5, width - 20, 1, 0x3d3d3d);
        if (active && item.Enabled) Fill(4, y, width - 8, 32, 0x3a3a3a, 4);
        int ink = item.Enabled ? TextColor : 0x868686;
        if (item.Icon is { } provider)
            DrawIconEx(dc, Px(12), Px(y + 8), Icon(provider), Px(16), Px(16), 0, 0, 3);
        else Label(item.Glyph, 10, y, 20, 32, 14, color: ink, align: 1, symbol: true);
        int labelWidth = width - MenuLabelInsets(item);
        if (labelWidth > 0) Label(item.Label, MenuLabelLeft, y, labelWidth, 32, 12, color: ink);
        if (item.Checked) Label("\uE73E", width - (item.Children == null ? 30 : SubmenuCheckInset), y, 16, 32, 12,
            color: ink, align: 1, symbol: true);
        if (item.Children != null) Label("\uE76C", width - 26, y, 16, 32, 10, color: ink, align: 1, symbol: true);
    }

    internal void TrayRow(Card card, int y, int width)
    {
        DrawIconEx(dc, Px(10), Px(y + (PopupWindow.TrayRowHeight - 16) / 2), Icon(card.Account.Provider), Px(16), Px(16), 0, 0, 3);
        y += (PopupWindow.TrayRowHeight - 2 * TrayLineHeight) / 2;
        var now = DateTimeOffset.Now;
        if (card.IsAntigravity)
        {
            int geminiX = width - 238, claudeX = width - 122;
            Label("Antigravity", 34, y, geminiX - 38, TrayLineHeight, 12, 600);
            Label(card.ErrorTip ?? card.Label, 34, y + TrayLineHeight, geminiX - 38, TrayLineHeight, 12, color: Muted);
            TrayPool("Gemini", card.Gemini, geminiX, y, claudeX - geminiX - 8, now);
            Label("·", claudeX - 8, y, 8, TrayLineHeight, 12, color: Muted);
            TrayPool("Claude/GPT", card.ClaudeGpt, claudeX, y, width - claudeX - 10, now);
            return;
        }
        Label(card.Label, 34, y, width - 122, TrayLineHeight, 12, 600);
        var summary = card.Summary;
        Label(summary == null ? "—" : $"{summary.Percent} left", width - 84, y, 74, TrayLineHeight, 12, 600,
            summary == null ? Muted : BarColor(summary.RemainingPercent), align: 2, ellipsis: false);
        string detail = TrayReset(summary, card.Bars, now) is { } next ? $"Resets {QuotaBar.LocalReset(next, now)}" :
            card.ErrorTip ?? "Reset unavailable";
        Label(detail, 34, y + TrayLineHeight, width - 44, TrayLineHeight, 12, color: Muted);
    }

    private void TrayPool(string name, QuotaRow? pool, int x, int y, int width, DateTimeOffset now)
    {
        var summary = pool?.Summary;
        SummaryValue(name + " ", summary, x, y, width, TrayLineHeight, 12);
        var next = pool == null ? null : TrayReset(summary, pool.Bars, now);
        Label(next is { } time ? $"Resets {QuotaBar.LocalReset(time, now)}" : "Reset unavailable",
            x, y + TrayLineHeight, width, TrayLineHeight, 12, color: Muted);
    }

    internal static DateTimeOffset? TrayReset(QuotaBar? summary, IEnumerable<QuotaBar> bars, DateTimeOffset now)
    {
        if (summary?.ResetsAt is { } reset && reset >= now) return reset;
        return summary?.Period == "Wk" ? null : QuotaBar.NextReset(bars, now);
    }

    private void SetDpi(int value)
    {
        if (dpi == value) return;
        Dispose();
        dpi = value;
    }

    internal int Measure(string text, int newDpi, int size = 12, int weight = 400)
    {
        SetDpi(newDpi);
        dc = GetDC(0);
        var saved = SaveDC(dc);
        try
        {
            Font(size, weight);
            GetTextExtentPoint32W(dc, text, text.Length, out var extent);
            return (int)Math.Ceiling(FromPixels(extent.X, dpi));
        }
        finally { RestoreDC(dc, saved); ReleaseDC(0, dc); dc = 0; }
    }

    private void Font(int size, int weight, bool symbol = false)
    {
        if (!fonts.TryGetValue((size, weight, symbol), out var font))
            fonts[(size, weight, symbol)] = font = CreateFontW(-Px(size), 0, 0, 0, weight, 0, 0, 0, 1, 0, 0, 4, 0,
                symbol ? "Segoe Fluent Icons" : size == 16 ? "Segoe UI Variable Display" : "Segoe UI Variable Text");
        SelectObject(dc, font);
    }

    private void Label(string text, int x, int y, int w, int h, int size, int weight = 400,
        int color = TextColor, uint align = 0, bool wrap = false, bool symbol = false, bool ellipsis = true)
    {
        Font(size, weight, symbol);
        SetTextColor(dc, Color(color));
        var r = Bounds(x, y, w, h);
        DrawTextW(dc, text, -1, ref r, align | 0x800 | (ellipsis ? 0x8000u : 0) | (wrap ? 0x10u : 0x24u));
    }

    private unsafe void Arrow(int x, int y, double angle)
    {
        Point At(double radius, double a) => new(Px(x + radius * Math.Cos(a + angle)), Px(y + radius * Math.Sin(a + angle)));
        Point* arc = stackalloc Point[50];
        for (int i = 0; i < 25; i++)
        {
            double a = 0.1 + i * 5.42 / 24;
            arc[i] = At(6.2, a);
            arc[49 - i] = At(5.1, a);
        }
        SetDCBrushColor(dc, Color(TextColor));
        Polygon(dc, arc, 50);
        arc[0] = At(8, 5.32); arc[1] = At(6.2, 5.85); arc[2] = At(3, 5.34);
        Polygon(dc, arc, 3);
    }

    private nint Icon(Provider provider)
    {
        if (icons.TryGetValue(provider, out var icon)) return icon;
        return icons[provider] = LoadIcon(provider.ToString(), Px(16));
    }

    internal static unsafe nint LoadIcon(string name, int size)
    {
        using var stream = typeof(Painter).Assembly.GetManifestResourceStream($"QuotaGadget.Assets.{name}.png")!;
        byte[] png = new byte[stream.Length];
        stream.ReadExactly(png);
        fixed (byte* data = png)
        {
            var icon = CreateIconFromResourceEx(data, (uint)png.Length, 1, 0x30000, size, size, 0);
            return icon != 0 ? icon : throw new InvalidDataException("Could not decode the gadget icon.");
        }
    }

    public void Dispose()
    {
        foreach (var icon in icons.Values) DestroyIcon(icon);
        foreach (var font in fonts.Values) DeleteObject(font);
        icons.Clear();
        fonts.Clear();
    }
}
