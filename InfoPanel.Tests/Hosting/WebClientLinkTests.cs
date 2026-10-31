using InfoPanel.Hosting;
using InfoPanel.Shell;
using InfoPanel.Tests.Shell;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace InfoPanel.Tests.Hosting;

/// <summary>
/// The configured web client: the origin a release build carries (the default for <c>--allow-origin</c>, none in a build
/// from source), the link the engine opens when asked (that origin's pairing link when it serves no web client and allows
/// exactly one cross origin, else the sign-in link), and that it never opens a browser by itself.
/// </summary>
public sealed class WebClientLinkTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static EngineOptions? Load(string? builtIn, out IReadOnlyList<string> errors, params string[] args)
    {
        var parsed = EngineCommandLine.Parse(args);
        Assert.Null(parsed.Error);
        return EngineOptions.Load(new ConfigurationBuilder().AddInMemoryCollection(parsed.Settings).Build(), out errors, builtIn);
    }

    // ---- the origin a build carries -------------------------------------------------------------------------------

    [Fact]
    public void ABuildFromSource_CarriesNoOrigin_AndAllowsNone()
    {
        Assert.Null(EngineOptions.BuiltInAllowOrigin);
        var options = Load(EngineOptions.BuiltInAllowOrigin, out var errors);
        Assert.Empty(errors);
        Assert.Empty(options!.AllowOrigins);
        Assert.False(options.AllowOriginsBuiltIn);
    }

    [Fact]
    public void TheOriginABuildCarries_IsAllowed_WhenNoFlagIsGiven()
    {
        var options = Load("https://Client.Example/", out var errors);

        Assert.Empty(errors);
        Assert.Equal(["https://client.example"], options!.AllowOrigins);
        Assert.True(options.AllowOriginsBuiltIn);
    }

    [Fact]
    public void AFlag_ReplacesTheOriginABuildCarries()
    {
        var options = Load("https://client.example", out var errors, "--allow-origin", "http://localhost:5180");

        Assert.Empty(errors);
        Assert.Equal(["http://localhost:5180"], options!.AllowOrigins);
        Assert.False(options.AllowOriginsBuiltIn);
    }

    [Theory]
    [InlineData("http://client.example")]
    [InlineData("https://client.example/path")]
    [InlineData("https://*.example")]
    [InlineData("not an origin")]
    public void ABadOriginInTheBuild_StopsStartUp(string builtIn)
    {
        Assert.Null(Load(builtIn, out var errors));
        Assert.Contains(errors, e => e.Contains("InfoPanelAllowOrigin") && e.Contains(builtIn));
    }

    [Fact]
    public void TheLaunchRecord_LeavesOutTheOriginABuildCarries_ButKeepsAFlag()
    {
        Assert.DoesNotContain("--allow-origin", LaunchRecord.ArgumentsOf(Load("https://client.example", out _)!));
        Assert.Contains("--allow-origin", LaunchRecord.ArgumentsOf(Load("https://client.example", out _, "--allow-origin", "https://other.example")!));
    }

    // ---- the link ---------------------------------------------------------------------------------------------------

    [Fact]
    public void WithNoWebRoot_AndOneCrossOrigin_TheLinkIsItsPairingLink()
    {
        var runtime = new EngineRuntime(5300, "tok", [], ["https://client.example"]) { WebRootServed = false };

        Assert.Equal(new EditorLink("https://client.example/#engine=http://127.0.0.1:5300&token=tok", WebClient: true), runtime.EditorLink);
    }

    [Fact]
    public void WithAWebRoot_OrNoneOrTwoCrossOrigins_TheLinkIsTheSignInLink()
    {
        var signIn = new EditorLink("http://127.0.0.1:5300/auth/tok", WebClient: false);
        Assert.Equal(signIn, new EngineRuntime(5300, "tok", [], ["https://client.example"]) { WebRootServed = true }.EditorLink);
        Assert.Equal(signIn, new EngineRuntime(5300, "tok", ["http://localhost:5180"], []) { WebRootServed = false }.EditorLink);
        Assert.Equal(signIn, new EngineRuntime(5300, "tok", [], ["https://a.example", "https://b.example"]) { WebRootServed = false }.EditorLink);
    }

    // ---- on the engine host -------------------------------------------------------------------------------------------

    /// <summary>The test engine with no web root: its one cross origin (<see cref="EngineFactory.CrossOrigin"/>) is the client.</summary>
    private static WebApplicationFactory<Program> WithoutWebRoot(EngineFactory engine, RecordingBrowserLauncher browser, bool noBrowser = true) =>
        engine.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Engine:WebRoot", "");
            builder.UseSetting("Engine:NoBrowser", noBrowser ? "true" : "false");
            builder.ConfigureTestServices(services => services.AddSingleton<BrowserLauncher>(browser));
        });

    [Fact]
    public async Task TheEngine_OpensNoBrowserAtStart_EvenWithoutNoBrowser()
    {
        using var engine = new EngineFactory();
        var browser = new RecordingBrowserLauncher();
        using var factory = WithoutWebRoot(engine, browser, noBrowser: false);
        using var api = factory.CreateClient();
        api.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", EngineFactory.Token);

        using (var response = await api.GetAsync("/api/session", Ct))
            response.EnsureSuccessStatusCode();
        await WaitForLog(engine, "Pair the client");
        await Task.Delay(200, Ct);

        Assert.Empty(browser.Links);
    }

    [Fact]
    public void OpenEditor_WithoutAWebRoot_OpensTheWebClientsPairingLink()
    {
        using var engine = new EngineFactory();
        var browser = new RecordingBrowserLauncher();
        using var factory = WithoutWebRoot(engine, browser);

        factory.Services.GetRequiredService<EditorOpener>().Open();

        Assert.Equal([new EditorLink($"{EngineFactory.CrossOrigin}/#engine=http://127.0.0.1:{EngineFactory.Port}&token={EngineFactory.Token}", WebClient: true)],
            browser.Links);
    }

    [Fact]
    public async Task WithoutAWebRoot_TheFileLogHasThePairingLinkRedacted_AndNoSignInLink()
    {
        using var engine = new EngineFactory();
        using var factory = WithoutWebRoot(engine, new RecordingBrowserLauncher());
        _ = factory.Services;

        var text = await WaitForLog(engine, "Pair the client");

        Assert.Contains($"{EngineFactory.CrossOrigin}/#engine=http://127.0.0.1:{EngineFactory.Port}&token=<token>", text);
        // The engine's own sign-in link leads nowhere without a web root; a dev server's still does.
        Assert.DoesNotContain($"http://127.0.0.1:{EngineFactory.Port}/auth/", text);
        Assert.DoesNotContain("Open the editor: ", text);
        Assert.Contains($"{EngineFactory.DevOrigin}/auth/<token>", text);
        Assert.DoesNotContain(EngineFactory.Token, text);
    }

    private static async Task<string> WaitForLog(EngineFactory engine, string marker)
    {
        var logs = Path.Combine(engine.DataDir, "logs");
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        var text = "";
        while (DateTime.UtcNow < deadline)
        {
            text = string.Join("\n", Directory.Exists(logs) ? Directory.GetFiles(logs).Select(ReadShared) : []);
            if (text.Contains(marker))
                return text;
            await Task.Delay(20, Ct);
        }

        Assert.Fail($"The log never said '{marker}': {text}");
        return text;
    }

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
