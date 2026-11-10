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
/// from the tag, and no line at all for a development build. And the notification: one per new version, across
/// restarts on the same data folder, none when up to date, for a development build, when the mark can't be written or
/// for Check for Updates… (its prompt answers), its click opening the newest notified version's release page and nothing
/// else.
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
    private readonly SteppingTime _time = new();
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
            services.AddSingleton(new UpdateCheck(Version, "http://127.0.0.1:9/releases.json", offline: true, _time, handler: _feed));
        }));
        _ = _factory.Services;
    }

    /// <summary>An engine of a <see cref="Version"/> build on <paramref name="engine"/>'s data folder, over <paramref name="feed"/>.</summary>
    private static WebApplicationFactory<Program> Engine(EngineFactory engine, FakeShellPlatform shell, RecordingBrowserLauncher browser, MemoryFeed feed)
    {
        var factory = engine.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IShellPlatform>(shell);
            services.AddSingleton<BrowserLauncher>(browser);
            services.AddSingleton(new UpdateCheck(Version, "http://127.0.0.1:9/releases.json", offline: true, handler: feed));
        }));
        _ = factory.Services;
        return factory;
    }

    private const string Click = "Click to see what's new and download it.";

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

    [Fact]
    public async Task ANewVersion_IsNotifiedOnce_AndNotAgainAfterARestart_ButANewerOneIs()
    {
        using var engine = new EngineFactory();
        var feed = new MemoryFeed { Body = Releases("2.0.0-beta.1") };
        var first = new FakeShellPlatform();
        var browser = new RecordingBrowserLauncher();
        using (var factory = Engine(engine, first, browser, feed))
        {
            await WaitUntil(() => first.Icon != null);

            Assert.Equal(UpdateCheckResult.Available, await factory.Services.GetRequiredService<UpdateCheck>().CheckNowAsync(Ct));

            var note = Assert.Single(first.Notifications);
            Assert.Equal(("InfoPanel 2.0.0-beta.1 is available", Click), (note.Title, note.Text));
            Assert.Equal("""{"format":"infopanel.updates/1","notified":"2.0.0-beta.1"}""", File.ReadAllText(Path.Combine(engine.DataDir, "updates.json")));
        }

        await engine.WaitForTheFolderAsync(Ct);
        var second = new FakeShellPlatform();
        using (var factory = Engine(engine, second, browser, feed))
        {
            await WaitUntil(() => second.Icon != null);
            var check = factory.Services.GetRequiredService<UpdateCheck>();

            Assert.Equal(UpdateCheckResult.Available, await check.CheckNowAsync(Ct));
            Assert.Equal("2.0.0-beta.1", check.Latest?.Version);
            Assert.Empty(second.Notifications);
        }

        await engine.WaitForTheFolderAsync(Ct);
        feed.Body = Releases("2.0.0-beta.2", "2.0.0-beta.1");
        var third = new FakeShellPlatform();
        using (var factory = Engine(engine, third, browser, feed))
        {
            await WaitUntil(() => third.Icon != null);

            Assert.Equal(UpdateCheckResult.Available, await factory.Services.GetRequiredService<UpdateCheck>().CheckNowAsync(Ct));

            Assert.Equal("InfoPanel 2.0.0-beta.2 is available", Assert.Single(third.Notifications).Title);
        }

        Assert.Empty(browser.Opened);
        Assert.Equal(3, feed.Count);
    }

    [Fact]
    public async Task UpToDate_NotifiesNothing_UntilANewerVersionIsOut()
    {
        await WaitUntil(() => _shell.Icon != null);
        var check = _factory.Services.GetRequiredService<UpdateCheck>();
        _feed.Body = Releases(Version, "2.0.0-alpha.3");

        Assert.Equal(UpdateCheckResult.UpToDate, await check.CheckNowAsync(Ct));
        Assert.Empty(_shell.Notifications);
        Assert.False(File.Exists(Path.Combine(_base.DataDir, "updates.json")));

        _feed.Body = Releases("2.0.0-beta.1", Version);
        _time.Advance(UpdateCheck.ManualSpacing);
        Assert.Equal(UpdateCheckResult.Available, await check.CheckNowAsync(Ct));

        Assert.Equal("InfoPanel 2.0.0-beta.1 is available", Assert.Single(_shell.Notifications).Title);
    }

    /// <summary>A test engine is a development build: its check is off, so nothing is ever notified.</summary>
    [Fact]
    public async Task ADevelopmentBuild_NotifiesNothing()
    {
        using var engine = new EngineFactory();
        var shell = new FakeShellPlatform();
        var feed = new MemoryFeed { Body = Releases("9.0.0") };
        using var factory = engine.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IShellPlatform>(shell);
            services.AddSingleton(new UpdateCheck("2.0.0-dev", "http://127.0.0.1:9/releases.json", offline: true, handler: feed));
        }));
        _ = factory.Services;
        await WaitUntil(() => shell.Icon != null);

        Assert.Equal(UpdateCheckResult.Off, await factory.Services.GetRequiredService<UpdateCheck>().CheckNowAsync(Ct));

        Assert.Empty(shell.Notifications);
        Assert.Equal(0, feed.Count);
        Assert.False(File.Exists(Path.Combine(engine.DataDir, "updates.json")));
    }

    /// <summary>
    /// macOS keeps one notification under one identifier and Windows one balloon, so only the newest notification's click
    /// is live: it opens that notification's version's release page, which with the high-water mark is the newest
    /// version notified.
    /// </summary>
    [Fact]
    public async Task ANotificationsClick_OpensTheNewestNotifiedVersionsReleasePage_AndNothingElse()
    {
        await WaitUntil(() => _shell.Icon != null);
        var check = _factory.Services.GetRequiredService<UpdateCheck>();
        _feed.Body = Releases("2.0.0-beta.1");
        await check.CheckNowAsync(Ct);
        _feed.Body = Releases("2.0.0-beta.2", "2.0.0-beta.1");
        _time.Advance(UpdateCheck.ManualSpacing);
        await check.CheckNowAsync(Ct);
        Assert.Equal(["InfoPanel 2.0.0-beta.1 is available", "InfoPanel 2.0.0-beta.2 is available"], _shell.Notifications.Select(n => n.Title));
        Assert.Empty(_browser.Opened);

        await _shell.ClickNotificationAsync();

        var link = Assert.Single(_browser.Links);
        Assert.Equal((Page + "2.0.0-beta.2", false), (link.Url, link.WebClient));
        Assert.Empty(_shell.Prompts);
        Assert.Empty(_shell.Copied);
        Assert.Empty(_base.Folders.Opened);
        Assert.False(_shell.Removed);
        Assert.False(_factory.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested);
        Assert.Equal(2, _feed.Count);
        Assert.Equal(2, _shell.Notifications.Count);
    }

    /// <summary>
    /// Check for Updates… answers in its prompt only, with no notification as well; the version is still remembered, so
    /// the background check doesn't notify it later. A newer version the background check finds is notified.
    /// </summary>
    [Fact]
    public async Task CheckForUpdates_AnswersInItsPromptOnly_AndRemembersTheVersion_WhileTheBackgroundCheckNotifies()
    {
        await WaitUntil(() => _shell.Icon != null);
        var check = _factory.Services.GetRequiredService<UpdateCheck>();
        _feed.Body = Releases("2.0.0-beta.1");

        await _shell.ClickAsync(ShellMenu.CheckForUpdates);

        Assert.Equal("InfoPanel 2.0.0-beta.1 is available", Assert.Single(_shell.Prompts).Title);
        Assert.Empty(_shell.Notifications);
        Assert.Equal("""{"format":"infopanel.updates/1","notified":"2.0.0-beta.1"}""", File.ReadAllText(Path.Combine(_base.DataDir, "updates.json")));
        Assert.False(_factory.Services.GetRequiredService<UpdateNotices>().ShouldNotify("2.0.0-beta.1"));

        _feed.Body = Releases("2.0.0-beta.2", "2.0.0-beta.1");
        _time.Advance(UpdateCheck.ManualSpacing);
        Assert.Equal(UpdateCheckResult.Available, await check.CheckNowAsync(Ct));

        Assert.Equal("InfoPanel 2.0.0-beta.2 is available", Assert.Single(_shell.Notifications).Title);
        Assert.Single(_shell.Prompts);
        Assert.Empty(_browser.Opened);
    }

    /// <summary>
    /// The mark is written before the notification is shown: when <c>updates.json</c> can't be written, nothing is shown
    /// for that version (at most once, ever). The menu's update line still says it.
    /// </summary>
    [Fact]
    public async Task WhenTheMarkCantBeWritten_NothingIsNotified_AndTheMenuStillSaysUpdateAvailable()
    {
        await WaitUntil(() => _shell.Icon != null);
        var check = _factory.Services.GetRequiredService<UpdateCheck>();
        Directory.CreateDirectory(Path.Combine(_base.DataDir, "updates.json"));
        _feed.Body = Releases("2.0.0-beta.1");

        Assert.Equal(UpdateCheckResult.Available, await check.CheckNowAsync(Ct));

        Assert.Empty(_shell.Notifications);
        Assert.Equal((ShellMenu.UpdateAvailable, "Update Available: InfoPanel 2.0.0-beta.1…"), (UpdateLine().Id, UpdateLine().Label));
        Assert.True(Directory.Exists(Path.Combine(_base.DataDir, "updates.json")));
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

    /// <summary>The system's clock, its timestamps moved on by <see cref="Advance"/> (past the manual checks' spacing).</summary>
    private sealed class SteppingTime : TimeProvider
    {
        private long _offset;

        public void Advance(TimeSpan by) => Interlocked.Add(ref _offset, (long)(by.TotalSeconds * TimestampFrequency) + 1);

        public override long GetTimestamp() => base.GetTimestamp() + Interlocked.Read(ref _offset);
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
