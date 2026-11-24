namespace QuotaGadget;

internal sealed record Card(Account Account, string Label, string? Tier, string? Error, List<QuotaRow> Rows, bool Stale,
    SavedResets? Resets = null)
{
    internal QuotaBar? Lowest => Bars.MinBy(bar => bar.RemainingPercent);
    internal QuotaBar? Summary => Bars.FirstOrDefault(bar => bar.Period == "Wk") ?? Lowest;
    internal bool IsAntigravity => Account.Provider == Provider.Antigravity;
    internal QuotaRow? Gemini => Rows.Find(row => row.Name == "Gemini");
    internal QuotaRow? ClaudeGpt => Rows.Find(row => row.Name == "Claude/GPT");
    internal string? ErrorTip => Error switch
    {
        "Login expired — re-login" or "Sign in to load quota" => "Sign in again",
        "Check your connection" => "Offline",
        "Quota request timed out" => "Request timed out",
        "Rate limited — try later" => "Rate limited",
        "Install agy and check PATH" => "agy unavailable",
        "Update agy for quota reports" => "Update agy",
        _ => Error
    };
    internal string SummaryTip
    {
        get
        {
            if (ErrorTip is { } error) return $"{Label}\n{error}";
            if (IsAntigravity)
                return $"{Label}\nGemini {PoolWindows(Gemini)}\nClaude/GPT {PoolWindows(ClaudeGpt)}";
            string detail = string.Join(" · ", Bars
                .GroupBy(bar => bar.Name.Split(' ')[^1])
                .OrderBy(group => group.Key == "Week" ? 0 : 1)
                .Take(2).Select(group => $"{group.Key} {group.MinBy(bar => bar.RemainingPercent)!.Percent}"));
            return detail.Length == 0 ? Label : $"{Label}\n{detail}";
        }
    }
    private static string PoolWindows(QuotaRow? pool) => pool?.Single?.Percent ??
        $"5h {pool?.FiveHour?.Percent ?? "—"} · Wk {pool?.Week?.Percent ?? "—"}";
    internal IEnumerable<QuotaBar> Bars => Rows.SelectMany(row => row.Bars);
    internal int ResetWidth => Resets == null ? 0 : 24 + 7 * Resets.Count.ToString().Length;
    internal int TierWidth { get; set; }
    internal int LabelRight => Layout.Right - (ResetWidth == 0 ? 0 : ResetWidth + 4) - (TierWidth == 0 ? 0 : TierWidth + 4);
    internal bool LabelTruncated { get; set; }
    internal const int HeaderHeight = 24;
    internal int RowsOffset => HeaderHeight + (Error == null ? 0 : 16);
    internal int Height => RowsOffset + 16 * Rows.Count;
}

internal enum Part { Surface, Refresh, Menu, Collapse, Header, Row, Cancel }
internal readonly record struct Hit(Part Part, int Card = -1, string? Tip = null);

internal sealed class Layout(List<Card> cards, string? status, bool waiting, int maxHeight, bool collapsed = false,
    int maxWidth = Layout.Width, string? cliStatus = null)
{
    internal const int Width = 276, Margin = 8, HeaderBottom = 40, Right = Width - Margin;
    internal const int PoolNameWidth = 65, FiveX = Margin + PoolNameWidth + 3, WeekX = 168;
    internal const int PoolTextOffset = 16, PoolColumnWidth = 52;
    private const int CompactMarginsAndButtons = Margin * 2 + 56;
    private readonly int[] summaryOffsets = CompactOffsets(cards, maxWidth);
    internal int SummaryWidth => summaryOffsets[^1];
    internal int SummaryViewport => Math.Min(SummaryWidth, Math.Max(0, maxWidth - CompactMarginsAndButtons));
    internal (int Left, int Width) SummaryCell(int index) => (summaryOffsets[index], summaryOffsets[index + 1] - summaryOffsets[index]);
    internal int SummaryIndex(int x)
    {
        int i = Array.BinarySearch(summaryOffsets, x);
        return i >= 0 ? i : ~i - 1;
    }

    private static int[] CompactOffsets(List<Card> cards, int maxWidth)
    {
        const int poolCellWidth = PoolTextOffset + 2 * PoolColumnWidth;
        int dualPoolAccounts = cards.Count(card => card.IsAntigravity);
        int available = maxWidth - CompactMarginsAndButtons - dualPoolAccounts * poolCellWidth;
        int singleWidth = Math.Clamp(available / Math.Max(1, cards.Count - dualPoolAccounts), 42, 58);
        var offsets = new int[cards.Count + 1];
        for (int i = 0; i < cards.Count; i++)
            offsets[i + 1] = offsets[i] + (cards[i].IsAntigravity ? poolCellWidth : singleWidth);
        return offsets;
    }
    internal bool Collapsed => collapsed;
    internal int WindowWidth => collapsed ? Math.Max(CompactMarginsAndButtons + SummaryViewport, cliStatus == null ? 0 : Width) : Width;
    internal int ButtonY => collapsed ? 4 : 6;
    internal int ButtonX(Part part) => WindowWidth - Margin - (part switch
    {
        Part.Refresh => 56,
        Part.Collapse when !collapsed => 84,
        _ => 28
    });
    internal List<Card> Cards => cards;
    internal string? Status => status;
    internal string? CliStatus => cliStatus;
    internal bool Waiting => waiting;
    internal int ContentHeight { get; } = cards.Count == 0 ? 32 : cards.Sum(card => card.Height);
    private int CliFooter => cliStatus == null ? 0 : 22;
    internal int Height => collapsed ? 36 + CliFooter : Math.Min(maxHeight, HeaderBottom + ContentHeight + Footer + Margin + CliFooter);
    private int Footer => status == null ? 0 : waiting ? 32 : 44;
    internal int StatusBottom => Height - Margin - CliFooter;
    internal int BodyBottom => StatusBottom - Footer;
    internal int MaxScroll => collapsed ? SummaryWidth - SummaryViewport :
        Math.Max(0, HeaderBottom + ContentHeight - BodyBottom);

    internal Hit HitTest(int x, int y, int scroll)
    {
        if (x < 0 || x >= WindowWidth || y < 0 || y >= Height) return new(Part.Surface);
        if (y >= ButtonY && y < ButtonY + 28 && x < WindowWidth - Margin)
        {
            if (x >= ButtonX(Part.Menu)) return collapsed ? new(Part.Collapse, Tip: "Expand") : new(Part.Menu, Tip: "Gadget menu");
            if (x >= ButtonX(Part.Refresh)) return new(Part.Refresh, Tip: "Refresh now");
            if (!collapsed && x >= ButtonX(Part.Collapse)) return new(Part.Collapse, Tip: "Collapse to one row");
            if (collapsed && x >= Margin && x < Margin + SummaryViewport)
            {
                int index = SummaryIndex(x - Margin + scroll);
                return new(Part.Row, index, cards[index].SummaryTip);
            }
        }
        if (collapsed) return new(Part.Surface);
        if (y >= BodyBottom)
            return waiting && y < StatusBottom && x >= Right - 44 ? new(Part.Cancel) : new(Part.Surface);
        if (y < HeaderBottom) return new(Part.Surface);
        int offset = y - HeaderBottom + scroll;
        for (int i = 0; i < cards.Count; i++)
        {
            var card = cards[i];
            if (offset < card.Height)
            {
                if (offset < Card.HeaderHeight) return new(Part.Header, i,
                    card.Resets != null && x >= Right - card.ResetWidth && x < Right ? card.Resets.Tooltip :
                    card.LabelTruncated && x >= 32 && x < card.LabelRight ? card.Label : null);
                if (offset < card.RowsOffset) return new(Part.Row, i, card.ErrorTip);
                var row = card.Rows[(offset - card.RowsOffset) / 16];
                var bar = row.Single ?? (x >= WeekX ? row.Week : x >= FiveX ? row.FiveHour : null);
                return new(Part.Row, i, bar?.Tip);
            }
            offset -= card.Height;
        }
        return new(Part.Surface);
    }
}
