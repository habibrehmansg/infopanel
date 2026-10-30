using System.Net.Http.Headers;
using InfoPanel.Hosting;
using InfoPanel.Preview.Video;
using InfoPanel.Scenes;
using InfoPanel.Tests.Contract;
using InfoPanel.Tests.Preview.Video;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace InfoPanel.Tests;

/// <summary>
/// The engine on a <c>TestServer</c>, with a temp data folder, a temp web root holding an <c>index.html</c>,
/// a fixed token, port, dev origin and cross origin, seeded mock sensors and a fixed PC regional format (en-US), so
/// frames don't depend on the machine's region. Its folder opener only records (<see cref="Folders"/>), so no test engine
/// opens a file manager, and its browser launcher starts nothing. Its 1.x folder is its own (<see cref="V1Dir"/>, empty unless a test puts
/// 1.x data there), so no test engine reads the 1.x data of the user running the tests. It runs offline
/// (<c>--offline</c>): its media providers connect only to this computer, so a test's web image reaches a server the
/// test started on loopback, or nothing. Every response it gives, on the TestServer
/// or on Kestrel (<c>LiveEngine</c>), is checked against spec/openapi.json (<see cref="ContractCheck"/>), except in
/// <c>Category=Performance</c> tests; <see cref="Contract"/> holds what the check saw, and disposing the factory fails on
/// any violation it recorded.
/// </summary>
public class EngineFactory : WebApplicationFactory<Program>
{
    public const string Token = "test-token_0123456789";
    public const int Port = 47123;
    public const string DevOrigin = "http://localhost:5180";
    /// <summary>A web client served from another origin (<c>--allow-origin</c>).</summary>
    public const string CrossOrigin = "https://client.example";
    public const string IndexMarker = "client-test-index";

    /// <summary>The PC's regional format every test engine has, whatever the machine's.</summary>
    public static RegionalFormat Regional { get; } = RegionalFormat.Lookup("en-US")!;

    public EngineFactory()
    {
        Root = Path.Combine(Path.GetTempPath(), "infopanel-engine-tests-" + Guid.NewGuid().ToString("N"));
        DataDir = Path.Combine(Root, "data");
        WebRoot = Path.Combine(Root, "web");
        V1Dir = Path.Combine(Root, "v1");
        Directory.CreateDirectory(Path.Combine(WebRoot, "assets"));
        File.WriteAllText(Path.Combine(WebRoot, "index.html"), $"<!doctype html><title>{IndexMarker}</title>");
        File.WriteAllText(Path.Combine(WebRoot, "assets", "app.js"), "console.log('web-client');");
    }

    public string Root { get; }

    public string DataDir { get; }

    public string WebRoot { get; }

    /// <summary>The engine's 1.x folder (<c>--v1-dir</c>): not created, so the engine finds no 1.x data unless a test puts some there.</summary>
    public string V1Dir { get; }

    /// <summary>What the contract check saw of this engine's responses.</summary>
    public ContractLog Contract { get; } = new();

    /// <summary>The folders this engine was asked to open (<c>POST /api/folders/{folder}/open</c>), in order: none is opened.</summary>
    public RecordingFolderOpener Folders { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Outermost, ahead of every other startup filter, so it sees each response as the client gets it.
        if (!ContractCheck.OptedOut())
            builder.ConfigureServices(services => services.Insert(0, ServiceDescriptor.Singleton<IStartupFilter>(new ContractCheckFilter(Contract))));

        builder.UseSetting("Engine:DataDir", DataDir);
        builder.UseSetting("Engine:WebRoot", WebRoot);
        builder.UseSetting("Engine:V1Dir", V1Dir);
        builder.UseSetting("Engine:Token", Token);
        builder.UseSetting("Engine:Port", Port.ToString());
        builder.UseSetting("Engine:NoBrowser", "true");
        builder.UseSetting("Engine:MockSeed", "42");
        // Media providers connect only to this computer: no test engine reaches the network.
        builder.UseSetting("Engine:Offline", "true");
        // No plugin hosts unless a test asks (Engine:NoPlugins false, Engine:BundledPlugins its folder).
        builder.UseSetting("Engine:NoPlugins", "true");
        builder.UseSetting("Engine:DevOrigins:0", DevOrigin);
        builder.UseSetting("Engine:AllowOrigins:0", CrossOrigin);
        // No video decoder, whatever the machine has (INFOPANEL_FFMPEG_DIR included): a test that plays video sets its own.
        builder.UseSetting("Engine:FFmpegDir", Path.Combine(Root, "no-ffmpeg"));
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton(Regional);
            // A fake H.264 encoder as the run's only one (as --h264 enables it), so video runs on any machine.
            services.AddSingleton(sp => new H264Backends(H264Backends.Enabled([new FakeH264Backend()], sp.GetRequiredService<EngineOptions>().H264),
                sp.GetRequiredService<ILoggerFactory>().CreateLogger("InfoPanel.Preview.Video")));
            services.AddSingleton<IFolderOpener>(Folders);
            // Every test engine has --no-browser; one that turns it off gets a launcher that opens nothing.
            services.AddSingleton(sp => new BrowserLauncher(sp.GetRequiredService<ILogger<BrowserLauncher>>(),
                _ => throw new InvalidOperationException("No test engine opens a browser.")));
        });
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

    private int _asserted;

    /// <summary>Stops the engine, removes its folders, and fails when any response it gave broke the contract.</summary>
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            try { Directory.Delete(Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }

            // Once (the base's disposal comes back here), after the hosts, those of WithWebHostBuilder and LiveEngine's
            // included, have stopped.
            if (Interlocked.Exchange(ref _asserted, 1) == 0)
                Contract.AssertConformed(TimeSpan.FromSeconds(10));
        }
    }
}

/// <summary>A folder opener for tests: it records each folder it is asked to open and opens none, or throws <see cref="Failure"/>.</summary>
public sealed class RecordingFolderOpener : IFolderOpener
{
    private readonly Lock _lock = new();
    private readonly List<string> _opened = [];

    /// <summary>When set, every open is recorded and then throws it, as a file manager that can't be started.</summary>
    public Exception? Failure { get; init; }

    public IReadOnlyList<string> Opened
    {
        get
        {
            lock (_lock)
                return [.. _opened];
        }
    }

    public void Open(string folder)
    {
        lock (_lock)
            _opened.Add(folder);
        if (Failure != null)
            throw Failure;
    }
}
