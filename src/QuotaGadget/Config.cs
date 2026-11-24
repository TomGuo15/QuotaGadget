using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace QuotaGadget;

internal enum Provider { Codex, Claude, Antigravity }
internal sealed record Account(Provider Provider, string ProfileDir);

[JsonSerializable(typeof(Config))]
[JsonSerializable(typeof(System.Text.Json.Nodes.JsonObject))]
[JsonSerializable(typeof(System.Text.Json.Nodes.JsonNode))]
[JsonSerializable(typeof(CachedQuota[]))]
internal partial class GadgetJson : JsonSerializerContext;

internal sealed class Config
{
    internal static readonly string Home = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QuotaGadget");
    internal static string FilePath => Path.Combine(Home, "accounts.json");
    internal static string Profiles => Path.Combine(Home, "profiles");
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter<Provider>(JsonNamingPolicy.CamelCase, false) }
    };
    private static readonly GadgetJson Json = new(Options);

    public List<Account> Accounts { get; set; } = [];
    public double? Left { get; set; }
    public double? Top { get; set; }
    public bool AlwaysOnTop { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public bool AutoUpdateClis { get; set; } = true;
    public DateTimeOffset? LastCliUpdateUtc { get; set; }
    public bool Collapsed { get; set; }
    public bool Hidden { get; set; }

    internal static string DefaultDir(Provider provider) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), provider switch
        {
            Provider.Codex => ".codex",
            Provider.Claude => ".claude",
            _ => throw new ArgumentOutOfRangeException(nameof(provider))
        });

    internal static List<Account> DiscoverDefaultAccounts(Func<Provider, string>? defaultDir = null,
        Func<string?>? antigravityEmail = null, Func<bool>? antigravityCliAvailable = null,
        Func<string, string?>? readEnvironment = null)
    {
        defaultDir ??= DefaultDir;
        readEnvironment ??= Environment.GetEnvironmentVariable;
        var codex = new Account(Provider.Codex, defaultDir(Provider.Codex));
        var claude = new Account(Provider.Claude, defaultDir(Provider.Claude));
        var antigravity = new Account(Provider.Antigravity, "");
        List<Account> accounts = [];
        if (CanImport(codex, readEnvironment)) accounts.Add(codex);
        if (CanImport(claude, readEnvironment)) accounts.Add(claude);
        if (CanImport(antigravity, readEnvironment, () =>
            (antigravityEmail == null ? AccountIdentity.Read(antigravity).Email : antigravityEmail()) != null &&
            (antigravityCliAvailable ?? HasAntigravityCli)())) accounts.Add(antigravity);
        return accounts;
    }

    internal static bool CanImport(Account account, Func<string, string?> readEnvironment, Func<bool>? agyLogin = null) =>
        account.Provider switch
        {
            Provider.Codex => File.Exists(Path.Combine(account.ProfileDir, "auth.json")),
            Provider.Claude => File.Exists(Path.Combine(account.ProfileDir, ".credentials.json")) ||
                ClaudeDesktop.HistoryFiles(readEnvironment).Any(),
            _ => AntigravityDesktop.HubFiles(readEnvironment).Any() || (agyLogin ?? HasAntigravityCli)()
        };

    private static bool HasAntigravityCli()
    {
        try { Login.FindCli(Provider.Antigravity); return true; }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 2) { return false; }
    }

    internal static bool IsOwned(string path) =>
        Path.IsPathFullyQualified(path) &&
        Guid.TryParseExact(Path.GetFileName(path), "N", out _) &&
        string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), Profiles, StringComparison.OrdinalIgnoreCase);

    internal static bool CheckProfile(Account account, Func<Provider, string>? defaultDir = null)
    {
        if (!IsOwned(account.ProfileDir) && string.Equals(account.ProfileDir,
            (defaultDir ?? DefaultDir)(account.Provider), StringComparison.OrdinalIgnoreCase)) return false;
        CheckOwnedPath(account.ProfileDir);
        return true;
    }

    internal static void CheckOwnedPath(string profile)
    {
        if (!IsOwned(profile)) throw new InvalidDataException();
        for (DirectoryInfo? dir = new(profile); dir != null; dir = dir.Parent)
        {
            if (dir.Exists && dir.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException();
        }
    }

    internal static void CheckCredentialFile(string path, bool owned)
    {
        if (owned && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException();
    }

    internal static Config Load(string path)
    {
        if (!File.Exists(path)) return new Config();
        var config = JsonSerializer.Deserialize(File.ReadAllText(path), Json.Config);
        if (config?.Accounts == null) throw new InvalidDataException();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in config.Accounts)
        {
            if (a == null || a.ProfileDir == null ||
                !paths.Add(a.ProfileDir)) throw new InvalidDataException();
            if (a.Provider == Provider.Antigravity)
            {
                if (a.ProfileDir.Length != 0) throw new InvalidDataException();
            }
            else CheckProfile(a);
        }
        return config;
    }

    internal void Save(string path) => WriteAtomic(path, JsonSerializer.Serialize(this, Json.Config));

    internal static void WriteAtomic(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, content);
            File.Move(temp, path, overwrite: true);
        }
        finally { File.Delete(temp); }
    }
}
