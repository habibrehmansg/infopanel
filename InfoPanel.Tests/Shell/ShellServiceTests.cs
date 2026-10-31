using System.ComponentModel;
using System.Text.Json.Nodes;
using InfoPanel.Documents;
using InfoPanel.Hosting;
using InfoPanel.Shell;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace InfoPanel.Tests.Shell;

/// <summary>
/// The menu-bar item on the engine host, with a <see cref="FakeShellPlatform"/> and a browser launcher that only records:
/// when the icon comes and goes, Open Editor, and Quit with and without unsaved profiles.
/// </summary>
public sealed class ShellServiceTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly EngineFactory _base = new();
    private readonly FakeShellPlatform _shell = new();
    private readonly RecordingBrowserLauncher _browser = new();
    private readonly WebApplicationFactory<Program> _factory;

    public ShellServiceTests()
    {
        _factory = _base.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IShellPlatform>(sp =>
            {
                _shell.Lifetime = sp.GetRequiredService<IHostApplicationLifetime>();
                return _shell;
            });
            services.AddSingleton<BrowserLauncher>(_browser);
        }));
        // Starts the host.
        _ = _factory.Services;
    }

    public void Dispose()
    {
        _factory.Dispose();
        _base.Dispose();
    }

    private IHostApplicationLifetime Lifetime => _factory.Services.GetRequiredService<IHostApplicationLifetime>();

    private Workspace Workspace => _factory.Services.GetRequiredService<Workspace>();

    private bool Stopping => Lifetime.ApplicationStopping.IsCancellationRequested;

    /// <summary>A new profile named <paramref name="name"/> with a change not yet saved.</summary>
    private string Unsaved(string name)
    {
        var id = TestDocs.Create(Workspace, TestDocs.Doc(name, 100, 50));
        TestDocs.Commit(Workspace.Open(id)!, TestDocs.Profile(new JsonObject { ["width"] = 120 }));
        Assert.True(Workspace.Entry(id)!.Dirty);
        return id;
    }

    [Fact]
    public async Task TheIcon_IsShownOnceTheServerListens_WithItsAddress_AndRemovedWhenTheEngineStops()
    {
        await WaitUntil(() => _shell.Icon != null);

        Assert.Equal(1, _shell.ShowCount);
        Assert.True(_shell.ShownAfterStart);
        Assert.Equal($"InfoPanel — http://127.0.0.1:{EngineFactory.Port}", _shell.Icon!.Tooltip);
        Assert.False(_shell.Removed);
        Assert.Equal(ShellMenu.Build(new ShellState(InfoPanel.Api.ReadEndpoints.EngineVersion())), _shell.OpenMenu(), new MenuComparer());

        _factory.Dispose();

        Assert.True(_shell.Removed);
    }

    [Fact]
    public async Task OpenEditor_OpensTheSignInLink_WithTheToken()
    {
        await WaitUntil(() => _shell.Icon != null);

        await _shell.ClickAsync(ShellMenu.OpenEditor);

        Assert.Equal([$"http://127.0.0.1:{EngineFactory.Port}/auth/{EngineFactory.Token}"], _browser.Opened);
        Assert.False(Stopping);
    }

    [Fact]
    public async Task Quit_WithNothingUnsaved_StopsTheEngine_WithoutAsking()
    {
        await WaitUntil(() => _shell.Icon != null);

        await _shell.ClickAsync(ShellMenu.Quit);

        Assert.True(Stopping);
        Assert.Empty(_shell.Prompts);
        Assert.Empty(_browser.Opened);
    }

    [Fact]
    public async Task Quit_WithAnUnsavedProfile_Asks_AndCancelKeepsItRunning()
    {
        await WaitUntil(() => _shell.Icon != null);
        Unsaved("Desk overlay");
        _shell.Answer(1);

        await _shell.ClickAsync(ShellMenu.Quit);

        var prompt = Assert.Single(_shell.Prompts);
        Assert.Equal("Quit InfoPanel?", prompt.Title);
        Assert.Equal("“Desk overlay” has unsaved changes. InfoPanel keeps them for its next start, unsaved.", prompt.Text);
        Assert.Equal(["Open Editor", "Cancel", "Quit Without Saving"], prompt.Buttons);
        Assert.Equal(0, prompt.DefaultButton);
        Assert.Equal(1, prompt.CancelButton);
        Assert.False(Stopping);
        Assert.Empty(_browser.Opened);
    }

    [Fact]
    public async Task Quit_WithUnsavedProfiles_OpenEditorKeepsItRunning_AndOpensTheEditor()
    {
        await WaitUntil(() => _shell.Icon != null);
        Unsaved("Desk overlay");
        Unsaved("Wide bar");
        _shell.Answer(0);

        await _shell.ClickAsync(ShellMenu.Quit);

        var prompt = Assert.Single(_shell.Prompts);
        Assert.Equal("“Desk overlay” and “Wide bar” have unsaved changes. InfoPanel keeps them for its next start, unsaved.", prompt.Text);
        Assert.False(Stopping);
        Assert.Equal([$"http://127.0.0.1:{EngineFactory.Port}/auth/{EngineFactory.Token}"], _browser.Opened);
    }

    [Fact]
    public async Task Quit_WithUnsavedProfiles_QuitWithoutSavingStopsTheEngine()
    {
        await WaitUntil(() => _shell.Icon != null);
        Unsaved("Desk overlay");
        _shell.Answer(2);

        await _shell.ClickAsync(ShellMenu.Quit);

        Assert.Single(_shell.Prompts);
        Assert.True(Stopping);
        Assert.Empty(_browser.Opened);
    }

    [Fact]
    public async Task ThePrompt_NamesThreeProfiles_ThenSaysHowManyMore()
    {
        await WaitUntil(() => _shell.Icon != null);
        foreach (var name in new[] { "A", "B", "C" })
            Unsaved(name);
        _shell.Answer(1);
        await _shell.ClickAsync(ShellMenu.Quit);
        Unsaved("D");
        Unsaved("E");
        _shell.Answer(1);
        await _shell.ClickAsync(ShellMenu.Quit);

        Assert.Equal(["“A”, “B” and “C” have unsaved changes. InfoPanel keeps them for its next start, unsaved.",
            "“A”, “B”, “C” and 2 more have unsaved changes. InfoPanel keeps them for its next start, unsaved."],
            _shell.Prompts.Select(p => p.Text));
        Assert.False(Stopping);
    }

    [Fact]
    public async Task ASystemQuit_StopsTheEngine_WithoutAsking_EvenWithUnsavedProfiles()
    {
        await WaitUntil(() => _shell.Icon != null);
        Unsaved("Desk overlay");

        _shell.RequestTerminate();

        await WaitUntil(() => Stopping);
        Assert.Empty(_shell.Prompts);
    }

    [Theory]
    [InlineData("Production", false)]
    [InlineData("Development", false)]
    [InlineData(ShellService.TestEnvironment, true)]
    public async Task TheTestPromptSetting_AsksAtStart_OnlyWhenTheEngineRunsAsATest(string environment, bool asked)
    {
        // The data folder is this class's engine's: it stops first.
        _factory.Dispose();
        var shell = new FakeShellPlatform();
        using var factory = _base.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            builder.UseSetting(ShellService.TestPromptSetting, "true");
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IShellPlatform>(sp =>
                {
                    shell.Lifetime = sp.GetRequiredService<IHostApplicationLifetime>();
                    return shell;
                });
                services.AddSingleton<BrowserLauncher>(_browser);
            });
        });

        // Starts the host; the icon is shown, and the prompt asked, as it starts.
        _ = factory.Services;
        await WaitUntil(() => shell.Icon != null);

        Assert.Equal(asked ? 1 : 0, shell.Prompts.Count);
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

    private sealed class MenuComparer : IEqualityComparer<ShellMenu>
    {
        public bool Equals(ShellMenu? x, ShellMenu? y) => x!.Items.SequenceEqual(y!.Items);

        public int GetHashCode(ShellMenu obj) => obj.Items.Count;
    }
}

/// <summary>
/// The menu's Open Editor and the token: what it hands the OS opener when that fails is kept out of the log file and the
/// entries kept for clients.
/// </summary>
public sealed class ShellOpenEditorLogTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task OpenEditor_WithALauncherThatFails_KeepsTheTokenOutOfTheLogFile_AndTheEntries()
    {
        using var engine = new EngineFactory();
        var shell = new FakeShellPlatform();
        var attempts = new List<string>();
        using var factory = engine.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IShellPlatform>(shell);
            services.AddSingleton(sp => new BrowserLauncher(sp.GetRequiredService<ILogger<BrowserLauncher>>(), sp.GetRequiredService<RedirectFiles>(), command =>
            {
                var started = string.Join(" ", [command.FileName, .. command.ArgumentList]);
                lock (attempts)
                    attempts.Add(started);
                throw new Win32Exception($"An error occurred trying to start process '{started}'.");
            }));
        }));
        var recent = factory.Services.GetRequiredService<RecentLog>();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (shell.Icon == null && DateTime.UtcNow < deadline)
            await Task.Delay(10, Ct);

        await shell.ClickAsync(ShellMenu.OpenEditor);

        lock (attempts)
            Assert.Single(attempts);
        var logs = Path.Combine(engine.DataDir, "logs");
        string File() => string.Join("\n", Directory.Exists(logs) ? Directory.GetFiles(logs).Select(ReadShared) : []);
        while (!File().Contains("Could not open the browser") && DateTime.UtcNow < deadline)
            await Task.Delay(20, Ct);
        Assert.Contains("Could not open the browser", File());
        Assert.DoesNotContain(EngineFactory.Token, File());
        Assert.All(recent.After(0), e => Assert.DoesNotContain(EngineFactory.Token, $"{e.Message}\n{e.Exception}\n{e.Source}"));
    }

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

/// <summary>A browser launcher that records each link it is asked to open and opens nothing.</summary>
public sealed class RecordingBrowserLauncher() : BrowserLauncher(
    Microsoft.Extensions.Logging.Abstractions.NullLogger<BrowserLauncher>.Instance,
    new RedirectFiles(Path.Combine(Path.GetTempPath(), "never-written"), "0000000000000000", TimeProvider.System,
        Microsoft.Extensions.Logging.Abstractions.NullLogger<RedirectFiles>.Instance),
    _ => throw new InvalidOperationException("Nothing opens."))
{
    private readonly Lock _lock = new();
    private readonly List<EditorLink> _links = [];

    public IReadOnlyList<EditorLink> Links
    {
        get
        {
            lock (_lock)
                return [.. _links];
        }
    }

    /// <summary>The links' addresses, in order.</summary>
    public IReadOnlyList<string> Opened => [.. Links.Select(l => l.Url)];

    public override void Open(EditorLink link)
    {
        lock (_lock)
            _links.Add(link);
    }
}
