using System.Net.Mail;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace QuotaGadget;

internal readonly partial record struct AccountIdentity(string? Email, string? Tier, bool ClaudeDesktopLogin = false)
{
    internal static AccountIdentity Read(Account account, Func<Provider, string>? defaultDir = null,
        Func<string, string?>? readEnvironment = null)
    {
        try
        {
            if (account.Provider == Provider.Antigravity) return FromAntigravity(ReadAntigravity());
            bool owned = Config.CheckProfile(account, defaultDir);
            if (account.Provider == Provider.Claude)
            {
                string metadataDir = owned ? account.ProfileDir : Directory.GetParent(account.ProfileDir)!.FullName;
                var metadata = ReadObject(Path.Combine(metadataDir, ".claude.json"), owned);
                var credentials = Path.Combine(account.ProfileDir, ".credentials.json");
                var auth = ReadObject(credentials, owned);
                return new(Address((metadata?["oauthAccount"] as JsonObject)?["emailAddress"]),
                    ClaudeTier(auth?["claudeAiOauth"] as JsonObject),
                    !owned && auth == null && !File.Exists(credentials) &&
                    ClaudeDesktop.HistoryFiles(readEnvironment ?? Environment.GetEnvironmentVariable).Any());
            }
            var data = ReadObject(Path.Combine(account.ProfileDir, "auth.json"), owned);
            var parts = data?["tokens"]?["id_token"].Text()?.Split('.');
            if (parts is not { Length: 3 }) return default;
            string payload = parts[1].Replace('-', '+').Replace('_', '/');
            var bytes = Convert.FromBase64String(payload.PadRight((payload.Length + 3) / 4 * 4, '='));
            var claims = JsonNode.Parse(bytes) as JsonObject;
            var plan = (claims?["https://api.openai.com/auth"] as JsonObject)?["chatgpt_plan_type"].Text();
            return new(Address(claims?["email"]), plan switch
            {
                "pro" => "Pro", "prolite" => "Pro Lite", "plus" => "Plus", "free" => "Free", "team" => "Team",
                "business" => "Business", "enterprise" => "Enterprise", _ => null
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or
            FormatException or InvalidDataException or InvalidOperationException or ArgumentException)
        { return default; }
    }

    private static JsonObject? ReadObject(string path, bool owned)
    {
        try
        {
            Config.CheckCredentialFile(path, owned);
            using var stream = File.OpenRead(path);
            return JsonNode.Parse(stream) as JsonObject;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        { return null; }
    }

    internal static string? ClaudeTier(JsonObject? oauth) => oauth?["subscriptionType"].Text() switch
    {
        "pro" => "Pro",
        "max" => oauth["rateLimitTier"].Text() switch
        {
            "default_claude_max_5x" => "Max 5×", "default_claude_max_20x" => "Max 20×", _ => "Max"
        },
        "team" => "Team", "enterprise" => "Enterprise", _ => null
    };

    internal static string? Address(JsonNode? field)
    {
        string? text = field.Text()?.Trim();
        return MailAddress.TryCreate(text, out var parsed) && parsed.Address == text && !text.Any(char.IsControl)
            ? text : null;
    }

    internal static AccountIdentity FromAntigravity(JsonObject? profile) => new(Address(profile?["id_token_email"]), null);

    private static JsonObject? ReadAntigravity()
    {
        if (!CredRead("gemini:antigravity", 1, 0, out var ptr)) return null;
        try
        {
            var entry = Marshal.PtrToStructure<CredentialHeader>(ptr);
            var bytes = new byte[entry.CredentialBlobSize];
            try
            {
                Marshal.Copy(entry.CredentialBlob, bytes, 0, bytes.Length);
                return JsonNode.Parse(bytes) as JsonObject;
            }
            catch (JsonException) { return null; }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        finally { CredFree(ptr); }
    }

    // Prefix of the Windows CREDENTIALW structure, through CredentialBlob.
    [StructLayout(LayoutKind.Sequential)]
    private struct CredentialHeader
    {
        public uint Flags, Type;
        public IntPtr TargetName, Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "CredReadW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredRead(string target, uint type, uint flags, out IntPtr credential);
    [LibraryImport("advapi32.dll")]
    private static partial void CredFree(IntPtr credential);
}
