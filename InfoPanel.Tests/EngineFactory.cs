using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace InfoPanel.Tests;

/// <summary>
/// The engine on a <c>TestServer</c>, with a temp data folder, a temp web root holding an <c>index.html</c>,
/// a fixed token, port and dev origin, and seeded mock sensors.
/// </summary>
public sealed class EngineFactory : WebApplicationFactory<Program>
{
    public const string Token = "test-token_0123456789";
    public const int Port = 47123;
    public const string DevOrigin = "http://localhost:5180";
    public const string IndexMarker = "client-test-index";

    public EngineFactory()
    {
        Root = Path.Combine(Path.GetTempPath(), "infopanel-engine-tests-" + Guid.NewGuid().ToString("N"));
        DataDir = Path.Combine(Root, "data");
        WebRoot = Path.Combine(Root, "web");
        Directory.CreateDirectory(Path.Combine(WebRoot, "assets"));
        File.WriteAllText(Path.Combine(WebRoot, "index.html"), $"<!doctype html><title>{IndexMarker}</title>");
        File.WriteAllText(Path.Combine(WebRoot, "assets", "app.js"), "console.log('web-client');");
    }

    public string Root { get; }

    public string DataDir { get; }

    public string WebRoot { get; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Engine:DataDir", DataDir);
        builder.UseSetting("Engine:WebRoot", WebRoot);
        builder.UseSetting("Engine:Token", Token);
        builder.UseSetting("Engine:Port", Port.ToString());
        builder.UseSetting("Engine:NoBrowser", "true");
        builder.UseSetting("Engine:MockSeed", "42");
        builder.UseSetting("Engine:DevOrigins:0", DevOrigin);
    }

    /// <summary>A client that doesn't follow redirects or keep cookies.</summary>
    public HttpClient CreateRawClient() =>
        CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });

    /// <summary>A raw client that sends the token as a Bearer header.</summary>
    public HttpClient CreateApiClient()
    {
        var client = CreateRawClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        return client;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            try { Directory.Delete(Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
