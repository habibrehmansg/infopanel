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

    // ---- the link ---------------------------------------------------------------------------------------------------

    [Fact]
    public void WithNoWebRoot_AndOneCrossOrigin_TheLinkIsItsCodeLink_WithNoToken()
    {
        var runtime = new EngineRuntime(5300, "tok", [], ["https://client.example"]) { WebRootServed = false };

        Assert.Equal(new EditorLink("https://client.example/#engine=http://127.0.0.1:5300&code=q3Zt0vK8Lw2NxR5yB7mC1A", WebClient: true),
            runtime.EditorLinkFor(() => "q3Zt0vK8Lw2NxR5yB7mC1A"));
    }

    [Fact]
    public void WithAWebRoot_OrNoneOrTwoCrossOrigins_TheLinkIsTheSignInLink_AndNoCodeIsMinted()
    {
        var signIn = new EditorLink("http://127.0.0.1:5300/auth/tok", WebClient: false);
        string NoCode() => throw new InvalidOperationException("No code is minted for the sign-in link.");
        Assert.Equal(signIn, new EngineRuntime(5300, "tok", [], ["https://client.example"]) { WebRootServed = true }.EditorLinkFor(NoCode));
        Assert.Equal(signIn, new EngineRuntime(5300, "tok", ["http://localhost:5180"], []) { WebRootServed = false }.EditorLinkFor(NoCode));
        Assert.Equal(signIn, new EngineRuntime(5300, "tok", [], ["https://a.example", "https://b.example"]) { WebRootServed = false }.EditorLinkFor(NoCode));
    }

    /// <summary>
    /// A link to the client's sign-in settings (the menu's Edit from Another Computer… and its row of who is signed in)
    /// carries it as <c>then</c>, URL-encoded: after the code link's members, or as the sign-in link's query, which its
    /// redirect hands on after the token. It is the only page the engine knows.
    /// </summary>
    [Fact]
    public void ALinkToTheSignInSettings_CarriesItAsThen_InTheCodeLink_AndTheSignInLink()
    {
        var code = new EngineRuntime(5300, "tok", [], ["https://client.example"]) { WebRootServed = false };
        var signIn = new EngineRuntime(5300, "tok", [], []) { WebRootServed = true };

        Assert.Equal(
            new EditorLink("https://client.example/#engine=http://127.0.0.1:5300&code=q3Zt0vK8Lw2NxR5yB7mC1A&then=%23%2Fsettings%2Faccount", WebClient: true),
            code.EditorLinkFor(() => "q3Zt0vK8Lw2NxR5yB7mC1A", EditorRoutes.AccountSettings));
        Assert.Equal(new EditorLink("http://127.0.0.1:5300/auth/tok?then=%23%2Fsettings%2Faccount", WebClient: false),
            signIn.EditorLinkFor(() => throw new InvalidOperationException(), EditorRoutes.AccountSettings));
        Assert.Equal("#/settings/account", EditorRoutes.AccountSettings);
        Assert.Equal([EditorRoutes.AccountSettings], EditorRoutes.Known);
    }

    /// <summary>The page is one the engine knows, never a caller's: any other is refused before a code is minted.</summary>
    [Theory]
    [InlineData("#/profiles")]
    [InlineData("#/settings/engine")]
    [InlineData("https://elsewhere.example/")]
    [InlineData("#/settings/account&token=x")]
    [InlineData("")]
    public void ALinkToAPageTheEngineDoesntKnow_IsRefused(string then)
    {
        var runtime = new EngineRuntime(5300, "tok", [], ["https://client.example"]) { WebRootServed = false };
        var minted = 0;

        Assert.Throws<ArgumentException>(() => runtime.EditorLinkFor(() => $"{++minted}", then));
        Assert.Equal(0, minted);
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

    /// <summary>
    /// Open Editor mints a code each time and opens the web client's code link, never the session token; a code from an
    /// opened link exchanges once.
    /// </summary>
    [Fact]
    public async Task OpenEditor_WithoutAWebRoot_OpensACodeLink_ANewCodeEachTime_WithNoToken()
    {
        using var engine = new EngineFactory();
        var browser = new RecordingBrowserLauncher();
        using var factory = WithoutWebRoot(engine, browser);

        factory.Services.GetRequiredService<EditorOpener>().Open();
        factory.Services.GetRequiredService<EditorOpener>().Open();

        Assert.Equal(2, browser.Links.Count);
        var codes = new List<string>();
        foreach (var link in browser.Links)
        {
            Assert.True(link.WebClient);
            Assert.DoesNotContain(EngineFactory.Token, link.Url);
            var match = System.Text.RegularExpressions.Regex.Match(link.Url,
                $"^{System.Text.RegularExpressions.Regex.Escape($"{EngineFactory.CrossOrigin}/#engine=http://127.0.0.1:{EngineFactory.Port}&code=")}([A-Za-z0-9_-]{{22}})$");
            Assert.True(match.Success, link.Url);
            codes.Add(match.Groups[1].Value);
        }

        Assert.NotEqual(codes[0], codes[1]);
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = false });
        HttpContent Exchange() => new StringContent(System.Text.Json.JsonSerializer.Serialize(new { code = codes[0], name = "Chrome" }), System.Text.Encoding.UTF8, "application/json");
        using (var first = await client.PostAsync("/auth/pairing/code", Exchange(), Ct))
            Assert.Equal(System.Net.HttpStatusCode.Created, first.StatusCode);
        using (var second = await client.PostAsync("/auth/pairing/code", Exchange(), Ct))
            Assert.Equal(System.Net.HttpStatusCode.Forbidden, second.StatusCode);
    }

    /// <summary>
    /// The menu's Edit from Another Computer… opens the web client's code link with the sign-in settings, where another
    /// computer is set up to edit this one, as its <c>then</c>, after a new code; Open Editor's has none.
    /// </summary>
    [Fact]
    public async Task EditFromAnotherComputer_WithoutAWebRoot_OpensACodeLink_ThatLandsOnThePageForAnotherComputer()
    {
        using var engine = new EngineFactory();
        var browser = new RecordingBrowserLauncher();
        var shell = new FakeShellPlatform();
        using var factory = engine.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Engine:WebRoot", "");
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<BrowserLauncher>(browser);
                services.AddSingleton<IShellPlatform>(shell);
            });
        });
        _ = factory.Services;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (shell.Icon == null && DateTime.UtcNow < deadline)
            await Task.Delay(20, Ct);

        await shell.ClickAsync(ShellMenu.EditFromAnotherComputer);
        await shell.ClickAsync(ShellMenu.OpenEditor);

        Assert.Equal(2, browser.Links.Count);
        Assert.All(browser.Links, l => Assert.True(l.WebClient));
        var prefix = System.Text.RegularExpressions.Regex.Escape($"{EngineFactory.CrossOrigin}/#engine=http://127.0.0.1:{EngineFactory.Port}&code=");
        Assert.Matches($"^{prefix}[A-Za-z0-9_-]{{22}}&then=%23%2Fsettings%2Faccount$", browser.Links[0].Url);
        Assert.Matches($"^{prefix}[A-Za-z0-9_-]{{22}}$", browser.Links[1].Url);
        Assert.DoesNotContain(EngineFactory.Token, browser.Links[0].Url);
    }

    /// <summary>
    /// What the browser is handed is a private redirect file: it holds the code link and no session token. The launcher's
    /// process start is replaced, so nothing opens.
    /// </summary>
    [Fact]
    public void OpenEditor_WithoutAWebRoot_WritesTheCodeLinkIntoTheRedirectFile_AndNoToken()
    {
        using var engine = new EngineFactory();
        var started = new List<System.Diagnostics.ProcessStartInfo>();
        using var factory = engine.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Engine:WebRoot", "");
            builder.ConfigureTestServices(services => services.AddSingleton(sp => new BrowserLauncher(
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<BrowserLauncher>>(), sp.GetRequiredService<RedirectFiles>(),
                start => { started.Add(start); return 0; }, findChrome: () => "/Applications/Google Chrome.app", os: BrowserOS.MacOS)));
        });

        factory.Services.GetRequiredService<EditorOpener>().Open();

        var file = Assert.Single(started.SelectMany(s => s.ArgumentList).Where(a => a.EndsWith(".html", StringComparison.Ordinal)));
        var html = File.ReadAllText(file);
        Assert.Contains($"{EngineFactory.CrossOrigin}/#engine=http://127.0.0.1:{EngineFactory.Port}&amp;code=", html);
        Assert.DoesNotContain(EngineFactory.Token, html);
        Assert.DoesNotContain("token=", html);
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
