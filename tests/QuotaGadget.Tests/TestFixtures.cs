using System.Net;

namespace QuotaGadget.Tests;

internal sealed class TempDirectory(string? prefix = null) : IDisposable
{
    private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory(prefix);

    internal string FullName => directory.FullName;
    internal string PathFor(string name) => Path.GetFullPath(name, FullName);
    internal DirectoryInfo CreateSubdirectory(string name) => directory.CreateSubdirectory(name);

    internal string WriteFile(string name, string contents)
    {
        string path = Path.GetFullPath(PathFor(name));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        return path;
    }

    internal string WriteScript(string name, string contents) => WriteFile(name, contents.ReplaceLineEndings("\r\n") + "\r\n");

    public void Dispose() => directory.Delete(recursive: true);
}

internal sealed class OwnedProfile : IDisposable
{
    internal Account Account { get; }

    internal OwnedProfile(Provider provider)
    {
        Account = new(provider, Path.Combine(Config.Profiles, Guid.NewGuid().ToString("N")));
        Config.CheckOwnedPath(Account.ProfileDir);
        Directory.CreateDirectory(Account.ProfileDir);
    }

    internal string PathFor(string name) => Path.Combine(Account.ProfileDir, name);

    public void Dispose()
    {
        Config.CheckOwnedPath(Account.ProfileDir);
        Directory.Delete(Account.ProfileDir, recursive: true);
    }
}

internal sealed class MockHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
    : HttpMessageHandler
{
    internal MockHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : this((request, _) => Task.FromResult(respond(request))) { }

    internal static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => respond(request, ct);
}
