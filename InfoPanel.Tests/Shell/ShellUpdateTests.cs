using System.Net;
using System.Text;
using InfoPanel.Hosting;
using InfoPanel.Shell;
using InfoPanel.Updates;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace InfoPanel.Tests.Shell;

/// <summary>
/// The menu's update line on the engine host, with a <see cref="FakeShellPlatform"/>, a browser launcher that only
/// records, and an update check of a <c>2.0.0-beta.0</c> build over a feed answered in memory: Check for Updates… and its
/// three answers through the platform's prompt, Download and the Update Available line opening the release page built
/// from the tag, and no line at all for a development build.
/// </summary>
public sealed class ShellUpdateTests : IDisposable
{
    private const string Version = "2.0.0-beta.0";
    private const string Page = "https://github.com/habibrehmansg/infopanel/releases/tag/";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly EngineFactory _base = new();
    private readonly FakeShellPlatform _shell = new();
    private readonly RecordingBrowserLauncher _browser = new();
    private readonly MemoryFeed _feed = new();
    private readonly WebApplicationFactory<Program> _factory;

    public ShellUpdateTests()
    {
        _factory = _base.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IShellPlatform>(sp =>
            {
                _shell.Lifetime = sp.GetRequiredService<IHostApplicationLifetime>();
                return _shell;
            });
            services.AddSingleton<BrowserLauncher>(_browser);
            // A loopback feed: test engines run --offline. The handler answers in memory; nothing leaves the process.
            services.AddSingleton(new UpdateCheck(Version, "http://127.0.0.1:9/releases.json", offline: true, handler: _feed));
        }));
        _ = _factory.Services;
    }

    public void Dispose()
    {
        _factory.Dispose();
        _base.Dispose();
    }

    private static string Releases(params string[] tags) =>
        "[" + string.Join(",", tags.Select(t => $"{{\"tag_name\":\"{t}\",\"draft\":false,\"prerelease\":true,\"html_url\":\"https://example.test/elsewhere\"}}")) + "]";

    private ShellMenuItem UpdateLine() => _shell.OpenMenu().Items[2];

    [Fact]
    public async Task CheckForUpdates_FindsANewerVersion_AsksDownloadOrLater_AndDownloadOpensItsReleasePage()
    {
        await WaitUntil(() => _shell.Icon != null);
        Assert.Equal((ShellMenu.CheckForUpdates, "Check for Updates…"), (UpdateLine().Id, UpdateLine().Label));
        _feed.Body = Releases("2.0.0-beta.1", "1.3.1");
        _shell.Answer(0);

        await _shell.ClickAsync(ShellMenu.CheckForUpdates);

        var prompt = Assert.Single(_shell.Prompts);
        Assert.Equal("You have InfoPanel 2.0.0-beta.0. Download opens its release page.", prompt.Text);
        Assert.Equal("InfoPanel 2.0.0-beta.1 is available", prompt.Title);
        Assert.Equal(["Download", "Later"], prompt.Buttons);
        Assert.Equal((0, 1), (prompt.DefaultButton, prompt.CancelButton));
        Assert.Equal([Page + "2.0.0-beta.1"], _browser.Opened);
        Assert.Equal(1, _feed.Count);
    }

    [Fact]
    public async Task Later_OpensNothing_AndTheMenuThenSaysUpdateAvailable_WhichOpensTheReleasePage()
    {
        await WaitUntil(() => _shell.Icon != null);
        _feed.Body = Releases("2.0.0-beta.1");
        _shell.Answer(1);

        await _shell.ClickAsync(ShellMenu.CheckForUpdates);

        Assert.Empty(_browser.Opened);
        Assert.Equal((ShellMenu.UpdateAvailable, "Update Available: InfoPanel 2.0.0-beta.1…"), (UpdateLine().Id, UpdateLine().Label));
        Assert.DoesNotContain(_shell.OpenMenu().Items, i => i.Id == ShellMenu.CheckForUpdates);

        await _shell.ClickAsync(ShellMenu.UpdateAvailable);

        Assert.Equal([Page + "2.0.0-beta.1"], _browser.Opened);
        Assert.Single(_shell.Prompts);
    }

    [Fact]
    public async Task CheckForUpdates_WithNothingNewer_SaysYoureUpToDate()
    {
        await WaitUntil(() => _shell.Icon != null);
        _feed.Body = Releases(Version, "2.0.0-alpha.3", "1.4.0-preview.4");

        await _shell.ClickAsync(ShellMenu.CheckForUpdates);

        var prompt = Assert.Single(_shell.Prompts);
        Assert.Equal(("You're up to date", "InfoPanel 2.0.0-beta.0 is the newest version."), (prompt.Title, prompt.Text));
        Assert.Equal(["OK"], prompt.Buttons);
        Assert.Empty(_browser.Opened);
        Assert.Equal(ShellMenu.CheckForUpdates, UpdateLine().Id);
    }

    [Fact]
    public async Task CheckForUpdates_WhenTheFeedCantBeRead_SaysCouldntCheck()
    {
        await WaitUntil(() => _shell.Icon != null);
        _feed.Body = null;

        await _shell.ClickAsync(ShellMenu.CheckForUpdates);

        var prompt = Assert.Single(_shell.Prompts);
        Assert.Equal(("Couldn't check for updates", "Check your connection, then try again."), (prompt.Title, prompt.Text));
        Assert.Equal(["OK"], prompt.Buttons);
        Assert.Empty(_browser.Opened);
        Assert.Equal(1, _feed.Count);
    }

    /// <summary>A test engine is a development build: its menu has no update line, and it sends nothing.</summary>
    [Fact]
    public async Task ADevelopmentBuild_HasNoUpdateLine_AndItsCheckIsOff()
    {
        using var engine = new EngineFactory();
        var shell = new FakeShellPlatform();
        using var factory = engine.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.AddSingleton<IShellPlatform>(shell)));
        _ = factory.Services;
        await WaitUntil(() => shell.Icon != null);

        Assert.DoesNotContain(shell.OpenMenu().Items, i => i.Id is ShellMenu.CheckForUpdates or ShellMenu.UpdateAvailable);
        var check = factory.Services.GetRequiredService<UpdateCheck>();
        Assert.False(check.Enabled);
        Assert.Equal(UpdateCheckResult.Off, await check.CheckNowAsync(Ct));
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out");
            await Task.Delay(10, Ct);
        }
    }

    /// <summary>A feed answered in memory: <see cref="Body"/> with a 200, or a refused connection while it is null.</summary>
    private sealed class MemoryFeed : HttpMessageHandler
    {
        private int _count;

        public string? Body { get; set; }

        public int Count => Volatile.Read(ref _count);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Yield();
            Interlocked.Increment(ref _count);
            return Body is { } body
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") }
                : throw new HttpRequestException("Connection refused");
        }
    }
}
