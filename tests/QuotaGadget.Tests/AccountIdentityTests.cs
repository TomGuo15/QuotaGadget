using System.Text.Json.Nodes;
using Xunit;

namespace QuotaGadget.Tests;

public class AccountIdentityTests
{
    [Theory]
    [InlineData("pro", "Pro")]
    [InlineData("prolite", "Pro Lite")]
    [InlineData("unknown", null)]
    public void ReadsCodexIdTokenAndClaudeMetadataFromTheirOwnProfiles(string plan, string? expectedTier)
    {
        using var profile = new OwnedProfile(Provider.Codex);
        var codex = profile.Account;
        var claude = codex with { Provider = Provider.Claude };
        string auth = profile.PathFor("auth.json"), metadata = profile.PathFor(".claude.json");
        Assert.Null(AccountIdentity.Read(claude).Email);
        string claims = $$$"""{"email":"one+codex@example.test","https://api.openai.com/auth":{"chatgpt_plan_type":"{{{plan}}}"},"pad":"~~~"}""";
        string payload = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(claims))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        File.WriteAllText(auth, $$$"""{"tokens":{"id_token":"abc.{{{payload}}}.sig","access_token":"other"}}""");
        File.WriteAllText(metadata, """{"oauthAccount":{"emailAddress":"two@example.test"}}""");
        Assert.Equal(new("one+codex@example.test", expectedTier), AccountIdentity.Read(codex));
        Assert.Equal(new("two@example.test", null), AccountIdentity.Read(claude));
        File.WriteAllText(profile.PathFor(".credentials.json"),
            """{"claudeAiOauth":{"subscriptionType":"max","rateLimitTier":"default_claude_max_20x"}}""");
        Assert.Equal(new("two@example.test", "Max 20×"), AccountIdentity.Read(claude));

        File.WriteAllText(auth, """{"tokens":{"id_token":"abc.*.sig"}}""");
        File.WriteAllText(metadata, """{"oauthAccount":{"emailAddress":"a display label"}}""");
        Assert.Equal(default, AccountIdentity.Read(codex));
        Assert.Null(AccountIdentity.Read(claude).Email);
        File.WriteAllText(metadata, "{");
        Assert.Equal(new(null, "Max 20×"), AccountIdentity.Read(claude));
    }

    [Theory]
    [InlineData("""{"token":{"access_token":"fixture"},"id_token_email":"three@example.test"}""", "three@example.test")]
    [InlineData("""{"token":{"email":"unrelated@example.test"}}""", null)]
    public void ReadsOnlyAntigravityProfileIdentity(string profile, string? expected) =>
        Assert.Equal(expected, AccountIdentity.FromAntigravity(JsonNode.Parse(profile)!.AsObject()).Email);

    [Theory]
    [InlineData("max", "default_claude_max_5x", "Max 5×")]
    [InlineData("max", "unrecognized", "Max")]
    [InlineData("pro", "default_claude_max_20x", "Pro")]
    [InlineData(null, "default_claude_max_20x", null)]
    [InlineData("unknown", null, null)]
    public void ClaudeOnlyDisplaysReportedPlanAndMatchingMultiplier(string? plan, string? limit, string? expected) =>
        Assert.Equal(expected, AccountIdentity.ClaudeTier(new JsonObject { ["subscriptionType"] = plan, ["rateLimitTier"] = limit }));
}
