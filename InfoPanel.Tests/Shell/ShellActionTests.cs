using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using InfoPanel.Core.Tests.Images;
using InfoPanel.Desktop;
using InfoPanel.Documents;
using InfoPanel.Hosting;
using InfoPanel.Persistence;
using InfoPanel.Service;
using InfoPanel.Shell;
using InfoPanel.Tests.Desktop;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace InfoPanel.Tests.Shell;

/// <summary>
/// The full menu on the engine host (docs/2.0/desktop-shell.md, "The menu"), with a <see cref="FakeShellPlatform"/>, a
/// fake desktop (so desktop windows are on), the engine's recording folder opener and a recording browser launcher: what
/// the menu lists from the engine's state, and what each item does.
/// </summary>
public sealed class ShellActionTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly EngineFactory _base = new();
    private readonly FakeShellPlatform _shell = new();
    private readonly FakeDesktopPlatform _desktop = new(new DisplayInfo("main", new PointRect(0, 0, 2560, 1440), 1));
    private readonly RecordingBrowserLauncher _browser = new();
    private readonly WebApplicationFactory<Program> _factory;

    public ShellActionTests()
    {
        _factory = _base.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IShellPlatform>(sp =>
            {
                _shell.Lifetime = sp.GetRequiredService<IHostApplicationLifetime>();
                return _shell;
            });
            services.AddSingleton<IDesktopPlatform>(_desktop);
            services.AddSingleton<BrowserLauncher>(_browser);
        }));
        _ = _factory.Services;
    }

    public void Dispose()
    {
        _factory.Dispose();
        _base.Dispose();
        _desktop.Dispose();
    }

    private Workspace Workspace => _factory.Services.GetRequiredService<Workspace>();

    private async Task Shown() => await WaitUntil(() => _shell.Icon != null);

    private ShellMenuItem Item(string id) => Assert.Single(_shell.OpenMenu().Items, i => i.Id == id);

    private IReadOnlyList<ShellMenuItem> Desktop() => Item(ShellMenu.ShowOnDesktop).Children;

    /// <summary>A profile named <paramref name="name"/>, its window on or off, saved.</summary>
    private string Profile(string name, bool shown)
    {
        var doc = TestDocs.Doc(name, 100, 50);
        doc["profile"]!["window"] = new JsonObject { ["enabled"] = shown };
        return TestDocs.Create(Workspace, doc);
    }

    private ProfileDocument Doc(string id) => Workspace.Open(id)!;

    private static bool WindowOn(ProfileDocument doc) => doc.Current.Document.Profile.Window?.Enabled == true;

    private bool SavedWindowOn(string id)
    {
        var saved = JsonNode.Parse(File.ReadAllText(Path.Combine(_base.DataDir, "profiles", id, "document.json")))!;
        return saved["profile"]?["window"]?["enabled"]?.GetValue<bool>() == true;
    }

    [Fact]
    public async Task ShowOnDesktop_ListsTheEnginesProfiles_InTheLibrarysOrder_CheckedWhenTheirWindowIsOn()
    {
        await Shown();
        var on = Profile("Desk overlay", shown: true);
        var off = Profile("Neon core", shown: false);

        var listed = Desktop().Where(c => c.Label is "Desk overlay" or "Neon core").ToList();

        var expected = Workspace.List().Where(e => e.Id == on || e.Id == off).Select(e => e.Name).ToList();
        Assert.Equal(expected, listed.Select(c => c.Label));
        Assert.Equal(ShellMenu.DesktopItem(on, show: false), Assert.Single(listed, c => c.Label == "Desk overlay").Id);
        Assert.True(Assert.Single(listed, c => c.Label == "Desk overlay").Checked);
        Assert.Equal(ShellMenu.DesktopItem(off, show: true), Assert.Single(listed, c => c.Label == "Neon core").Id);
        Assert.False(Assert.Single(listed, c => c.Label == "Neon core").Checked);
    }

    [Fact]
    public async Task ShowOnDesktop_TurnsACleanProfilesWindowOn_AndSavesIt_AsOneUndoableChange()
    {
        await Shown();
        var id = Profile("Neon core", shown: false);
        var before = Doc(id).Rev;

        await _shell.ClickAsync(ShellMenu.DesktopItem(id, show: true));

        Assert.True(WindowOn(Doc(id)));
        Assert.Equal(before + 1, Doc(id).Rev);
        Assert.False(Workspace.Entry(id)!.Dirty);
        Assert.True(SavedWindowOn(id));
        Assert.True(Doc(id).Status.CanUndo);
        Assert.True(Assert.Single(Desktop(), c => c.Label == "Neon core").Checked);
    }

    [Fact]
    public async Task ShowOnDesktop_TurnsAWindowOff_AndSavesIt()
    {
        await Shown();
        var id = Profile("Desk overlay", shown: true);

        await _shell.ClickAsync(ShellMenu.DesktopItem(id, show: false));

        Assert.False(WindowOn(Doc(id)));
        Assert.False(Workspace.Entry(id)!.Dirty);
        Assert.False(SavedWindowOn(id));
    }

    [Fact]
    public async Task ShowOnDesktop_OnAProfileWithUnsavedChanges_LeavesTheToggleUnsaved_WithThem()
    {
        await Shown();
        var id = Profile("Neon core", shown: false);
        TestDocs.Commit(Doc(id), TestDocs.Profile(new JsonObject { ["width"] = 120 }));

        await _shell.ClickAsync(ShellMenu.DesktopItem(id, show: true));

        Assert.True(WindowOn(Doc(id)));
        Assert.True(Workspace.Entry(id)!.Dirty);
        Assert.False(SavedWindowOn(id));
        Assert.Equal(120, Doc(id).Current.Document.Profile.Width);
    }

    [Fact]
    public async Task ShowOnDesktop_KeepsTheRestOfTheWindow_AsTheDocumentHasIt()
    {
        await Shown();
        var doc = TestDocs.Doc("Neon core", 100, 50);
        doc["profile"]!["window"] = new JsonObject { ["enabled"] = false, ["x"] = 40, ["y"] = 30, ["level"] = "desktop" };
        var id = TestDocs.Create(Workspace, doc);

        await _shell.ClickAsync(ShellMenu.DesktopItem(id, show: true));

        var window = Doc(id).Current.Document.Profile.Window!;
        Assert.Equal((true, 40, 30, "desktop"), (window.Enabled, window.X, window.Y, window.Level));
    }

    [Fact]
    public async Task ShowOnDesktop_WhenTheWindowIsAlreadyAsAsked_ChangesNothing()
    {
        await Shown();
        var id = Profile("Desk overlay", shown: true);
        var before = Doc(id).Rev;

        // The menu said "show" (it was off when it opened); another client turned it on since.
        await _shell.ClickAsync(ShellMenu.DesktopItem(id, show: true));

        Assert.True(WindowOn(Doc(id)));
        Assert.Equal(before, Doc(id).Rev);
        Assert.False(Doc(id).Status.CanUndo);
    }

    [Fact]
    public async Task ShowOnDesktop_ForAProfileThatIsntThere_DoesNothing_AndKeepsRunning()
    {
        await Shown();

        await _shell.ClickAsync(ShellMenu.DesktopItem("no-such-profile", show: true));

        Assert.False(_factory.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested);
    }

    private IReadOnlyList<ShellMenuItem> ClickThroughLines() => Item(ShellMenu.ClickThrough).Children;

    private ShellService Service => _factory.Services.GetServices<IHostedService>().OfType<ShellService>().Single();

    /// <summary>A profile named <paramref name="name"/>, its window shown, clicks passing through it or not, saved.</summary>
    private string ShownProfile(string name, bool clickThrough, string level = "top")
    {
        var doc = TestDocs.Doc(name, 100, 50);
        doc["profile"]!["window"] = new JsonObject { ["enabled"] = true, ["level"] = level, ["clickThrough"] = clickThrough };
        return TestDocs.Create(Workspace, doc);
    }

    private static bool? ClickThroughOf(ProfileDocument doc) => doc.Current.Document.Profile.Window?.ClickThrough;

    private bool? SavedClickThrough(string id)
    {
        var saved = JsonNode.Parse(File.ReadAllText(Path.Combine(_base.DataDir, "profiles", id, "document.json")))!;
        return saved["profile"]?["window"]?["clickThrough"]?.GetValue<bool>();
    }

    [Fact]
    public async Task ClickThrough_ListsTheShownWindows_CheckedWhileClicksPassThrough()
    {
        await Shown();
        var through = ShownProfile("Desk overlay", clickThrough: true);
        var taking = ShownProfile("Neon core", clickThrough: false);
        Profile("Wide bar", shown: false);

        var listed = ClickThroughLines().Where(c => c.Label is "Desk overlay" or "Neon core" or "Wide bar").ToList();

        var expected = Workspace.List().Where(e => e.Id == through || e.Id == taking).Select(e => e.Name).ToList();
        Assert.Equal(expected, listed.Select(c => c.Label));
        var on = Assert.Single(listed, c => c.Label == "Desk overlay");
        Assert.Equal((ShellMenu.ClickThroughItem(through, on: false), true), (on.Id, on.Checked));
        var off = Assert.Single(listed, c => c.Label == "Neon core");
        Assert.Equal((ShellMenu.ClickThroughItem(taking, on: true), false), (off.Id, off.Checked));
    }

    [Fact]
    public async Task ClickThrough_TurnedOnForACleanProfile_IsSaved_AsOneUndoableChange_KeepingTheRestOfTheWindow_AndReachesItsWindow()
    {
        await Shown();
        var id = ShownProfile("Neon core", clickThrough: false, level: "desktop");
        var before = Doc(id).Rev;

        await _shell.ClickAsync(ShellMenu.ClickThroughItem(id, on: true));

        var window = Doc(id).Current.Document.Profile.Window!;
        Assert.Equal((true, true, "desktop"), (window.ClickThrough, window.Enabled, window.Level));
        Assert.Equal(before + 1, Doc(id).Rev);
        Assert.False(Workspace.Entry(id)!.Dirty);
        Assert.True(SavedClickThrough(id));
        Assert.True(Doc(id).Status.CanUndo);
        Assert.True(Assert.Single(ClickThroughLines(), c => c.Label == "Neon core").Checked);

        await _desktop.IdleAsync();
        var shown = await _desktop.InvokeAsync(() => _desktop.Created.Where(w => !w.Disposed).Select(w => w.Style).ToList());
        Assert.Contains(new DesktopWindowStyle(WindowLevel.Desktop, Movable: true, Resizable: true, ClickThrough: true), shown);
    }

    [Fact]
    public async Task ClickThrough_TurnedOff_IsSaved()
    {
        await Shown();
        var id = ShownProfile("Desk overlay", clickThrough: true);

        await _shell.ClickAsync(ShellMenu.ClickThroughItem(id, on: false));

        Assert.False(ClickThroughOf(Doc(id)));
        Assert.False(Workspace.Entry(id)!.Dirty);
        Assert.False(SavedClickThrough(id));
        Assert.False(Assert.Single(ClickThroughLines(), c => c.Label == "Desk overlay").Checked);
    }

    [Fact]
    public async Task ClickThrough_OnAProfileWithUnsavedChanges_IsLeftUnsaved_WithThem()
    {
        await Shown();
        var id = ShownProfile("Neon core", clickThrough: false);
        TestDocs.Commit(Doc(id), TestDocs.Profile(new JsonObject { ["width"] = 120 }));

        await _shell.ClickAsync(ShellMenu.ClickThroughItem(id, on: true));

        Assert.True(ClickThroughOf(Doc(id)));
        Assert.True(Workspace.Entry(id)!.Dirty);
        Assert.False(SavedClickThrough(id));
        Assert.Equal(120, Doc(id).Current.Document.Profile.Width);
    }

    [Fact]
    public async Task ClickThrough_AStaleLine_ChangesNothing()
    {
        await Shown();
        var id = ShownProfile("Desk overlay", clickThrough: true);
        var before = Doc(id).Rev;

        // The menu said "on" (it was off when it opened); another client turned it on since.
        await _shell.ClickAsync(ShellMenu.ClickThroughItem(id, on: true));

        Assert.True(ClickThroughOf(Doc(id)));
        Assert.Equal(before, Doc(id).Rev);
        Assert.False(Doc(id).Status.CanUndo);
    }

    [Fact]
    public async Task ClickThrough_AConflict_IsReadAgainAndRetried_KeepingTheOtherClientsChange()
    {
        await Shown();
        var id = ShownProfile("Neon core", clickThrough: false);
        // After the menu's change read the profile, before its commit: another client moves the window.
        Service.BeforeCommit = profileId =>
        {
            Service.BeforeCommit = null;
            var doc = Doc(profileId);
            var moved = doc.Current.Document.Profile.Window! with { X = 40, Y = 30 };
            TestDocs.Commit(doc, TestDocs.Profile(new JsonObject { ["window"] = JsonNode.Parse(JsonSerializer.Serialize(moved, InfoPanel.Spec.SpecJson.Options)) }));
        };

        await _shell.ClickAsync(ShellMenu.ClickThroughItem(id, on: true));

        var window = Doc(id).Current.Document.Profile.Window!;
        Assert.Equal((true, 40, 30), (window.ClickThrough, window.X, window.Y));
        Assert.Null(Service.BeforeCommit);
    }

    [Fact]
    public async Task ClickThrough_ForAProfileThatIsntThere_DoesNothing_AndKeepsRunning()
    {
        await Shown();

        await _shell.ClickAsync(ShellMenu.ClickThroughItem("no-such-profile", on: true));

        Assert.False(_factory.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested);
    }

    [Fact]
    public async Task ShowDataFolder_AndOpenLogsFolder_OpenTheEnginesOwnFolders()
    {
        await Shown();
        var paths = _factory.Services.GetRequiredService<DataPaths>();

        await _shell.ClickAsync(ShellMenu.ShowDataFolder);
        await _shell.ClickAsync(ShellMenu.OpenLogsFolder);

        Assert.Equal([paths.Root, paths.LogsDirectory], _base.Folders.Opened);
        Assert.Empty(_browser.Opened);
    }

    [Fact]
    public async Task MoreInTheEditor_OpensTheEditor()
    {
        await Shown();

        await _shell.ClickAsync(ShellMenu.MoreProfiles);

        Assert.Equal([$"http://127.0.0.1:{EngineFactory.Port}/auth/{EngineFactory.Token}"], _browser.Opened);
    }

    [Fact]
    public async Task CopyEngineDetails_CopiesTheEnginesDetails_NeverTheToken_ALink_OrAProfile()
    {
        await Shown();
        Profile("Secret plans", shown: true);
        var paths = _factory.Services.GetRequiredService<DataPaths>();

        await _shell.ClickAsync(ShellMenu.CopyEngineDetails);

        var text = Assert.Single(_shell.Copied);
        Assert.Contains($"Engine: {InfoPanel.Api.ReadEndpoints.EngineVersion()}, protocol {InfoPanel.Api.ReadEndpoints.Protocol}, ", text);
        Assert.Contains($"Engine build: {InfoPanel.Api.ReadEndpoints.EngineBuild()}\n", text);
        Assert.Contains($"OS version: {InfoPanel.Api.ReadEndpoints.OsVersion()}\n", text);
        Assert.Contains($"Architecture: {InfoPanel.Api.ReadEndpoints.Arch()}\n", text);
        Assert.Contains($"Engine address: http://127.0.0.1:{EngineFactory.Port}\n", text);
        // The home folder as ~: a test's data folder is inside it where the temporary folder is (Windows' %TEMP%), not on macOS.
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('/', '\\');
        var inHome = home.Length > 0 && paths.Root.StartsWith(home + Path.DirectorySeparatorChar, EngineDetails.PathComparison);
        Assert.Contains($"Data folder: {(inHome ? "~" + paths.Root[home.Length..] : paths.Root)}\n", text);
        Assert.Contains("Engine started: ", text);
        Assert.DoesNotContain(EngineFactory.Token, text);
        Assert.DoesNotContain("/auth/", text);
        Assert.DoesNotContain("#", text);
        Assert.DoesNotContain("Secret plans", text);
        Assert.DoesNotContain("Wide bar", text);
        Assert.Empty(_browser.Opened);
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
}

/// <summary>
/// The menu's signed-in line on the engine host (docs/2.0/desktop-shell.md, "The menu"), the configured service a fake on
/// a loopback port: the engine asks it who holds the credential (<c>GET /svc/v1/holder</c>) with the credential itself;
/// the holder's name and email with the answer, "Not signed in" without a credential, nothing while the name isn't known; the name
/// kept in memory only, never in a log, the recent entries, <c>/api/logs</c>, the settings or Copy Engine Details. Without
/// a menu, the engine asks nothing.
/// </summary>
public sealed class ShellSignedInTests : IAsyncDisposable
{
    private const string Credential = "test-credential.Zq8~+/Xy==";
    private const string Name = "Zed Quist-Holder";
    private const string Email = "zq-holder@example.test";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly LocalHttpServer _service;

    /// <summary>What the fake service answers: status and body.</summary>
    private volatile Func<(int Status, string Body)> _answer = () => (200, JsonSerializer.Serialize(new { name = Name, email = Email }));

    public ShellSignedInTests()
    {
        _service = new LocalHttpServer((_, stream, _) =>
        {
            var (status, body) = _answer();
            return LocalHttpServer.Answer(stream, status, Encoding.UTF8.GetBytes(body), "application/json");
        });
    }

    public async ValueTask DisposeAsync() => await _service.DisposeAsync();

    private string Address => $"http://127.0.0.1:{_service.Port}";

    private static StringContent Body(object value) => new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    private static async Task<(WebApplicationFactory<Program> Factory, FakeShellPlatform Shell)> Start(EngineFactory engine)
    {
        var shell = new FakeShellPlatform();
        var factory = engine.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IShellPlatform>(shell)));
        _ = factory.Services;
        await Until(() => shell.Icon != null, "the icon");
        return (factory, shell);
    }

    private static HttpClient Api(WebApplicationFactory<Program> factory)
    {
        var api = factory.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = false });
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", EngineFactory.Token);
        return api;
    }

    private async Task Connect(HttpClient api)
    {
        using var address = await api.PatchAsync("/api/settings", Body(new { service = new { address = Address } }), Ct);
        Assert.Equal(HttpStatusCode.OK, address.StatusCode);
        using var put = await api.PutAsync("/api/service/credential", Body(new { credential = Credential }), Ct);
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
    }

    private static string? SignedInLine(FakeShellPlatform shell) => shell.OpenMenu().Items.SingleOrDefault(i => i.Id == ShellMenu.SignedIn)?.Label;

    private static async Task Until(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"timed out waiting for {what}");
            await Task.Delay(10, Ct);
        }
    }

    [Fact]
    public async Task TheSignedInLine_AsksTheService_WithTheCredential_AndFollowsIt()
    {
        using var engine = new EngineFactory();
        var (factory, shell) = await Start(engine);
        using var _ = factory;
        using var api = Api(factory);
        Assert.Equal("Not signed in", SignedInLine(shell));

        await Connect(api);

        await Until(() => SignedInLine(shell) == Name, "the holder's name");
        Assert.Equal(Email, shell.OpenMenu().Items.Single(i => i.Id == ShellMenu.SignedIn).Detail);
        var request = Assert.Single(_service.Requests);
        Assert.Equal(("GET", "/svc/v1/holder", $"Bearer {Credential}"), (request.Method, request.Path, request.Header("Authorization")));

        using var deleted = await api.DeleteAsync("/api/service/credential", Ct);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        // Published by the holder's publisher, never inside the change's notification: shown as soon as it has run.
        await Until(() => SignedInLine(shell) == "Not signed in", "the line after the removal");
        Assert.Single(_service.Requests);
    }

    [Fact]
    public async Task ACredentialWhoseHolderIsntKnown_ShowsNoLine()
    {
        _answer = () => (503, "");
        using var engine = new EngineFactory();
        var (factory, shell) = await Start(engine);
        using var _ = factory;
        using var api = Api(factory);

        await Connect(api);
        await Until(() => _service.Requests.Count == 1, "the request");
        await Task.Delay(100, Ct);

        Assert.Null(SignedInLine(shell));
    }

    [Fact]
    public async Task AtStart_WithACredential_ItAsks()
    {
        using var engine = new EngineFactory();
        var (first, _) = await Start(engine);
        using (first)
        {
            using var api = Api(first);
            await Connect(api);
            await Until(() => _service.Requests.Count == 1, "the first engine's request");
        }

        await engine.WaitForTheFolderAsync(Ct);
        _answer = () => (200, JsonSerializer.Serialize(new { name = (string?)null, email = Email }));
        var (second, shell) = await Start(engine);
        using (second)
            await Until(() => SignedInLine(shell) == Email, "the second engine's answer");
        Assert.Equal(2, _service.Requests.Count);
    }

    [Fact]
    public async Task TheName_IsNeverInTheLogs_TheRecentLog_ApiLogs_TheSettings_OrCopyEngineDetails()
    {
        using var engine = new EngineFactory();
        var (factory, shell) = await Start(engine);
        using var _ = factory;
        using var api = Api(factory);
        await Connect(api);
        await Until(() => SignedInLine(shell) == Name, "the holder's name");

        await shell.ClickAsync(ShellMenu.CopyEngineDetails);

        var answers = new List<string> { Assert.Single(shell.Copied) };
        foreach (var path in new[] { "/api/logs", "/api/settings", "/api/service", "/api/session" })
            answers.Add(await api.GetStringAsync(path, Ct));
        var recent = factory.Services.GetRequiredService<RecentLog>().After(0);
        Assert.Contains(recent, e => e.Message.Contains("holder", StringComparison.OrdinalIgnoreCase));
        answers.AddRange(recent.Select(e => e.Message + e.Exception));
        foreach (var file in Directory.GetFiles(engine.DataDir, "*", SearchOption.AllDirectories))
        {
            if (file.EndsWith("engine.lock", StringComparison.Ordinal))
                continue;
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            answers.Add(reader.ReadToEnd());
        }

        foreach (var text in answers)
        {
            Assert.DoesNotContain("Quist", text);
            Assert.DoesNotContain("zq-holder", text);
        }
    }

    [Fact]
    public async Task WithoutAMenu_TheEngineAsksNothing()
    {
        using var engine = new EngineFactory();
        using var api = engine.CreateApiClient();

        await Connect(api);
        await Task.Delay(300, Ct);

        Assert.Empty(_service.Requests);
    }

    [Fact]
    public async Task AChangeOfAddress_WhileAnAnswerIsShown_FinishesAndShowsTheChange()
    {
        // A change of the service address holds the credential's lock from the credential's removal through the settings'
        // notification; an answer that comes meanwhile is shown without making the change's notification wait for it.
        using var engine = new EngineFactory();
        var store = new DeleteGatedStore(FileServiceCredentialStore.ForPrivateFile(engine.DataDir));
        var shell = new FakeShellPlatform();
        using var factory = engine.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.AddSingleton<IShellPlatform>(shell);
            s.AddSingleton<IServiceCredentialStore>(store);
        }));
        _ = factory.Services;
        await Until(() => shell.Icon != null, "the icon");
        var holder = factory.Services.GetRequiredService<ServiceHolder>();
        using var api = Api(factory);
        await Connect(api);
        await Until(() => SignedInLine(shell) == Name, "the holder's name");

        // The same credential provided again asks again; that answer waits until released.
        using var answer = new ManualResetEventSlim(false);
        _answer = () =>
        {
            answer.Wait(TimeSpan.FromSeconds(30));
            return (200, JsonSerializer.Serialize(new { name = "Late Holder", email = Email }));
        };
        using (var put = await api.PutAsync("/api/service/credential", Body(new { credential = Credential }), Ct))
            Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        await Until(() => _service.Requests.Count == 2, "the second request");

        // The change of address: its removal of the credential waits, inside the credential's lock, until released.
        await using var elsewhere = new LocalHttpServer((_, stream, _) => LocalHttpServer.Answer(stream, 404, [], "application/json"));
        store.Block = true;
        var patch = api.PatchAsync("/api/settings", Body(new { service = new { address = elsewhere.Url("") } }), Ct);
        try
        {
            await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
            // The answer comes meanwhile: the holder takes it in and shows what it knows while the change is held.
            answer.Set();
            await Until(() => holder.Requests == 2, "the second answer");
            await Task.Delay(500, Ct);
        }
        finally
        {
            store.Release.TrySetResult();
        }

        // The removal done, the change and its notification finish, and the line follows: nothing waits for good.
        using var patched = await patch.WaitAsync(TimeSpan.FromSeconds(15), Ct);
        Assert.Equal(HttpStatusCode.OK, patched.StatusCode);
        await Until(() => SignedInLine(shell) == "Not signed in", "the line after the change");
        Assert.Equal(2, _service.Requests.Count);
        Assert.Empty(elsewhere.Requests);
    }

    /// <summary>
    /// A store whose removals, while <see cref="Block"/> is set, signal <see cref="Entered"/> and wait for
    /// <see cref="Release"/> before removing.
    /// </summary>
    private sealed class DeleteGatedStore(IServiceCredentialStore inner) : IServiceCredentialStore
    {
        public volatile bool Block;
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Name => inner.Name;

        public StoredServiceCredential? Read() => inner.Read();

        public void Write(StoredServiceCredential record) => inner.Write(record);

        public void Delete()
        {
            if (Block)
            {
                Entered.TrySetResult();
                Release.Task.Wait(TimeSpan.FromSeconds(30));
            }

            inner.Delete();
        }
    }
}
