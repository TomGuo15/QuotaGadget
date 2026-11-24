using System.Text.Json;

namespace QuotaGadget;

internal sealed record CachedQuota(Account Account, QuotaWindow[] Windows, string? Tier, string? Email, DateTimeOffset FetchedAt,
    SavedResets? Resets = null)
{
    internal bool IsStale(DateTimeOffset now) => now - FetchedAt > TimeSpan.FromMinutes(15);
}

internal static class QuotaCache
{
    internal static Dictionary<Account, CachedQuota> Load(string path, IReadOnlyList<Account> accounts)
    {
        Dictionary<Account, CachedQuota> entries = [];
        try
        {
            using var file = File.OpenRead(path);
            foreach (var entry in JsonSerializer.Deserialize(file, GadgetJson.Default.CachedQuotaArray) ?? [])
            {
                if (entry?.Account != null && accounts.Contains(entry.Account) && entry.FetchedAt != default &&
                    entry.Windows is { Length: > 0 } && entry.Windows.All(w => w != null &&
                        !string.IsNullOrWhiteSpace(w.Name) && w.UsedPercent is >= 0 and <= 100))
                    entries[entry.Account] = entry;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // The quota providers can repopulate a missing or unreadable cache.
        }
        return entries;
    }

    internal static void Save(string path, IEnumerable<CachedQuota> entries) =>
        Config.WriteAtomic(path, JsonSerializer.Serialize(entries.ToArray(), GadgetJson.Default.CachedQuotaArray));
}
