namespace QuotaGadget;

internal sealed record QuotaBar(string Name, double RemainingPercent, string Reset, string Tip, DateTimeOffset? ResetsAt)
{
    public string Percent => $"{RemainingPercent:0}%";
    public string Period => Name == "Week" || Name.EndsWith(" Week", StringComparison.Ordinal) ? "Wk" :
        Name == "5h" || Name.EndsWith(" 5h", StringComparison.Ordinal) ? "5h" : "";

    internal static string LocalReset(DateTimeOffset time, DateTimeOffset now) =>
        time.LocalDateTime.ToString(time - now > TimeSpan.FromDays(6) ? "MMM d" : "ddd HH:mm");

    internal static DateTimeOffset? NextReset(IEnumerable<QuotaBar> bars, DateTimeOffset now) =>
        bars.Select(bar => bar.ResetsAt).Where(time => time >= now).Min();

    internal static QuotaBar From(QuotaWindow window, DateTimeOffset now)
    {
        double left = Math.Clamp(100 - window.UsedPercent, 0, 100);
        string reset = MainWindow.Countdown(window.ResetsAt, now);
        string tip = $"{left:0}% left";
        if (window.ResetsAt is { } time)
            tip += $" · resets {LocalReset(time, now)}";
        return new(window.Name, left, reset, tip, window.ResetsAt);
    }
}

internal sealed record QuotaRow(string Name, QuotaBar? Single = null, QuotaBar? FiveHour = null, QuotaBar? Week = null)
{
    public bool Grouped => Single == null;
    internal QuotaBar? Summary => Week ?? Single ?? FiveHour;
    internal IEnumerable<QuotaBar> Bars
    {
        get
        {
            if (Single != null) yield return Single;
            if (FiveHour != null) yield return FiveHour;
            if (Week != null) yield return Week;
        }
    }

    internal static List<QuotaRow> Build(Provider provider, IReadOnlyList<QuotaWindow> windows, DateTimeOffset now)
    {
        List<QuotaRow> rows = [];
        foreach (var window in windows)
        {
            var bar = QuotaBar.From(window, now);
            if (provider == Provider.Antigravity &&
                window.Name is "Gemini 5h" or "Gemini Week" or "Claude/GPT 5h" or "Claude/GPT Week")
            {
                var group = window.Name[..window.Name.LastIndexOf(' ')];
                int index = rows.FindIndex(row => row.Grouped && row.Name == group);
                if (index < 0) { index = rows.Count; rows.Add(new(group)); }
                rows[index] = window.Name.EndsWith("5h", StringComparison.Ordinal)
                    ? rows[index] with { FiveHour = bar } : rows[index] with { Week = bar };
            }
            else rows.Add(new(window.Name, Single: bar));
        }
        return rows;
    }
}
