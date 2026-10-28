using InfoPanel;
using InfoPanel.Api;
using InfoPanel.Desktop;
using InfoPanel.Documents;
using InfoPanel.Drawing;
using InfoPanel.Hosting;
using InfoPanel.Images;
using InfoPanel.Output;
using InfoPanel.Persistence;
using InfoPanel.Preview;
using InfoPanel.Scenes;
using InfoPanel.Security;
using InfoPanel.Sensors;
using InfoPanel.Sensors.MacOS;
using InfoPanel.Settings;
using Microsoft.AspNetCore.HostFiltering;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using System.Net;
using System.Text.Json.Serialization;

// The InfoPanel 2.0 engine: a headless, loopback-only ASP.NET Core host that stores and edits 2.0 documents, reads
// sensors, renders every output with the scene runtime and serves the web client. Exit codes: 0 clean, 1 fatal error, 2 bad arguments, 3 data folder in use.

var parsed = EngineCommandLine.Parse(args);
if (parsed.ShowHelp)
{
    Console.WriteLine(EngineCommandLine.Usage);
    return 0;
}

if (parsed.Error != null)
{
    Console.Error.WriteLine($"InfoPanel: {parsed.Error}");
    Console.Error.WriteLine(EngineCommandLine.Usage);
    return 2;
}

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [.. parsed.HostArgs] });
builder.Configuration.AddInMemoryCollection(parsed.Settings);

var options = EngineOptions.Load(builder.Configuration, out var optionErrors);
if (options == null)
{
    foreach (var error in optionErrors)
        Console.Error.WriteLine($"InfoPanel: {error}");
    return 2;
}

var paths = options.DataDir != null ? new DataPaths(options.DataDir) : DataPaths.DefaultRoot();
DataFolder.CreatePrivate(paths.Root);

using var instanceLock = InstanceLock.TryAcquire(paths.EngineLockFile, out var lockError);
if (instanceLock == null)
{
    Console.Error.WriteLine($"InfoPanel: {lockError}");
    return 3;
}

if (options.Fresh)
{
    try
    {
        // Re-checks what EngineOptions.Load checked (real path, only engine files) now that the lock is held.
        DataFolder.Wipe(paths.Root, paths.EngineLockFile);
    }
    catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine($"InfoPanel: {ex.Message}");
        return 2;
    }
}

DataFolder.CreatePrivate(paths.LogsDirectory);

// The sign-in link carries the token, so it goes to the console only; the file gets it redacted (EngineLifetime), and
// so do the recent entries kept for clients. The level is the log level setting, set once the settings are open.
var logLevel = new LoggingLevelSwitch(LogEventLevel.Information);
var recentLog = new RecentLog();
var engineLogger = new LoggerConfiguration()
    .MinimumLevel.ControlledBy(logLevel)
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
    .Enrich.FromLogContext()
    .WriteTo.Logger(console => console
        .Filter.ByExcluding(e => LogTargets.Is(e, LogTargets.File))
        .WriteTo.Console())
    .WriteTo.Logger(file => file
        .Filter.ByExcluding(e => LogTargets.Is(e, LogTargets.Console))
        .WriteTo.File(
            Path.Combine(paths.LogsDirectory, "infopanel-.log"),
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 7,
            fileSizeLimitBytes: 100L * 1024 * 1024,
            rollOnFileSizeLimit: true)
        .WriteTo.Sink(recentLog))
    .CreateLogger();
Log.Logger = engineLogger;
// Errors nothing else catches are logged; before an unhandled exception ends the process the log is written out.
var crashLogging = CrashLogging.Install(engineLogger.ForContext<CrashLogging>(), flush: engineLogger.Dispose);

try
{
    if (options.Fresh)
        Log.Information("Wiped the data folder {DataDir} (--fresh)", paths.Root);

    // The stores are opened and their blobs swept before anything changes them; the first run stores the example.
    var workspace = Workspace.Start(paths, new WorkspaceOptions { Logger = engineLogger.ForContext<Workspace>() }, DateTimeOffset.UtcNow);
    // The engine settings: a file that can't be read is logged, leaves the defaults in effect and is never rewritten.
    var settings = SettingsStore.Open(paths, engineLogger.ForContext<SettingsStore>());
    logLevel.MinimumLevel = LevelOf(settings.Current.Settings.LogLevel);
    settings.Changed += snapshot => logLevel.MinimumLevel = LevelOf(snapshot.Settings.LogLevel);
    // Integration tokens: a file that can't be read stops the engine rather than dropping or overwriting them.
    var tokens = TokenStore.Open(paths.TokensFile, paths.Root, TimeProvider.System, engineLogger.ForContext<TokenStore>());

    FontRegistry.RegisterEmbedded();
    var userFonts = FontRegistry.RegisterDirectory(paths.FontsDirectory);
    if (userFonts.Count > 0)
        Log.Information("Registered {Count} font families from {Folder}", userFonts.Count, paths.FontsDirectory);

    var webRoot = DataFolder.FindWebRoot(options.WebRoot);
    if (webRoot == null)
        Log.Warning("No web client installed (no wwwroot next to the engine and no --web-root); serving the API only");
    else
        Log.Information("Serving the web client from {WebRoot}", webRoot);

    var port = options.Port == 0 ? EngineRuntime.FindFreePort() : options.Port;
    var runtime = new EngineRuntime(port, options.Token, options.DevOrigins, options.AllowOrigins);

    builder.Services.AddSerilog(engineLogger);
    // Loopback only: endpoints from configuration (Kestrel:Endpoints from arguments, environment or appsettings)
    // are dropped by giving Kestrel an empty configuration, and URL settings (ASPNETCORE_URLS, --urls, *_PORTS)
    // are never preferred, so Kestrel binds the one Listen below and ignores them (it logs "Overriding address(es)").
    builder.WebHost.PreferHostingUrls(false);
    builder.WebHost.ConfigureKestrel(kestrel =>
    {
        kestrel.Configure(new ConfigurationBuilder().Build(), reloadOnChange: false);
        kestrel.Listen(IPAddress.Loopback, port);
    });
    builder.Services.Insert(0, ServiceDescriptor.Transient<IStartupFilter, SecurityHeadersStartupFilter>());
    builder.Services.Configure<HostFilteringOptions>(o =>
    {
        o.AllowedHosts = ["localhost", "127.0.0.1", "[::1]"];
        o.AllowEmptyHosts = false;
        o.IncludeFailureMessage = false;
    });
    builder.Services.AddProblemDetails();
    builder.Services.ConfigureHttpJsonOptions(json =>
    {
        json.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
        json.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    });

    builder.Services.AddSingleton(options);
    builder.Services.AddSingleton(paths);
    builder.Services.AddSingleton(runtime);
    builder.Services.AddSingleton(tokens);
    builder.Services.AddSingleton(settings);
    builder.Services.AddSingleton(recentLog);
    // Made by a factory so the container disposes it, removing its handlers, when the host stops; the finally below
    // covers a start that fails before the host is built.
    builder.Services.AddSingleton(_ => crashLogging);
    // Made by a factory so the container disposes it (and every open document) after the services that use it.
    builder.Services.AddSingleton(_ => workspace);
    builder.Services.AddSingleton(TimeProvider.System);
    // Mock mode: the mock sensors and simulated unknown keys (tests, demos). Otherwise the platform's real sensors (only
    // macOS has a source so far) and unknown keys read as unavailable.
    if (options.Mock)
    {
        builder.Services.AddSingleton(sp => new MockSensorSource(options.MockSeed, sp.GetRequiredService<TimeProvider>()));
        builder.Services.AddSingleton<ISensorSource>(sp => sp.GetRequiredService<MockSensorSource>());
    }
    else if (OperatingSystem.IsMacOS())
    {
        builder.Services.AddSingleton<ISensorSource>(sp => MacSensorSource.Create(sp.GetRequiredService<TimeProvider>()));
    }

    builder.Services.AddSingleton(sp => new SensorHub(sp.GetServices<ISensorSource>(), new SensorHubOptions { SimulateUnknown = options.Mock }));
    builder.Services.AddSingleton<ISensorReader>(sp => sp.GetRequiredService<SensorHub>());
    // The history samples at the graph rate setting, re-timed when it changes.
    builder.Services.AddSingleton(sp =>
    {
        var hub = sp.GetRequiredService<SensorHub>();
        var history = new SensorHistory(hub, sp.GetRequiredService<TimeProvider>(), hub.GetBackfill, settings.Current.Settings.GraphInterval);
        settings.Changed += snapshot => history.Interval = snapshot.Settings.GraphInterval;
        return history;
    });
    builder.Services.AddSingleton<ISensorHistory>(sp => sp.GetRequiredService<SensorHistory>());
    // One media cache for the process, over the blob store; every render loop's runtime and every thumbnail reads it. A
    // blob stored after a lookup of it failed is read again, so its missing-media placeholders repair.
    builder.Services.AddSingleton(_ =>
    {
        var media = new MediaSampleCache(new BlobMediaStore(workspace.Blobs), new MediaSampleCacheOptions { Limits = workspace.Limits });
        workspace.BlobStored += media.Retry;
        return media;
    });
    builder.Services.AddSingleton(sp => new SceneRuntimeOptions
    {
        Inputs = new SensorSceneInputs(sp.GetRequiredService<ISensorReader>(), sp.GetRequiredService<ISensorHistory>()),
        Media = sp.GetRequiredService<MediaSampleCache>(),
        Limits = workspace.Limits,
        Support = workspace.Support,
    });
    // Live frames for /ws and desktop windows; disposed with the host, which stops every render loop.
    builder.Services.AddSingleton(sp => new RenderService(
        sp.GetRequiredService<Workspace>(),
        sp.GetRequiredService<SceneRuntimeOptions>(),
        sp.GetRequiredService<TimeProvider>(),
        sp.GetService<RenderServiceOptions>()));
    builder.Services.AddSingleton(new PreviewSocketOptions());
    builder.Services.AddSingleton<BrowserLauncher>();
    builder.Services.AddHostedService<SensorService>();
    builder.Services.AddHostedService<EngineLifetime>();

    // Desktop windows: on macOS the main thread runs AppKit and the host runs on a worker (DesktopHost).
    var desktop = DesktopHost.Create(options);
    builder.Services.AddSingleton(desktop.Platform);
    builder.Services.AddSingleton<DisplayWindowManager>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<DisplayWindowManager>());

    var app = builder.Build();

    app.UseApiProblems();
    app.UseMiddleware<SecurityMiddleware>();
    var webSockets = new WebSocketOptions();
    foreach (var origin in runtime.AllowedOrigins)
        webSockets.AllowedOrigins.Add(origin);
    app.UseWebSockets(webSockets);
    app.UseClientFiles(webRoot);
    app.UseRouting();
    app.UseMiddleware<ScopeFilter>();

    app.MapGet("/healthz", () => Results.Text("ok", "text/plain"));
    app.MapAuth();
    app.MapReadEndpoints();
    app.MapProfileEndpoints();
    app.MapBlobEndpoints();
    app.MapTokenEndpoints();
    app.MapPreviewSocket();
    app.MapClientFallback(webRoot);

    await desktop.RunAsync(() => app.RunAsync());
    return 0;
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "The engine stopped on an error");
    return 1;
}
finally
{
    crashLogging.Dispose();
    if (ReferenceEquals(Log.Logger, engineLogger))
        Log.Logger = Serilog.Core.Logger.None;
    await engineLogger.DisposeAsync();
}

static LogEventLevel LevelOf(EngineLogLevel level) => level switch
{
    EngineLogLevel.Debug => LogEventLevel.Debug,
    EngineLogLevel.Warning => LogEventLevel.Warning,
    EngineLogLevel.Error => LogEventLevel.Error,
    _ => LogEventLevel.Information,
};

public partial class Program;
