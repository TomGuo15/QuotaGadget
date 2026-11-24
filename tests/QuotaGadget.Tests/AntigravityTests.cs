using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace QuotaGadget.Tests;

[Collection("CLI path")]
public class AntigravityTests
{
    private const string Csrf = "synthetic-csrf";
    private const string SummaryMethod = "RetrieveUserQuotaSummary", StatusMethod = "GetUserStatus";
    private const string Summary = """
        {"response":{"groups":[{"buckets":[
          {"bucketId":"gemini-weekly","remainingFraction":0.75},
          {"bucketId":"3p-5h","remainingFraction":0.5}]}]}}
        """;
    private const string Status = """
        {"userStatus":{"email":"ide@example.test","planStatus":{"planInfo":{"planName":"Pro"}},
          "cascadeModelConfigData":{"clientModelConfigs":[
            {"label":"Gemini Pro","quotaInfo":{"remainingFraction":0.25}}]}}}
        """;

    private const string Report = """
        {"status":"SUCCESS","command":{"name":"usage","data":{"groups":[
          {"name":"Gemini Models","buckets":[{"window":"weekly","remaining_fraction":0.75}]}
        ]}}}
        """;
    private static readonly AccountQuota DesktopQuota = new([new("Claude/GPT Week", 40, null)], Email: "desktop@example.test");
    private static readonly AccountQuota DesktopUnavailable = AccountQuota.Fail("Open Antigravity or install agy");

    [Fact]
    public async Task HttpUsesSummaryWindowsAndStatusIdentity()
    {
        using var files = new HubFiles();
        using var server = new HubServer();
        var serving = server.ServeAsync(files.Token, new(200, Summary), new(200, Status));

        var quota = await AntigravityDesktop.FetchAsync([files.Hub(server.Port)], files.Token);

        AssertRpcPair(await serving, server.Port);
        Assert.Null(quota.Error);
        Assert.Equal("ide@example.test", quota.Email);
        Assert.Equal("Pro", quota.Tier);
        Assert.Equal(new QuotaWindow[] { new("Gemini Week", 25, null), new("Claude/GPT 5h", 50, null) }, quota.Windows);
    }

    [Fact]
    public async Task HttpsFallbackKeepsSummaryWhenStatusFails()
    {
        using var files = new HubFiles();
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=127.0.0.1", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        // Schannel's TLS server needs a private key it can reopen during the handshake.
        using var certificate = new X509Certificate2(generated.Export(X509ContentType.Pfx));
        using var http = new HubServer();
        using var https = new HubServer(certificate);
        var servingHttp = http.ServeAsync(files.Token, new(503), new(503));
        var servingHttps = https.ServeAsync(files.Token, new(200, Summary), new(503));

        var quota = await AntigravityDesktop.FetchAsync([files.Hub(http.Port, https.Port)], files.Token);

        AssertRpcPair(await servingHttp, http.Port);
        AssertRpcPair(await servingHttps, https.Port);
        Assert.Null(quota.Error);
        Assert.Null(quota.Email);
        Assert.Null(quota.Tier);
        Assert.Equal(new QuotaWindow[] { new("Gemini Week", 25, null), new("Claude/GPT 5h", 50, null) }, quota.Windows);
    }

    [Fact]
    public async Task MalformedAndDeadHubsAreSkippedBeforeUsingFallbackStatus()
    {
        using var files = new HubFiles();
        using var rejected = new HubServer();
        using var server = new HubServer();
        var serving = server.ServeAsync(files.Token, new(200, "malformed"), new(200, Status));

        var quota = await AntigravityDesktop.FetchAsync([files.Write("{malformed"),
            files.Hub(rejected.Port, pid: int.MaxValue), files.Hub(rejected.Port, token: "invalid\r\ntoken"),
            files.Hub(server.Port)], files.Token);

        Assert.False(rejected.HasPendingConnection);
        Assert.Null(quota.Error);
        Assert.Equal(new QuotaWindow("Gemini", 75, null), Assert.Single(quota.Windows));
        Assert.Equal("ide@example.test", quota.Email);
        Assert.Equal("Pro", quota.Tier);
        AssertRpcPair(await serving, server.Port);
    }

    [Fact]
    public async Task StatusCanReturnQuotaAfterSummaryTimesOut()
    {
        using var files = new HubFiles();
        using var server = new HubServer();
        var fetching = AntigravityDesktop.FetchAsync([files.Hub(server.Port)], files.Token);
        using var unanswered = await server.AcceptAsync(files.Token);
        AssertRpc(await ReadRequest(unanswered.GetStream(), files.Token), SummaryMethod, server.Port);

        var serving = server.ServeAsync(files.Token, new Response(200, Status));
        var quota = await fetching;

        AssertRpc(Assert.Single(await serving), StatusMethod, server.Port);
        Assert.Null(quota.Error);
        Assert.Equal(75, Assert.Single(quota.Windows).UsedPercent);
        Assert.Equal("ide@example.test", quota.Email);
    }

    [Fact]
    public async Task HubDoesNotFollowRedirects()
    {
        using var files = new HubFiles();
        using var destination = new HubServer();
        using var server = new HubServer();
        var redirect = new Response(307, Headers: $"Location: http://127.0.0.1:{destination.Port}/other\r\n");
        var serving = server.ServeAsync(files.Token, redirect, redirect);

        var quota = await AntigravityDesktop.FetchAsync([files.Hub(server.Port)], files.Token);

        Assert.Equal("Open Antigravity or install agy", quota.Error);
        Assert.False(destination.HasPendingConnection);
        AssertRpcPair(await serving, server.Port);
    }

    [Fact]
    public async Task CallerCancellationAbortsAnInFlightRpc()
    {
        using var files = new HubFiles();
        using var server = new HubServer();
        using var caller = new CancellationTokenSource();
        var fetching = AntigravityDesktop.FetchAsync([files.Hub(server.Port)], caller.Token);
        using var client = await server.AcceptAsync(files.Token);
        var request = await ReadRequest(client.GetStream(), files.Token);

        caller.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fetching.WaitAsync(files.Token));
        Assert.True(fetching.IsCanceled);
        AssertRpc(request, SummaryMethod, server.Port);
        Assert.False(server.HasPendingConnection);
    }

    [Fact]
    public void SummaryMapsEachReportedBucketByItsId()
    {
        var data = JsonNode.Parse("""
            {"response":{"groups":[
              {"displayName":"Gemini Models","buckets":[
                {"bucketId":"gemini-5h","remainingFraction":0.875,"resetTime":"2030-01-01T02:00:00+02:00"},
                {"bucketId":"gemini-weekly","remainingFraction":0.625}]},
              {"displayName":"Claude and GPT models","buckets":[
                {"bucketId":"3p-5h","remainingFraction":1},
                {"bucketId":"3p-weekly","remainingFraction":0}]}
            ]}}
            """);

        var quota = AntigravityDesktop.ParseSummary(data);

        Assert.Null(quota.Error);
        Assert.Collection(quota.Windows,
            w => Assert.Equal(new("Gemini 5h", 12.5, DateTimeOffset.Parse("2030-01-01T00:00:00Z")), w),
            w => Assert.Equal(new("Gemini Week", 37.5, null), w),
            w => Assert.Equal(new("Claude/GPT 5h", 0, null), w),
            w => Assert.Equal(new("Claude/GPT Week", 100, null), w));
    }

    [Fact]
    public void SummaryKeepsValidBucketsWhenOthersAreMissingOrDisabled()
    {
        var data = JsonNode.Parse("""
            {"groups":[
              false,
              {"displayName":"Gemini Models","buckets":[
                {"bucketId":"gemini-5h","remainingFraction":0,"disabled":true},
                {"bucketId":"gemini-weekly","resetTime":"2030-01-01T00:00:00Z"}]},
              {"displayName":"Claude and GPT models","buckets":[
                {"bucketId":"3p-5h","remainingFraction":0.5,"enabled":false},
                {"window":"weekly","remainingFraction":0.75,"resetTime":"bad timestamp"}]},
              {"displayName":"Gemini 4 Argon","buckets":[
                {"bucketId":"gemini-argon-wtu-weekly","window":"weekly","remainingFraction":0.25}]}
            ]}
            """);

        var quota = AntigravityDesktop.ParseSummary(data);

        Assert.Null(quota.Error);
        Assert.Equal(new QuotaWindow("Claude/GPT Week", 25, null), Assert.Single(quota.Windows));
    }

    [Fact]
    public void DesktopSummaryUsesKnownBucketsAndPrefersAResetOnTiedUsage()
    {
        var report = JsonNode.Parse("""
            {"groups":[
              {"displayName":"Gemini Models","buckets":[
                {"window":"weekly","remainingFraction":0.625},
                {"bucketId":"gemini-weekly","remainingFraction":0.25},
                {"bucketId":"gemini-weekly","remainingFraction":0.25,"resetTime":"2030-01-01T00:00:00Z"},
                {"bucketId":"gemini-weekly","remainingFraction":0.25,"resetTime":"2030-01-02T00:00:00Z"},
                {"bucketId":"3p-5h","window":"weekly","remainingFraction":0.5}]},
              {"displayName":"Other Models","buckets":[{"window":"weekly","remainingFraction":0}]}]}
            """);

        Assert.Collection(AntigravityDesktop.ParseSummary(report).Windows,
            w => Assert.Equal(new("Gemini Week", 75, DateTimeOffset.Parse("2030-01-01T00:00:00Z")), w),
            w => Assert.Equal(new("Claude/GPT 5h", 50, null), w));
    }

    [Fact]
    public void StatusDeduplicatesModelPoolsAndPreservesTheSelectedReading()
    {
        var data = JsonNode.Parse("""
            {"userStatus":{
              "email":"sample@example.invalid",
              "planStatus":{"planInfo":{"planName":"Pro"}},
              "cascadeModelConfigData":{"clientModelConfigs":[
                {"label":"Gemini 3.1 Pro (Low)","quotaInfo":{"remainingFraction":0.75,"resetTime":"2030-01-01T01:00:00Z"}},
                {"label":"Gemini 3.1 Pro (High)","quotaInfo":{"remainingFraction":0.50,"resetTime":"2030-01-03T01:00:00Z"}},
                {"label":"Gemini 4 Argon","quotaInfo":{"remainingFraction":0.125}},
                {"label":"Claude Sonnet","quotaInfo":{"remainingFraction":0.25,"resetTime":"2030-01-02T01:00:00Z"}},
                {"label":"GPT-OSS 120B","quotaInfo":{"disabled":true,"remainingFraction":0.125}},
                {"label":"GPT-OSS 120B","quotaInfo":{"remainingFraction":0.25,"resetTime":"2030-01-02T01:00:00Z"}},
                {"label":"Claude Opus","enabled":false,"quotaInfo":{"remainingFraction":0}},
                {"label":"Other model","quotaInfo":{"remainingFraction":0}}
              ]}
            }}
            """);

        var quota = AntigravityDesktop.ParseStatus(data);

        Assert.Null(quota.Error);
        Assert.Equal("sample@example.invalid", quota.Email);
        Assert.Equal("Pro", quota.Tier);
        Assert.Collection(quota.Windows,
            w => Assert.Equal(new("Gemini", 50, DateTimeOffset.Parse("2030-01-03T01:00:00Z")), w),
            w => Assert.Equal(new("Claude/GPT", 75, DateTimeOffset.Parse("2030-01-02T01:00:00Z")), w));
    }

    [Fact]
    public void StatusPreservesValidMetadataWithoutUsableQuotas()
    {
        var data = JsonNode.Parse("""
            {"userStatus":{"email":" person@example.invalid ","userTier":{"name":"Ultra"},
              "cascadeModelConfigData":{"clientModelConfigs":[
                {"label":"Gemini 3 Flash","quotaInfo":{"resetTime":"2030-01-02T01:00:00Z"}},
                {"label":"Claude Sonnet","quotaInfo":{"remainingFraction":-1}},
                {"label":"GPT-OSS 120B","quotaInfo":{"remainingFraction":1.5}}
              ]}
            }}
            """);

        var quota = AntigravityDesktop.ParseStatus(data);

        Assert.NotNull(quota.Error);
        Assert.Empty(quota.Windows);
        Assert.Equal("person@example.invalid", quota.Email);
        Assert.Equal("Ultra", quota.Tier);
    }

    [Fact]
    public void StatusRejectsInvalidEmailAndMissingPlan()
    {
        var data = JsonNode.Parse("""
            {"userStatus":{"email":"Synthetic Person <person@example.invalid>","planStatus":false,
              "cascadeModelConfigData":{"clientModelConfigs":[
                {"label":"Gemini 3 Flash","quotaInfo":{"remainingFraction":0}}
              ]}
            }}
            """);

        var quota = AntigravityDesktop.ParseStatus(data);

        Assert.Null(quota.Email);
        Assert.Null(quota.Tier);
        Assert.Equal(new QuotaWindow("Gemini", 100, null), Assert.Single(quota.Windows));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("""{"response":{"groups":false},"userStatus":123}""")]
    public void UnexpectedShapesProduceNoQuota(string json)
    {
        var data = JsonNode.Parse(json);

        Assert.NotNull(AntigravityDesktop.ParseSummary(data).Error);
        Assert.NotNull(AntigravityDesktop.ParseStatus(data).Error);
    }

    [Fact]
    public async Task SuccessfulCliDoesNotOpenTheDesktopSource()
    {
        var provider = WithReport("1.1.11", Report, _ => throw new NotSupportedException());
        var result = await provider.FetchAsync("cli@example.test", CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal(new QuotaWindow("Gemini Week", 25, null), Assert.Single(result.Windows));
        Assert.Equal("cli@example.test", result.Email);
    }

    [Fact]
    public void CliReportKeepsRepeatedWindowsAndUnknownLabelsInReportOrder()
    {
        var report = JsonNode.Parse("""
            {"status":"SUCCESS","command":{"name":"usage","data":{"groups":[
              {"name":"Gemini Models","buckets":[{"window":"weekly","remaining_fraction":0.5},
                {"window":"weekly","remaining_fraction":0.25}]},
              {"name":"Other Models","buckets":[{"window":"daily","remaining_fraction":0.75}]}]}}}
            """)!;

        Assert.Collection(AntigravityQuota.Parse(report).Windows,
            w => Assert.Equal(new("Gemini Week", 50, null), w),
            w => Assert.Equal(new("Gemini Week", 75, null), w),
            w => Assert.Equal(new("Other Models daily", 25, null), w));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MissingCliUsesDesktopOrPromptsToOpenAntigravity(bool desktopAvailable)
    {
        var provider = new AntigravityQuota(findCli: () => throw new Win32Exception(2),
            runCli: (_, _, _) => throw new NotSupportedException(),
            fetchDesktop: _ => Task.FromResult(desktopAvailable ? DesktopQuota : DesktopUnavailable));
        var result = await provider.FetchAsync("cli@example.test", CancellationToken.None);

        if (desktopAvailable) Assert.Same(DesktopQuota, result);
        else Assert.Equal("Open Antigravity or install agy", result.Error);
    }

    [Theory]
    [InlineData("1.1.10", null, "Update agy for quota reports")]
    [InlineData("unknown version", null, "Update agy for quota reports")]
    [InlineData("1.1.11", null, "Check agy login or connection")]
    [InlineData("1.1.11", "malformed", "Check login or quota data")]
    [InlineData("1.1.11", "[]", "Check login or quota data")]
    [InlineData("1.1.11", """{"status":"SUCCESS","command":{"name":"usage","data":{"groups":[]}}}""", "Quota unavailable")]
    public async Task UnusableCliReportUsesDesktopAndRetainsCliFailureIfUnavailable(string version,
        string? report, string expectedError)
    {
        var provider = WithReport(version, report, _ => Task.FromResult(DesktopQuota));
        Assert.Same(DesktopQuota, await provider.FetchAsync(null, CancellationToken.None));

        provider = WithReport(version, report, _ => Task.FromResult(DesktopUnavailable));
        Assert.Equal(expectedError, (await provider.FetchAsync(null, CancellationToken.None)).Error);
    }

    [Fact]
    public async Task FailureToStartDiscoveredCliFallsBackWhileRetainingItsError()
    {
        var provider = new AntigravityQuota(findCli: () => "fixture-agy.exe",
            runCli: (_, _, _) => throw new Win32Exception(2), fetchDesktop: _ => Task.FromResult(DesktopQuota));
        Assert.Same(DesktopQuota, await provider.FetchAsync(null, CancellationToken.None));

        provider = new AntigravityQuota(findCli: () => "fixture-agy.exe",
            runCli: (_, _, _) => throw new Win32Exception(2), fetchDesktop: _ => Task.FromResult(DesktopUnavailable));
        Assert.Equal("Install agy and check PATH", (await provider.FetchAsync(null, CancellationToken.None)).Error);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CliTimeoutFallsBackWithTheCallerToken(bool desktopAvailable)
    {
        using var caller = new CancellationTokenSource();
        var provider = new AntigravityQuota(findCli: () => "fixture-agy.exe",
            runCli: (_, args, _) => args == "--version" ? Task.FromResult<string?>("1.1.11") :
                Task.FromCanceled<string?>(new CancellationToken(canceled: true)),
            fetchDesktop: token =>
            {
                Assert.Equal(caller.Token, token);
                return Task.FromResult(desktopAvailable ? DesktopQuota : DesktopUnavailable);
            });

        var result = await provider.FetchAsync(null, caller.Token);
        if (desktopAvailable) Assert.Same(DesktopQuota, result);
        else Assert.Equal("Quota request timed out", result.Error);
    }

    [Fact]
    public async Task CallerCancellationPropagatesWithoutOpeningTheDesktopSource()
    {
        using var caller = new CancellationTokenSource();
        var provider = new AntigravityQuota(findCli: () => "fixture-agy.exe", runCli: (_, args, token) =>
        {
            if (args == "--version") return Task.FromResult<string?>("1.1.11");
            caller.Cancel();
            return Task.FromCanceled<string?>(token);
        }, fetchDesktop: _ => throw new NotSupportedException());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.FetchAsync(null, caller.Token));
    }

    private static AntigravityQuota WithReport(string version, string? report,
        Func<CancellationToken, Task<AccountQuota>> fetchDesktop) => new(findCli: () => "fixture-agy.exe",
        runCli: (_, args, _) => Task.FromResult(args switch
        {
            "--version" => version,
            "-p /usage --output-format json --log-file NUL" => report,
            _ => throw new NotSupportedException()
        }), fetchDesktop: fetchDesktop);

    private static void AssertRpcPair(Request[] requests, int port) => Assert.Collection(requests,
        request => AssertRpc(request, SummaryMethod, port), request => AssertRpc(request, StatusMethod, port));

    private static void AssertRpc(Request request, string method, int port)
    {
        Assert.Equal($"POST /exa.language_server_pb.LanguageServerService/{method} HTTP/1.1", request.Line);
        Assert.Equal($"127.0.0.1:{port}", request.Headers["Host"]);
        Assert.Equal(Csrf, request.Headers["X-Codeium-Csrf-Token"]);
        Assert.Equal("1", request.Headers["Connect-Protocol-Version"]);
        Assert.Equal("application/json; charset=utf-8", request.Headers["Content-Type"]);
        var expected = JsonNode.Parse("""{"metadata":{"ideName":"antigravity","extensionName":"quota-gadget","locale":"en"}}""");
        Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(request.Body)));
    }

    private static async Task<Request> ReadRequest(Stream stream, CancellationToken ct)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        string start = await reader.ReadLineAsync(ct) ?? throw new EndOfStreamException();
        Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadLineAsync(ct) is { Length: > 0 } line)
        {
            int separator = line.IndexOf(':');
            headers.Add(line[..separator], line[(separator + 1)..].Trim());
        }
        var body = new char[int.Parse(headers["Content-Length"], CultureInfo.InvariantCulture)];
        Assert.Equal(body.Length, await reader.ReadBlockAsync(body.AsMemory(), ct));
        return new(start, headers, new string(body));
    }

    private sealed record Request(string Line, Dictionary<string, string> Headers, string Body);
    private sealed record Response(int Status, string Body = "", string Headers = "");

    private sealed class HubServer : IDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly X509Certificate2? certificate;
        private readonly List<Stream> connections = [];

        internal HubServer(X509Certificate2? certificate = null)
        {
            this.certificate = certificate;
            listener.Start();
        }

        internal int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
        internal bool HasPendingConnection => listener.Pending();
        internal ValueTask<TcpClient> AcceptAsync(CancellationToken ct) => listener.AcceptTcpClientAsync(ct);

        internal async Task<Request[]> ServeAsync(CancellationToken ct, params Response[] responses)
        {
            var requests = new Request[responses.Length];
            for (int i = 0; i < responses.Length; i++)
            {
                var socket = await listener.AcceptSocketAsync(ct);
                var network = new NetworkStream(socket, ownsSocket: true);
                Stream stream = certificate == null ? network : new SslStream(network);
                connections.Add(stream);
                if (stream is SslStream tls)
                    await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, ct);
                requests[i] = await ReadRequest(stream, ct);
                var response = responses[i];
                var length = Encoding.UTF8.GetByteCount(response.Body);
                var output = $"HTTP/1.1 {response.Status} Fixture\r\nContent-Type: application/json\r\nContent-Length: {length}\r\n" +
                    $"Connection: close\r\n{response.Headers}\r\n{response.Body}";
                await stream.WriteAsync(Encoding.UTF8.GetBytes(output), ct);
            }
            return requests;
        }

        public void Dispose()
        {
            listener.Stop();
            // FetchAsync has consumed the responses before the test releases the server sockets.
            foreach (var stream in connections) stream.Dispose();
        }
    }

    private sealed class HubFiles : IDisposable
    {
        private readonly TempDirectory directory = new("QuotaGadget hub fixture ");
        private readonly CancellationTokenSource deadline = new(TimeSpan.FromSeconds(12));
        private int count;

        internal CancellationToken Token => deadline.Token;

        internal FileInfo Hub(int httpPort, int? httpsPort = null, int? pid = null, string token = Csrf) =>
            Write(JsonSerializer.Serialize(new { pid = pid ?? Environment.ProcessId, httpPort, httpsPort, csrfToken = token }));

        internal FileInfo Write(string contents) => new(directory.WriteFile($"hub_{++count}.json", contents));

        public void Dispose()
        {
            deadline.Cancel();
            deadline.Dispose();
            directory.Dispose();
        }
    }
}
