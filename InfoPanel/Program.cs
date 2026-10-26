using InfoPanel;
using InfoPanel.Api;
using InfoPanel.Desktop;
using InfoPanel.Drawing;
using InfoPanel.Editing;
using InfoPanel.Hosting;
using InfoPanel.Images;
using InfoPanel.Output;
using InfoPanel.Persistence;
using InfoPanel.Preview;
using InfoPanel.Security;
using InfoPanel.Sensors;
using Microsoft.AspNetCore.HostFiltering;
using Serilog;
using Serilog.Events;
using System.Net;
using System.Text.Json.Serialization;

// The InfoPanel 2.0 engine: a headless, loopback-only ASP.NET Core host that loads profiles, reads sensors, renders
// with Core and serves the web client. Exit codes: 0 clean, 1 fatal error, 2 bad arguments, 3 data folder in use.

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

// The sign-in link carries the token, so it goes to the console only; the file gets it redacted (EngineLifetime).
var engineLogger = new LoggerConfiguration()
    .MinimumLevel.Information()
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
            rollOnFileSizeLimit: true))
    .CreateLogger();
Log.Logger = engineLogger;

try
{
    if (options.Fresh)
        Log.Information("Wiped the data folder {DataDir} (--fresh)", paths.Root);

    var store = new ProfileStore(paths);
    if (options.ImportV1 != null)
    {
        var summary = V1DataImporter.ImportFolder(options.ImportV1, store);
        Log.Information("Imported {Imported} profile(s) from {Source}; skipped {Skipped}",
            summary.Imported.Count, options.ImportV1, summary.Skipped.Count);
        foreach (var profile in summary.Imported)
            Log.Information("  imported {Name} ({Items} items, {Assets} assets){Renamed}", profile.Name, profile.ItemCount,
                profile.AssetCount, profile.GuidRegenerated ? $" as {profile.Guid}" : "");
        foreach (var skipped in summary.Skipped)
            Log.Warning("  skipped {Name} ({Guid}): {Reason}", skipped.Name, skipped.Guid, skipped.Reason);
    }

    try
    {
        if (Workspace.EnsureExampleProfile(store))
            Log.Information("The data folder had no profiles; created the example profile");
    }
    catch (InvalidDataException ex)
    {
        Log.Error(ex, "Could not read the profile list");
    }

    FontRegistry.RegisterEmbedded();
    var userFonts = FontRegistry.RegisterDirectory(paths.FontsDirectory);
    if (userFonts.Count > 0)
        Log.Information("Registered {Count} font families from {Folder}", userFonts.Count, paths.FontsDirectory);

    var webRoot = DataFolder.FindWebRoot(options.WebRoot);
    if (webRoot == null)
        Log.Warning("The web client isn't built (no wwwroot or client/dist); serving the API only");
    else
        Log.Information("Serving the web client from {WebRoot}", webRoot);

    var port = options.Port == 0 ? EngineRuntime.FindFreePort() : options.Port;
    var runtime = new EngineRuntime(port, options.Token, options.DevOrigins);

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
    builder.Services.AddSingleton(store);
    builder.Services.AddSingleton<IAssetLocator>(store);
    builder.Services.AddSingleton(TimeProvider.System);
    builder.Services.AddSingleton(sp => new MockSensorSource(options.MockSeed, sp.GetRequiredService<TimeProvider>()));
    builder.Services.AddSingleton<ISensorSource>(sp => sp.GetRequiredService<MockSensorSource>());
    builder.Services.AddSingleton(sp => new SensorHub(sp.GetServices<ISensorSource>()));
    builder.Services.AddSingleton<ISensorReader>(sp => sp.GetRequiredService<SensorHub>());
    builder.Services.AddSingleton(sp => new Workspace(store, sp.GetRequiredService<SensorHub>(), sp.GetRequiredService<TimeProvider>()));
    builder.Services.AddSingleton(sp =>
    {
        var hub = sp.GetRequiredService<SensorHub>();
        return new SensorHistory(hub, sp.GetRequiredService<TimeProvider>(), hub.GetBackfill);
    });
    builder.Services.AddSingleton<ISensorHistory>(sp => sp.GetRequiredService<SensorHistory>());
    builder.Services.AddSingleton(_ => new ImageCache());
    builder.Services.AddSingleton<IImageSource>(sp => sp.GetRequiredService<ImageCache>());
    builder.Services.AddSingleton(sp => new ProfileRenderer(
        sp.GetRequiredService<ISensorReader>(),
        sp.GetRequiredService<ISensorHistory>(),
        sp.GetRequiredService<IImageSource>(),
        sp.GetRequiredService<IAssetLocator>(),
        sp.GetRequiredService<TimeProvider>()));
    builder.Services.AddSingleton<ProfileRenderStates>();
    builder.Services.AddSingleton<ThumbnailRenderer>();
    // Live frames for /ws (and later panels); disposed with the host, which stops every render loop.
    builder.Services.AddSingleton(sp => new RenderService(
        sp.GetRequiredService<Workspace>(),
        sp.GetRequiredService<ProfileRenderer>(),
        sp.GetRequiredService<ProfileRenderStates>(),
        sp.GetRequiredService<ThumbnailRenderer>(),
        sp.GetRequiredService<TimeProvider>(),
        sp.GetService<RenderServiceOptions>()));
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
    app.UseWebClientFiles(webRoot);
    app.UseRouting();

    app.MapGet("/healthz", () => Results.Text("ok", "text/plain"));
    app.MapAuth();
    app.MapReadEndpoints();
    app.MapEditEndpoints();
    app.MapPreviewSocket();
    app.MapWebClientFallback(webRoot);

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
    if (ReferenceEquals(Log.Logger, engineLogger))
        Log.Logger = Serilog.Core.Logger.None;
    await engineLogger.DisposeAsync();
}

public partial class Program;
