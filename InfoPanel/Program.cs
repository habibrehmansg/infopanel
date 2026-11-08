using InfoPanel;
using InfoPanel.Api;
using InfoPanel.Desktop;
using InfoPanel.Documents;
using InfoPanel.Drawing;
using InfoPanel.Hosting;
using InfoPanel.Images;
using InfoPanel.Import;
using InfoPanel.Output;
using InfoPanel.Persistence;
using InfoPanel.Plugins;
using InfoPanel.Preview;
using InfoPanel.Preview.Video;
using InfoPanel.Scenes;
using InfoPanel.Security;
using InfoPanel.Sensors;
using InfoPanel.Sensors.HwInfo;
using InfoPanel.Sensors.HwInfo.Gadget;
using InfoPanel.Sensors.Libre;
using InfoPanel.Sensors.MacOS;
using InfoPanel.Sensors.Replay;
using InfoPanel.Service;
using InfoPanel.Settings;
using InfoPanel.Spec;
using Microsoft.AspNetCore.HostFiltering;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using System.Net;
using System.Text.Json.Serialization;

// The InfoPanel 2.0 engine: a headless, loopback-only ASP.NET Core host that stores and edits 2.0 documents, reads
// sensors, renders every output with the scene runtime and serves the web client. Exit codes: 0 clean, 1 fatal error
// (a data folder whose marker this engine can't honour included), 2 bad arguments, 3 data folder in use.

// A plugin host (the engine starts one per plugin DLL): nothing of the engine runs, not even its command line.
if (InfoPanel.Plugins.Host.PluginHostEntry.IsHostMode(args))
    return await InfoPanel.Plugins.Host.PluginHostEntry.RunAsync(args);

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

// When this run started (the session's `started`): before anything else it does.
var started = DateTimeOffset.UtcNow;

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

// The 1.x folder the 1.x import reads, never writes: a data folder that is it, holds it, or sits inside its profiles or
// assets folder is refused before anything is created (the default layout, InfoPanel/v2 inside it, passes).
var v1Root = options.V1Dir ?? V1Folder.DefaultRoot();
if (V1Folder.FindOverlap(v1Root, paths.Root) is { } overlap)
{
    Console.Error.WriteLine($"InfoPanel: {overlap}");
    return 2;
}

// The data folder's marker, read only, before anything is created there (the lock file included): a folder this engine
// can't honour stops it with the reason on the console and is left exactly as it was. --fresh wipes the folder, marker
// included, so it is checked after the wipe instead.
if (!options.Fresh)
{
    try
    {
        StoreMarker.Preflight(paths);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine($"InfoPanel: {ex.Message}");
        return 1;
    }
}

try
{
    DataFolder.CreatePrivate(paths.Root);
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
{
    // A data folder that is a link to a drive that isn't there, or a file: nothing is created through it.
    Console.Error.WriteLine($"InfoPanel: the data folder {paths.Root} can't be used: {ex.Message}");
    return 1;
}

// Only a regular file is locked: one another engine holds is exit 3; one that can't be locked (a FIFO, a link whose
// target can't be reached) stops start-up with exit 1, nothing created.
var acquired = InstanceLock.TryAcquire(paths.EngineLockFile, out var lockError, out var lockHeld);
if (acquired == null && lockHeld)
{
    // A second launch: the user started InfoPanel again, so an engine that holds the folder and answers on its channel
    // opens the editor (unless --no-browser), and this one exits 3. One that doesn't answer is stopping (it closes its
    // channel before it lets go of the folder): this launch waits for the folder and starts once it is free.
    var handOff = await SecondLaunch.HandOffAsync(paths.EngineLockFile, options.RunDir ?? LocalChannel.DefaultRunDirectory(), paths.Root,
        openEditor: !options.NoBrowser, SecondLaunch.HandOff);
    if (handOff.Opened)
    {
        Console.WriteLine("InfoPanel is already running; opened the editor");
        return 3;
    }

    (acquired, lockError, lockHeld) = (handOff.Lock, handOff.Error ?? lockError, handOff.Held);
}

// Held until the entry point returns, after every service is disposed and the log written out: the folder is let go
// only once nothing of this engine writes there.
using var instanceLock = acquired;
if (instanceLock == null)
{
    Console.Error.WriteLine($"InfoPanel: {lockError}");
    return lockHeld ? 3 : 1;
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

// The marker again, now under the lock, before anything repairs, sweeps or writes there (the log included): a folder
// this engine can't honour stops it with the reason on the console, and nothing in the folder is touched; the lock
// file, once there, is left in place. Written when absent.
try
{
    StoreMarker.Open(paths);
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
{
    Console.Error.WriteLine($"InfoPanel: {ex.Message}");
    return 1;
}

// The log file, opened only as a regular file (the folder created when nothing is there): when it can't be, the engine
// says so once on the console and logs there only, so a FIFO or a link to nothing never blocks or stops start-up.
var logFile = new EngineLogFile(paths.LogsDirectory, message => Console.Error.WriteLine(message));

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
        .WriteTo.Sink(logFile)
        .WriteTo.Sink(recentLog))
    .CreateLogger();
Log.Logger = engineLogger;
// Errors nothing else catches are logged; before an unhandled exception ends the process the log is written out.
var crashLogging = CrashLogging.Install(engineLogger.ForContext<CrashLogging>(), flush: engineLogger.Dispose);

try
{
    if (options.Fresh)
        Log.Information("Wiped the data folder {DataDir} (--fresh)", paths.Root);
    // --record-sensors on a folder whose hwinfo/ or libre/ couldn't be listed: nothing is recorded, and the log says why.
    options.LogRecordingOff(Log.ForContext<EngineOptions>());

    // Video: FFmpeg's decoding libraries, beside the engine, else in INFOPANEL_FFMPEG_DIR (development; a test engine's
    // Engine:FFmpegDir setting replaces both). Without them media.video isn't listed: a video item is unsupported, kept.
    IReadOnlyList<string?> ffmpegFolders = builder.Configuration[$"{EngineOptions.Section}:FFmpegDir"] is { Length: > 0 } ffmpegDir
        ? [ffmpegDir]
        : [.. InfoPanel.Images.Video.FFmpeg.DefaultFolders()];
    var ffmpeg = InfoPanel.Images.Video.FFmpeg.Load(ffmpegFolders, out var noVideo);
    if (ffmpeg is null)
        InfoPanel.Images.Video.FFmpeg.LogUnavailable(noVideo);
    else
        Log.Information("Video playback: FFmpeg {Versions} from {Folder}", ffmpeg.Versions, ffmpeg.Folder);

    // The stores are opened and their blobs swept before anything changes them (behind the marker, checked again); the
    // first run stores the example for this platform and sensor source.
    var workspace = Workspace.Start(paths, new WorkspaceOptions
    {
        Logger = engineLogger.ForContext<Workspace>(),
        // Media providers are configured (the http provider below, and rtsp with FFmpeg); video files play when FFmpeg loaded.
        Support = ffmpeg is null
            ? EngineSupport.Base.WithScene(EngineSupport.MediaProvider)
            : EngineSupport.Base.WithScene(EngineSupport.MediaProvider, EngineSupport.MediaVideo),
        Examples = Workspace.FirstRunExamples(OperatingSystem.IsMacOS(), options.Mock),
        // 1.x data waiting to be migrated brings the user's own profiles: no example then.
        ExamplesWanted = () => !new V1Folder(v1Root).IsPending(paths),
    }, DateTimeOffset.UtcNow);
    // The engine settings: a file that can't be read is logged, leaves the defaults in effect and is never rewritten.
    var settings = SettingsStore.Open(paths, engineLogger.ForContext<SettingsStore>());
    logLevel.MinimumLevel = LevelOf(settings.Current.Settings.LogLevel);
    settings.Changed += snapshot => logLevel.MinimumLevel = LevelOf(snapshot.Settings.LogLevel);
    // Integration tokens: a file that can't be read stops the engine rather than dropping or overwriting them.
    var tokens = TokenStore.Open(paths.TokensFile, paths.Root, TimeProvider.System, engineLogger.ForContext<TokenStore>());
    // The paired clients and the last port: a file that can't be read is logged, the run has none, and it is never rewritten.
    var pairings = PairingStore.Open(paths.PairingsFile, paths.Root, TimeProvider.System, engineLogger.ForContext<PairingStore>());
    // The credential a client provides for the configured service: a private file (0600) on macOS and Linux, a file
    // encrypted for this user (DPAPI) with a private access list on Windows; never in settings.json.
    IServiceCredentialStore serviceStore;
    if (OperatingSystem.IsWindows())
    {
        var windowsFiles = new WindowsPrivateFiles();
        serviceStore = FileServiceCredentialStore.ForProtectedFile(paths.Root, temp => windowsFiles.CreateFile(temp, windowsFiles.CurrentUser),
            engineLogger.ForContext<FileServiceCredentialStore>());
    }
    else
    {
        serviceStore = FileServiceCredentialStore.ForPrivateFile(paths.Root, engineLogger.ForContext<FileServiceCredentialStore>());
    }

    FontRegistry.RegisterEmbedded();
    var userFonts = FontRegistry.RegisterDirectory(paths.FontsDirectory);
    if (userFonts.Count > 0)
        Log.Information("Registered {Count} font families from {Folder}", userFonts.Count, paths.FontsDirectory);

    // The PC's regional format, read once: a profile without a locale draws numbers and dates in it. A change in the
    // operating system's settings shows after a restart.
    var regionalFormat = PcRegionalFormat.Read();
    Log.Information("Regional format: {Tag:l} (decimal '{Decimal:l}', group '{Group:l}')",
        regionalFormat.Tag, regionalFormat.DecimalSeparator, regionalFormat.GroupSeparator);

    // A web client served here, or one served elsewhere that the engine opens (a release build's configured web client).
    var webRoot = DataFolder.FindWebRoot(options.WebRoot);
    if (webRoot != null)
        Log.Information("Serving the web client from {WebRoot}", webRoot);
    else if (options.AllowOrigins.Count > 0)
        Log.Information("No web client installed here; the web client at {Origins} is the editor", string.Join(", ", options.AllowOrigins));
    else
        Log.Warning("No web client installed (no wwwroot next to the engine and no --web-root); serving the API only");

    // --port 0 (the default) prefers the engine's last port, kept in pairings.json, so paired clients find it again; when
    // that port is taken, a free one, and those clients follow the next link the engine opens. An explicit --port wins.
    var port = options.Port;
    if (port == 0)
    {
        port = EngineRuntime.FindFreePort(pairings.Port);
        if (pairings.Port is { } last && last != port)
            Log.Information("Port {Old} is in use; listening on {New}. Paired clients reconnect once they are opened from InfoPanel again", last, port);
    }

    var runtime = new EngineRuntime(port, options.Token, options.DevOrigins, options.AllowOrigins) { Started = started, WebRootServed = webRoot != null };

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
    // The host rule, next: the loopback names and the proxy.hosts setting, read at each request (Security/HostRule.cs).
    // ASP.NET's own host filtering is left open, whatever configuration says, so the rule alone decides.
    builder.Services.AddSingleton(new HostRule(settings));
    builder.Services.Insert(1, ServiceDescriptor.Transient<IStartupFilter, HostRuleStartupFilter>());
    builder.Services.Configure<HostFilteringOptions>(o => o.AllowedHosts = ["*"]);
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
    builder.Services.AddSingleton(pairings);
    builder.Services.AddSingleton(sp => new PairingCodes(sp.GetRequiredService<TimeProvider>(), id => pairings.Find(id) is not null));
    builder.Services.AddSingleton(sp => new PairingProofs(pairings, runtime, workspace.Instance, sp.GetRequiredService<TimeProvider>(),
        engineLogger.ForContext<PairingProofs>()));
    builder.Services.AddSingleton(settings);
    // The configured service: its address is the setting, else the build's web client origin. A change of address removes
    // the credential (and one made while the engine was stopped is removed when it is first made, at start). Every request
    // to the service goes through ServiceClient's rules.
    builder.Services.AddSingleton(serviceStore);
    builder.Services.AddSingleton(sp =>
    {
        string? AddressOf(EngineSettings s) => s.Service.Address ?? options.DefaultServiceAddress;
        var service = new ServiceCredentials(sp.GetRequiredService<IServiceCredentialStore>(),
            () => AddressOf(settings.Current.Settings), engineLogger.ForContext<ServiceCredentials>());
        // A change of the address in effect removes the credential before it is stored, and is refused (503) when it can't.
        // A change that keeps the address doesn't touch the credential, so it never waits for the credential's store.
        settings.Changing = (previous, next) => AddressOf(previous) == AddressOf(next) ? null : service.AddressChanging(AddressOf(next));
        // The address the last change left in effect; Changed is raised under the settings' lock, in order.
        var inEffect = AddressOf(settings.Current.Settings);
        settings.Changed += snapshot =>
        {
            var address = AddressOf(snapshot.Settings);
            if (address == inEffect)
                return;
            inEffect = address;
            service.AddressChanged();
        };
        return service;
    });
    builder.Services.AddSingleton(sp => new ServiceClient(sp.GetRequiredService<ServiceCredentials>(), loopbackOnly: options.Offline,
        sp.GetRequiredService<TimeProvider>()));
    // A credential replaced by another for the same address: the service is asked once, in the background, to forget it;
    // dropped when the engine stops, whose disposal of its services waits for it briefly (before the client's).
    builder.Services.AddSingleton(sp => new ServiceRelease(sp.GetRequiredService<ServiceClient>(),
        sp.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping, engineLogger.ForContext<ServiceRelease>()));
    // Pairing through the configured service: a pass it signed, checked against its public keys (fetched from its address
    // without a credential, under ServiceClient's rules) and the credential's id, while service.pairing is on.
    builder.Services.AddSingleton(sp => new PairingKeys(() => sp.GetRequiredService<ServiceCredentials>().Address, loopbackOnly: options.Offline,
        sp.GetRequiredService<TimeProvider>(), engineLogger.ForContext<PairingKeys>()));
    builder.Services.AddSingleton(sp => new PairingPasses(settings, sp.GetRequiredService<ServiceCredentials>(), sp.GetRequiredService<PairingKeys>(),
        sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<PairingPasses>>()));
    // Who holds the credential, asked of the service for the menu's signed-in line (the shell starts and stops it); kept in memory only.
    builder.Services.AddSingleton(sp => new ServiceHolder(sp.GetRequiredService<ServiceClient>(), sp.GetRequiredService<ServiceCredentials>(),
        sp.GetRequiredService<TimeProvider>(), engineLogger.ForContext<ServiceHolder>()));
    builder.Services.AddSingleton(recentLog);
    builder.Services.AddSingleton(regionalFormat);
    // The 1.x import: its limits (tests lower them), the 1.x folder, and one import read at a time.
    builder.Services.AddSingleton(ImportLimits.Default);
    builder.Services.AddSingleton(sp => new V1Folder(v1Root, sp.GetRequiredService<ImportLimits>()));
    builder.Services.AddSingleton<ImportGate>();
    // Made by a factory so the container disposes it, removing its handlers, when the host stops; the finally below
    // covers a start that fails before the host is built.
    builder.Services.AddSingleton(_ => crashLogging);
    // Made by a factory so the container disposes it (and every open document) after the services that use it.
    builder.Services.AddSingleton(_ => workspace);
    builder.Services.AddSingleton(TimeProvider.System);
    // Mock mode: the mock sensors and simulated unknown keys (tests, demos). Otherwise the platform's real sensors (the
    // macOS source; on Windows HWiNFO through its shared memory, then its gadget values in the registry) and unknown keys
    // read as unavailable.
    if (options.ReplaySensors is { } recording)
    {
        // A recording of a PC's sensors, replayed in a loop under its sources' ids and keys (dev and test; the session says mock):
        // the Windows PC's sources in its order, HWiNFO's from the recording, its gadget values with no key (not recorded).
        builder.Services.AddSingleton<ISensorSource>(sp => SensorReplay.CreateHwInfo(recording, sp.GetRequiredService<TimeProvider>()));
        builder.Services.AddSingleton<ISensorSource>(sp => SensorReplay.CreateGadget(sp.GetRequiredService<TimeProvider>()));
        // Libre's sensor helper as recorded, its settings applied as on the PC, when the recording holds it; its PawnIO
        // driver as the recorded helper said, with no installer to run (not this computer).
        if (SensorRecording.HoldsLibre(recording))
        {
            builder.Services.AddSingleton(sp =>
            {
                var libre = SensorReplay.CreateLibre(recording, () => settings.Current.Settings.Libre, sp.GetRequiredService<TimeProvider>());
                settings.Changed += _ => libre.SettingsChanged();
                return libre;
            });
            builder.Services.AddSingleton<ISensorSource>(sp => sp.GetRequiredService<LibreSource>());
            builder.Services.AddSingleton(sp => new PawnIoInstaller(PawnIoInstaller.DefaultPath, new ShellPawnIoLauncher(),
                sp.GetRequiredService<LibreSource>().DriverChanged, supported: false, sp.GetRequiredService<TimeProvider>()));
            // Reading all sensors: nothing to set up in a replay (not this computer).
            builder.Services.AddSingleton(sp => new LibreElevation(LibreHelperProcess.DefaultPath, system: null, new ShellPawnIoLauncher(),
                sp.GetRequiredService<LibreSource>().HelperChanged, sp.GetRequiredService<TimeProvider>()));
        }
    }
    else if (options.Mock)
    {
        builder.Services.AddSingleton(sp => new MockSensorSource(options.MockSeed, sp.GetRequiredService<TimeProvider>()));
        builder.Services.AddSingleton<ISensorSource>(sp => sp.GetRequiredService<MockSensorSource>());
    }
    else if (OperatingSystem.IsMacOS())
    {
        builder.Services.AddSingleton<ISensorSource>(sp => MacSensorSource.Create(sp.GetRequiredService<TimeProvider>()));
    }
    else if (OperatingSystem.IsWindows())
    {
        builder.Services.AddSingleton<ISensorSource>(sp =>
        {
            var time = sp.GetRequiredService<TimeProvider>();
            // --record-sensors: what HWiNFO's source copies, for --record-seconds, into a recording --replay-sensors plays.
            var recorder = options.RecordSensors is { } folder
                ? new HwInfoRecorder(folder, SensorRecording.MaxRounds, options.Scrub, HwInfoSource.Interval, time.GetUtcNow(), TimeSpan.FromSeconds(options.RecordSeconds), time)
                : null;
            return HwInfoSource.CreateWindows(time, recorder);
        });
        // HWiNFO's gadget values (the readings ticked "Report value in Gadget"), read from the user's registry.
        builder.Services.AddSingleton<ISensorSource>(sp => HwInfoGadgetSource.CreateWindows(sp.GetRequiredService<TimeProvider>()));
        // LibreHardwareMonitor's sensors, after HWiNFO's (1.4.x's start order): the sensor helper in its own folder beside the engine, started
        // by the source and run by sources.libre.* as they change; --record-sensors records its answers too.
        builder.Services.AddSingleton(sp =>
        {
            var time = sp.GetRequiredService<TimeProvider>();
            var recorder = options.RecordSensors is { } folder
                ? new LibreRecorder(folder, SensorRecording.MaxRounds, options.Scrub, LibreSource.Interval, time.GetUtcNow(), TimeSpan.FromSeconds(options.RecordSeconds), time)
                : null;
            var libre = LibreSource.CreateWindows(() => settings.Current.Settings.Libre, time, recorder, sp.GetRequiredService<LibreElevation>());
            // A change is heard at once: Off stops the helper even while a request to it hangs.
            settings.Changed += _ => libre.SettingsChanged();
            return libre;
        });
        builder.Services.AddSingleton<ISensorSource>(sp => sp.GetRequiredService<LibreSource>());
        // The PawnIO installer bundled beside the engine (pawnio/PawnIO_setup.exe, its SHA-256 pinned), run only when a
        // session asks (POST /api/sensors/sources/libre/driver); an install that ends well starts a new sensor helper.
        builder.Services.AddSingleton(sp => new PawnIoInstaller(PawnIoInstaller.DefaultPath, new ShellPawnIoLauncher(),
            sp.GetRequiredService<LibreSource>().DriverChanged, supported: true, sp.GetRequiredService<TimeProvider>()));
        // Reading all sensors (POST/DELETE /api/sensors/sources/libre/elevation): the sensor helper's task, run as SYSTEM
        // for the engine's user, set up or removed through this install's own helper run with runas when a session asks;
        // each helper start runs that task when it names this helper and the engine is unelevated, else the child.
        builder.Services.AddSingleton(sp => new LibreElevation(LibreHelperProcess.DefaultPath, OperatingSystem.IsWindows() ? new WindowsLibreElevationSystem() : null, new ShellPawnIoLauncher(),
            () => sp.GetRequiredService<LibreSource>().HelperChanged(), sp.GetRequiredService<TimeProvider>()));
    }
    // The engine's own sources on every OS (the clock, the drives, the network, the weather), outside mock mode and a replay.
    builder.Services.AddBuiltInSources(options);
    // Plugin sensors, after the built-in sources; their hosts start once the engine listens.
    builder.Services.AddPlugins(options, paths, settings);

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
    // blob stored after a lookup of it failed is read again, so its missing-media placeholders repair; a stored blob's
    // inspection gives the cache its size, so a picture still decoding is laid out at the size it will be drawn at.
    // Fetched web images are decoded by the same cache, from memory (FetchedMediaStore), under keys no asset has.
    var fetched = new FetchedMediaStore();
    builder.Services.AddSingleton(_ =>
    {
        var media = new MediaSampleCache(new EngineMediaStore(new BlobMediaStore(workspace.Blobs), fetched), new MediaSampleCacheOptions
        {
            Limits = workspace.Limits,
            Video = new InfoPanel.Images.Video.VideoOptions { Decoder = ffmpeg },
        });
        workspace.BlobStored += media.Retry;
        return media;
    });
    // The http provider (spec/docs/providers.md): it reads the media.localNetwork setting for every connection, and a
    // change of it drops what it fetched, so every web image is fetched again under the new rule. --offline keeps every
    // connection on this computer.
    builder.Services.AddSingleton(sp =>
    {
        var http = new HttpMediaProvider(sp.GetRequiredService<MediaSampleCache>(), fetched, new HttpMediaProviderOptions
        {
            Limits = workspace.Limits,
            LocalNetwork = () => settings.Current.Settings.Media.LocalNetwork,
            LoopbackOnly = options.Offline,
            UserAgent = $"InfoPanel/{ReadEndpoints.EngineVersion()}",
        });
        var localNetwork = settings.Current.Settings.Media.LocalNetwork;
        var changing = new Lock();
        settings.Changed += snapshot =>
        {
            lock (changing)
            {
                if (localNetwork == snapshot.Settings.Media.LocalNetwork) return;
                localNetwork = snapshot.Settings.Media.LocalNetwork;
            }
            http.Reset();
        };
        return http;
    });
    // The rtsp provider (spec/docs/providers.md), only with FFmpeg: camera streams decoded as videos are, every connection
    // checked as the http provider's are, and dropped on a change of the local network setting as its fetches are.
    if (ffmpeg is not null && InfoPanel.Images.Video.RtspProvider.Available(ffmpeg))
    {
        builder.Services.AddSingleton(sp =>
        {
            var rtsp = new InfoPanel.Images.Video.RtspProvider(ffmpeg, sp.GetRequiredService<MediaSampleCache>(), new InfoPanel.Images.Video.RtspProviderOptions
            {
                Limits = workspace.Limits,
                LocalNetwork = () => settings.Current.Settings.Media.LocalNetwork,
                LoopbackOnly = options.Offline,
                UserAgent = $"InfoPanel/{ReadEndpoints.EngineVersion()}",
            });
            var localNetwork = settings.Current.Settings.Media.LocalNetwork;
            var changing = new Lock();
            settings.Changed += snapshot =>
            {
                lock (changing)
                {
                    if (localNetwork == snapshot.Settings.Media.LocalNetwork) return;
                    localNetwork = snapshot.Settings.Media.LocalNetwork;
                }
                rtsp.Reset();
            };
            return rtsp;
        });
    }
    builder.Services.AddSingleton(sp => new SceneRuntimeOptions
    {
        Inputs = new SensorSceneInputs(sp.GetRequiredService<ISensorReader>(), sp.GetRequiredService<ISensorHistory>()),
        Media = sp.GetRequiredService<MediaSampleCache>(),
        Providers = sp.GetService<InfoPanel.Images.Video.RtspProvider>() is { } rtsp
            ? new Dictionary<string, IMediaProvider> { [HttpMediaProvider.Name] = sp.GetRequiredService<HttpMediaProvider>(), [InfoPanel.Images.Video.RtspProvider.Name] = rtsp }
            : new Dictionary<string, IMediaProvider> { [HttpMediaProvider.Name] = sp.GetRequiredService<HttpMediaProvider>() },
        Limits = workspace.Limits,
        Support = workspace.Support,
        Locale = sp.GetRequiredService<RegionalFormat>(),
    });
    // Live frames for /ws and desktop windows; disposed with the host, which stops every render loop.
    builder.Services.AddSingleton(sp => new RenderService(
        sp.GetRequiredService<Workspace>(),
        sp.GetRequiredService<SceneRuntimeOptions>(),
        sp.GetRequiredService<TimeProvider>(),
        sp.GetService<RenderServiceOptions>()));
    builder.Services.AddSingleton(new PreviewSocketOptions());
    // The preview's H.264 encoders: the hardware one, then the user's own ffmpeg (--ffmpeg, PATH, then the folders package
    // managers use), each probed only when a video subscription first reaches it.
    builder.Services.AddSingleton(sp => H264Backends.ForThisComputer(options.H264, sp.GetRequiredService<ILoggerFactory>().CreateLogger("InfoPanel.Preview.Video"),
        FfmpegDiscovery.ForThisComputer(options.Ffmpeg)));
    // The engine's stop cancels a probe still running and waits for it to kill and collect its ffmpeg.
    builder.Services.AddHostedService<H264Shutdown>();
    // Links the engine opens go to the browser through a private file in the run folder, never in a process's arguments.
    var runDirectory = options.RunDir ?? LocalChannel.DefaultRunDirectory();
    builder.Services.AddSingleton(sp => new RedirectFiles(runDirectory, LocalChannel.HashOf(paths.Root), sp.GetRequiredService<TimeProvider>(),
        sp.GetRequiredService<ILogger<RedirectFiles>>()));
    builder.Services.AddSingleton(sp => new BrowserLauncher(sp.GetRequiredService<ILogger<BrowserLauncher>>(), sp.GetRequiredService<RedirectFiles>()));
    builder.Services.AddSingleton<EditorOpener>();
    builder.Services.AddSingleton<IFolderOpener>(sp => new FolderOpener(sp.GetRequiredService<ILogger<FolderOpener>>()));
    builder.Services.AddHostedService<SensorService>();
    builder.Services.AddHostedService<EngineLifetime>();
    // The data folder's local channel, for a second launch on this computer: opened once the engine is up.
    builder.Services.AddSingleton<LocalChannelService>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<LocalChannelService>());
    // AI apps elsewhere (docs/2.0/relay.md): with service.aiApps on, one connection to the configured service's relay, under
    // ServiceClient's rules; its calls reach the tool host and nothing else. The tools run here with read or edit, never the session's powers.
    builder.Services.AddSingleton<InfoPanel.Mcp.Tools.McpToolHost>();
    builder.Services.AddSingleton(sp => new RelayClient(sp.GetRequiredService<ServiceCredentials>(), sp.GetRequiredService<InfoPanel.Mcp.Tools.McpToolHost>(),
        new RelayOptions { EngineVersion = ReadEndpoints.EngineVersion(), LoopbackOnly = options.Offline }, sp.GetRequiredService<TimeProvider>(),
        engineLogger.ForContext<RelayClient>()));
    builder.Services.AddHostedService<InfoPanel.Mcp.RelayHostedService>();

    // Desktop windows and the menu-bar item: on macOS the main thread runs AppKit and the host runs on a worker (DesktopHost).
    var desktop = DesktopHost.Create(options);
    // macOS: the app delegate goes in now, on the main thread before the AppKit loop runs, so an infopanel: link that
    // started the app (delivered while AppKit finishes launching) isn't lost.
    if (OperatingSystem.IsMacOS() && desktop.Shell is InfoPanel.Shell.MacOS.MacShellPlatform macShell)
        macShell.InstallDelegate();
    builder.Services.AddSingleton(desktop.Platform);
    builder.Services.AddSingleton(desktop.Shell);
    builder.Services.AddHostedService<InfoPanel.Shell.ShellService>();
    builder.Services.AddSingleton<DisplayWindowManager>();
    builder.Services.AddSingleton<DisplayLister>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<DisplayWindowManager>());
    // USB panels (docs/2.0/panels.md): after the render service, so the host stops them, each screen turned off, first.
    builder.Services.AddSingleton(sp => new InfoPanel.Panels.PanelService(options, paths, sp.GetRequiredService<Workspace>(),
        sp.GetRequiredService<RenderService>(), settings, sp.GetRequiredService<TimeProvider>(), engineLogger.ForContext<InfoPanel.Panels.PanelService>(),
        sp.GetService<InfoPanel.Panels.PanelServiceOptions>()));
    builder.Services.AddHostedService(sp => sp.GetRequiredService<InfoPanel.Panels.PanelService>());

    var app = builder.Build();
    // Opened now, so a credential kept for another address than the one in effect is removed at start.
    app.Services.GetRequiredService<ServiceCredentials>();

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
    app.MapImportEndpoints();
    app.MapTokenEndpoints();
    app.MapPairingEndpoints();
    app.MapSettingsEndpoints();
    app.MapServiceEndpoints();
    app.MapLogEndpoints();
    app.MapFolderEndpoints();
    app.MapPluginEndpoints();
    app.MapDisplayEndpoints();
    app.MapDeviceEndpoints();
    app.MapPreviewSocket();
    app.MapClientFallback(webRoot);

    // A launch an infopanel: link asked for (its only argument, never read) that started the engine: the editor opens
    // once, when the server listens, as Open Editor does.
    if (options.FromLink)
        app.Lifetime.ApplicationStarted.Register(() =>
        {
            Log.Information("InfoPanel was opened from a link: opening the editor");
            var editor = app.Services.GetRequiredService<EditorOpener>();
            _ = Task.Run(() =>
            {
                try
                {
                    editor.Open();
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "The editor couldn't be opened");
                }
            });
        });

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
