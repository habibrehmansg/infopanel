using System.Net;
using System.Net.Sockets;
using InfoPanel.Hosting;
using InfoPanel.Models;
using InfoPanel.Sensors;
using InfoPanel.Settings;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace InfoPanel.Tests;

public class EngineStartupTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task LogFile_HasTheSignInLinkRedacted_AndFoldersAreOwnerOnly()
    {
        using var factory = new EngineFactory();
        using (var client = factory.CreateApiClient())
        using (var response = await client.GetAsync("/api/session", Ct))
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var logs = Path.Combine(factory.DataDir, "logs");
        var text = "";
        await WaitUntil(() =>
        {
            text = string.Join("\n", Directory.GetFiles(logs).Select(ReadShared));
            return text.Contains("Open the editor") && text.Contains("Pair the client");
        });

        Assert.Contains("/auth/<token>", text);
        // The pairing link for the cross origin is logged to the file redacted too.
        Assert.Contains($"{EngineFactory.CrossOrigin}/#engine=http://127.0.0.1:{EngineFactory.Port}&token=<token>", text);
        Assert.DoesNotContain(EngineFactory.Token, text);
        if (!OperatingSystem.IsWindows())
        {
            const UnixFileMode ownerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            Assert.Equal(ownerOnly, File.GetUnixFileMode(factory.DataDir));
            Assert.Equal(ownerOnly, File.GetUnixFileMode(logs));
        }
    }

    [Fact]
    public async Task TheSettings_AreOpenedAtStartUp_AndAFirstRunWritesNoSettingsFile()
    {
        using var factory = new EngineFactory();
        using (var client = factory.CreateApiClient())
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/session", Ct)).StatusCode);

        var store = factory.Services.GetRequiredService<SettingsStore>();
        Assert.Equal((SettingsStatus.Ok, 15), (store.Status, store.Current.Settings.FrameRate));
        Assert.Equal(Path.Combine(factory.DataDir, "settings.json"), store.Path);
        Assert.False(File.Exists(store.Path));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("""{"format":"infopanel.settings/1","logLevel":"\uD800"}""")] // a lone surrogate: no text to read
    public async Task AnUnreadableSettingsFile_DoesntStopTheEngine_IsLogged_AndIsLeftAsItIs(string content)
    {
        using var factory = new EngineFactory();
        Directory.CreateDirectory(factory.DataDir);
        var path = Path.Combine(factory.DataDir, "settings.json");
        File.WriteAllText(path, content);

        using (var client = factory.CreateApiClient())
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/session", Ct)).StatusCode);

        var store = factory.Services.GetRequiredService<SettingsStore>();
        Assert.Equal((SettingsStatus.Unreadable, "json"), (store.Status, store.Problem!.Code));
        Assert.Equal(15, store.Current.Settings.FrameRate);
        Assert.Equal(content, File.ReadAllText(path));
        var log = "";
        await WaitUntil(() =>
        {
            log = string.Join("\n", Directory.GetFiles(Path.Combine(factory.DataDir, "logs")).Select(ReadShared));
            return log.Contains("The settings file");
        });
        Assert.Single(log.Split('\n'), line => line.Contains("The settings file") && line.Contains("[ERR]"));
    }

    [Fact]
    public async Task AFirstStart_WritesTheDataFolderMarker()
    {
        using var factory = new EngineFactory();
        using (var client = factory.CreateApiClient())
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/session", Ct)).StatusCode);

        Assert.Equal("""{"format":"infopanel.store/1","requires":[]}""", File.ReadAllText(Path.Combine(factory.DataDir, "store.json")));
    }

    /// <summary>
    /// The engine itself, as a child process, on a data folder whose marker it can't honour: it stops with exit 1 and a
    /// message naming why, before it writes anything there (no log, no repair of the leftovers, no sweep of the old
    /// unreferenced blob, no first-run example): the folder is byte for byte as it was.
    /// </summary>
    [Theory]
    [InlineData("""{"format":"infopanel.store/1","requires":["test.unknown"]}""", "test.unknown")]
    [InlineData("""{"format":"infopanel.store/2","requires":[]}""", "infopanel.store/2")]
    [InlineData("{ not json", "can't be read")]
    public async Task AMarkerThisEngineCantHonour_StopsStartUp_WithExit1_AndWritesNothing(string marker, string named)
    {
        var root = Path.Combine(Path.GetTempPath(), "infopanel-marker-" + Guid.NewGuid().ToString("N"));
        var data = Path.Combine(root, "data");
        try
        {
            const string profile = "0a000000-0000-4000-8000-000000000000";
            Directory.CreateDirectory(Path.Combine(data, "profiles", profile));
            Directory.CreateDirectory(Path.Combine(data, "profiles", $"{profile}.{new string('1', 32)}.tmp"));
            Directory.CreateDirectory(Path.Combine(data, "blobs"));
            File.WriteAllText(Path.Combine(data, "profiles", profile, "document.json"), """{"format":"infopanel.document/1"}""");
            File.WriteAllText(Path.Combine(data, "profiles.json"), """{"order":["0b000000-0000-4000-8000-000000000000"]}""");
            File.WriteAllText(Path.Combine(data, $"profiles.json.{new string('2', 32)}.tmp"), "x");
            File.WriteAllText(Path.Combine(data, $"tokens.json.{new string('3', 32)}.tmp"), "x");
            var blob = Path.Combine(data, "blobs", Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData("old"u8)));
            File.WriteAllText(blob, "old");
            File.SetLastWriteTimeUtc(blob, DateTime.UtcNow.AddDays(-30));
            File.WriteAllText(Path.Combine(data, "engine.lock"), "");
            File.WriteAllText(Path.Combine(data, "store.json"), marker);
            var before = TreeHash(data);

            var (exitCode, said) = await RunUntilItStops(data, before);
            Assert.Equal(1, exitCode);
            Assert.Contains(named, said);
            Assert.Contains("store.json", said);
            Assert.Equal(before, TreeHash(data));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// The same with no lock file yet: the marker is checked, read only, before the lock is created, so a refused start
    /// leaves no <c>engine.lock</c> behind either.
    /// </summary>
    [Fact]
    public async Task AMarkerThisEngineCantHonour_WithNoLockYet_LeavesNoLockBehind()
    {
        var root = Path.Combine(Path.GetTempPath(), "infopanel-marker-" + Guid.NewGuid().ToString("N"));
        var data = Path.Combine(root, "data");
        try
        {
            Directory.CreateDirectory(data);
            File.WriteAllText(Path.Combine(data, "store.json"), """{"format":"infopanel.store/2","requires":[]}""");
            var before = TreeHash(data);

            var (exitCode, said) = await RunUntilItStops(data, before);

            Assert.Equal(1, exitCode);
            Assert.Contains("infopanel.store/2", said);
            Assert.False(File.Exists(Path.Combine(data, "engine.lock")));
            Assert.Equal(before, TreeHash(data));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// A data folder inside the 1.x folder's profiles (or assets) folder, or the 1.x folder itself: start-up stops with
    /// exit 2 (bad arguments) and a message naming both, before anything is created.
    /// </summary>
    [Theory]
    [InlineData("profiles/v2")]
    [InlineData("assets/v2")]
    [InlineData("")]
    public async Task ADataFolderThatOverlapsThe1xFolder_StopsStartUp_WithExit2_AndCreatesNothing(string inside)
    {
        var root = Path.Combine(Path.GetTempPath(), "infopanel-overlap-" + Guid.NewGuid().ToString("N"));
        var v1 = Path.Combine(root, "v1");
        try
        {
            Directory.CreateDirectory(Path.Combine(v1, "profiles"));
            Directory.CreateDirectory(Path.Combine(v1, "assets"));
            File.WriteAllText(Path.Combine(v1, "profiles.xml"), "<ArrayOfProfile />");
            var data = inside.Length == 0 ? v1 : Path.Combine(v1, inside);
            var before = TreeHash(root);

            var (exitCode, said) = await RunUntilItStops(data, () => TreeHash(root), before, v1);

            Assert.True(exitCode == 2, $"exit {exitCode}: {said}");
            Assert.Contains("1.x folder", said);
            Assert.Equal(before, TreeHash(root));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Runs the engine as a child process on <paramref name="data"/> until it exits: its exit code and everything it
    /// said. It fails at once when the folder changes from <paramref name="before"/> while it runs (an engine that doesn't
    /// stop writes there within moments), or after a minute.
    /// </summary>
    private static Task<(int ExitCode, string Said)> RunUntilItStops(string data, string before) =>
        RunUntilItStops(data, () => TreeHash(data), before);

    /// <inheritdoc cref="RunUntilItStops(string, string)"/>
    /// <param name="snapshot">What must not change while it runs (<paramref name="before"/> is its value before).</param>
    /// <param name="v1">The engine's 1.x folder (<see cref="EngineProcess"/>).</param>
    private static async Task<(int ExitCode, string Said)> RunUntilItStops(string data, Func<string> snapshot, string before, string? v1 = null)
    {
        using var engine = System.Diagnostics.Process.Start(EngineProcess(data, EngineRuntime.FindFreePort(), v1))!;
        var output = engine.StandardOutput.ReadToEndAsync(Ct);
        var errors = engine.StandardError.ReadToEndAsync(Ct);
        try
        {
            // An engine that doesn't stop writes its log within moments: fail then rather than wait it out.
            var deadline = Environment.TickCount64 + 60_000;
            while (!engine.HasExited)
            {
                if (Environment.TickCount64 > deadline || snapshot() != before)
                    Assert.Fail($"The engine didn't stop before writing to the data folder.");
                await Task.Delay(50, Ct);
            }
        }
        finally
        {
            if (!engine.HasExited)
                engine.Kill(entireProcessTree: true);
            await engine.WaitForExitAsync(Ct);
        }

        return (engine.ExitCode, await output + await errors);
    }

    /// <summary>
    /// The engine as a child process on <paramref name="data"/> and <paramref name="port"/>, its output read. Its 1.x folder is
    /// <paramref name="v1"/>, else one beside the data folder that isn't created: a test engine never reads the 1.x data of
    /// the user running the tests.
    /// </summary>
    private static System.Diagnostics.ProcessStartInfo EngineProcess(string data, int port, string? v1 = null)
    {
        var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "InfoPanel.exe" : "InfoPanel"))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in new[] { "--data-dir", data, "--v1-dir", v1 ?? data + "-v1", "--port", port.ToString(), "--token", EngineFactory.Token, "--no-browser", "--no-windows", "--no-tray", "--no-panels", "--mock" })
            start.ArgumentList.Add(arg);
        return start;
    }

    /// <summary>
    /// Runs the engine as a child process on <paramref name="data"/> until it answers <c>/healthz</c> (then stops it), it
    /// exits, or <see cref="StartLimit"/> passes (it is then killed: blocked, say): whether it answered, and everything it said.
    /// </summary>
    /// <summary>
    /// The most the engine is given to answer: a sanity limit, not the behaviour under test (a start held up by its log
    /// target would be held for good). Under parallel full runs this Mac's program launches and file opens can stall for
    /// tens of seconds (its endpoint protection authorises each one); 30 s didn't always cover the engine's start. The wait
    /// still ends as soon as it answers or exits.
    /// </summary>
    private static readonly TimeSpan StartLimit = TimeSpan.FromSeconds(120);

    private static async Task<(bool Listening, string Said)> RunUntilListening(string data)
    {
        var port = EngineRuntime.FindFreePort();
        using var engine = System.Diagnostics.Process.Start(EngineProcess(data, port))!;
        var output = engine.StandardOutput.ReadToEndAsync(Ct);
        var errors = engine.StandardError.ReadToEndAsync(Ct);
        var listening = false;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        try
        {
            var deadline = Environment.TickCount64 + (long)StartLimit.TotalMilliseconds;
            while (!listening && !engine.HasExited && Environment.TickCount64 < deadline)
            {
                try
                {
                    using var response = await http.GetAsync($"http://127.0.0.1:{port}/healthz", Ct);
                    listening = response.IsSuccessStatusCode;
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !Ct.IsCancellationRequested)
                {
                }
                if (!listening)
                    await Task.Delay(100, Ct);
            }
        }
        finally
        {
            if (!engine.HasExited)
                engine.Kill(entireProcessTree: true);
            await engine.WaitForExitAsync(Ct);
        }

        return (listening, await output + await errors);
    }

    /// <summary>A <c>pairings.json</c> with one pairing and the engine's last port, as an engine that paired a client leaves it.</summary>
    private static void WritePairings(string data, int port)
    {
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "pairings.json"),
            $$"""{"pairings":[{"id":"0123456789abcdef0123456789abcdef","name":"Chrome","created":"2026-11-02T09:30:00Z","secret":"{{new string('A', 43)}}"}],"port":{{port}}}""");
    }

    private static int? StoredPort(string data)
    {
        var file = Path.Combine(data, "pairings.json");
        if (!File.Exists(file))
            return null;
        using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));
        return json.RootElement.TryGetProperty("port", out var port) ? port.GetInt32() : null;
    }

    /// <summary>
    /// Runs the engine as a child process on <paramref name="data"/> with <c>--port</c> <paramref name="port"/> until it has
    /// written <c>runtime.json</c> and answers <c>/healthz</c> on the port it names (then kills it), or 30 seconds pass: the
    /// port it listened on (0 when it never did) and everything it said.
    /// </summary>
    private static async Task<(int Port, string Said)> RunUntilItListensOn(string data, int port)
    {
        using var engine = System.Diagnostics.Process.Start(EngineProcess(data, port))!;
        var output = engine.StandardOutput.ReadToEndAsync(Ct);
        var errors = engine.StandardError.ReadToEndAsync(Ct);
        var listening = 0;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        try
        {
            var deadline = Environment.TickCount64 + 30_000;
            var runtime = Path.Combine(data, "runtime.json");
            while (listening == 0 && !engine.HasExited && Environment.TickCount64 < deadline)
            {
                try
                {
                    using var json = System.Text.Json.JsonDocument.Parse(ReadShared(runtime));
                    var named = json.RootElement.GetProperty("port").GetInt32();
                    using var response = await http.GetAsync($"http://127.0.0.1:{named}/healthz", Ct);
                    if (response.IsSuccessStatusCode)
                        listening = named;
                }
                catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or HttpRequestException or TaskCanceledException && !Ct.IsCancellationRequested)
                {
                }
                if (listening == 0)
                    await Task.Delay(100, Ct);
            }
        }
        finally
        {
            if (!engine.HasExited)
                engine.Kill(entireProcessTree: true);
            await engine.WaitForExitAsync(Ct);
        }

        return (listening, await output + await errors);
    }

    [Fact]
    public async Task AfterAPairing_PortZero_ListensOnTheRememberedPort()
    {
        using var temp = new TempFolder();
        var data = Path.Combine(temp.Path, "data");
        var remembered = EngineRuntime.FindFreePort();
        WritePairings(data, remembered);

        var (port, said) = await RunUntilItListensOn(data, 0);

        Assert.True(port == remembered, said);
        Assert.Equal(remembered, StoredPort(data));
    }

    [Fact]
    public async Task WhenTheRememberedPortIsBusy_PortZero_ListensOnAnother_SaysSo_AndRemembersIt()
    {
        using var temp = new TempFolder();
        var data = Path.Combine(temp.Path, "data");
        var busy = new TcpListener(IPAddress.Loopback, 0);
        busy.Start();
        try
        {
            var remembered = ((IPEndPoint)busy.LocalEndpoint).Port;
            WritePairings(data, remembered);

            var (port, said) = await RunUntilItListensOn(data, 0);

            Assert.True(port > 0 && port != remembered, said);
            Assert.Contains($"Port {remembered} is in use; listening on {port}", said);
            Assert.Equal(port, StoredPort(data));
        }
        finally
        {
            busy.Stop();
        }
    }

    [Fact]
    public async Task AnExplicitPort_WinsOverTheRememberedOne()
    {
        using var temp = new TempFolder();
        var data = Path.Combine(temp.Path, "data");
        WritePairings(data, EngineRuntime.FindFreePort());
        var explicitPort = EngineRuntime.FindFreePort();

        var (port, said) = await RunUntilItListensOn(data, explicitPort);

        Assert.True(port == explicitPort, said);
    }

    [Fact]
    public async Task WithoutAPairing_NoPairingsFileIsWritten()
    {
        using var temp = new TempFolder();
        var data = Path.Combine(temp.Path, "data");

        var (port, said) = await RunUntilItListensOn(data, 0);

        Assert.True(port > 0, said);
        Assert.False(File.Exists(Path.Combine(data, "pairings.json")));
    }

    /// <summary>A temp folder of its own, removed after.</summary>
    private sealed class TempFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "infopanel-port-tests-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Today's log file is a FIFO with nothing reading it, or <c>logs/</c> a link to a drive that isn't there: the engine
    /// starts anyway (the file log can't block it or stop it), says once on the console that it logs there only, and
    /// leaves what is at the path as it was.
    /// </summary>
    [Theory]
    [InlineData("a FIFO at today's log")]
    [InlineData("logs a link to nothing")]
    public async Task ALogTargetThatIsntAFile_DoesntHoldUpStartUp_AndTheConsoleSaysSo(string what)
    {
        if (OperatingSystem.IsWindows())
            return; // no FIFOs in folders, and symbolic links need privileges
        var root = Path.Combine(Path.GetTempPath(), "infopanel-logs-" + Guid.NewGuid().ToString("N"));
        var data = Path.Combine(root, "data");
        var logs = Path.Combine(data, "logs");
        try
        {
            Directory.CreateDirectory(data);
            if (what == "a FIFO at today's log")
            {
                Directory.CreateDirectory(logs);
                InfoPanel.Core.Tests.Fifo.Create(Path.Combine(logs, $"infopanel-{DateTime.Now:yyyyMMdd}.log"));
            }
            else
            {
                Directory.CreateSymbolicLink(logs, Path.Combine(root, "unmounted", "logs"));
            }

            var (listening, said) = await RunUntilListening(data);

            Assert.True(listening, $"The engine didn't start:\n{said}");
            Assert.Single(said.Split('\n'), line => line.Contains("logs") && line.Contains("console only"));
            if (what == "a FIFO at today's log")
                Assert.Equal(0, new FileInfo(Path.Combine(logs, $"infopanel-{DateTime.Now:yyyyMMdd}.log")).Length);
            else
                Assert.False(Directory.Exists(Path.Combine(root, "unmounted")));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// The data folder itself a link to a drive that isn't there: start-up stops with exit 1 and a message naming it, and
    /// nothing is created through the link.
    /// </summary>
    [Fact]
    public async Task ADataFolderLinkToNothing_StopsStartUp_WithExit1_AndCreatesNothing()
    {
        if (OperatingSystem.IsWindows())
            return; // symbolic links need privileges there
        var root = Path.Combine(Path.GetTempPath(), "infopanel-root-" + Guid.NewGuid().ToString("N"));
        var data = Path.Combine(root, "data");
        var target = Path.Combine(root, "unmounted", "data");
        try
        {
            Directory.CreateDirectory(root);
            Directory.CreateSymbolicLink(data, target);
            string Snapshot() => $"{Directory.Exists(Path.Combine(root, "unmounted"))}|{new FileInfo(data).LinkTarget}";
            var before = Snapshot();

            var (exitCode, said) = await RunUntilItStops(data, Snapshot, before);

            Assert.Equal(1, exitCode);
            Assert.Contains(data, said);
            Assert.Equal(before, Snapshot());
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// <c>engine.lock</c> a FIFO, or a link to a file that isn't there: the folder can't be locked, so start-up stops with
    /// exit 1 (not 3: no other engine holds it) and a message naming the lock, writes nothing, and creates no file through
    /// the link.
    /// </summary>
    [Theory]
    [InlineData("a FIFO")]
    [InlineData("a link to nothing")]
    public async Task ALockThatIsntARegularFile_StopsStartUp_WithExit1_AndWritesNothing(string what)
    {
        if (OperatingSystem.IsWindows())
            return; // no FIFOs in folders, and symbolic links need privileges
        var root = Path.Combine(Path.GetTempPath(), "infopanel-lock-" + Guid.NewGuid().ToString("N"));
        var data = Path.Combine(root, "data");
        var elsewhere = Path.Combine(root, "elsewhere");
        try
        {
            Directory.CreateDirectory(data);
            Directory.CreateDirectory(elsewhere);
            File.WriteAllText(Path.Combine(data, "store.json"), """{"format":"infopanel.store/1","requires":[]}""");
            var lockPath = Path.Combine(data, "engine.lock");
            if (what == "a FIFO")
                InfoPanel.Core.Tests.Fifo.Create(lockPath);
            else
                File.CreateSymbolicLink(lockPath, Path.Combine(elsewhere, "engine.lock"));
            var before = TreeHash(data);

            var (exitCode, said) = await RunUntilItStops(data, before);

            Assert.Equal(1, exitCode);
            Assert.Contains("engine.lock", said);
            Assert.Equal(before, TreeHash(data));
            Assert.Empty(Directory.EnumerateFileSystemEntries(elsewhere));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Every file and folder under <paramref name="root"/>: names, kinds, sizes, write times and bytes (the lock's
    /// bytes aren't read: a running engine holds it).
    /// </summary>
    private static string TreeHash(string root)
    {
        var text = new System.Text.StringBuilder();
        foreach (var entry in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var info = new FileInfo(entry);
            text.Append(Path.GetRelativePath(root, entry)).Append('|').Append(info.Attributes).Append('|').Append(info.LastWriteTimeUtc.Ticks).Append('|');
            if (File.Exists(entry))
            {
                text.Append(info.Length).Append('|');
                if (info.Name != "engine.lock")
                {
                    try
                    {
                        text.Append(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(entry))));
                    }
                    catch (IOException)
                    {
                        text.Append("(held open)");
                    }
                }
            }
            text.Append('\n');
        }
        return text.ToString();
    }

    [Fact]
    public async Task ThumbnailWhoseItemsFailToDraw_StillRenders()
    {
        using var engine = new EngineFactory();
        using var factory = engine.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddSingleton<ISensorReader>(new ThrowingReader())));
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = false });
        client.DefaultRequestHeaders.Authorization = new("Bearer", EngineFactory.Token);

        using var list = await client.GetAsync("/api/profiles", Ct);
        var id = System.Text.Json.JsonDocument.Parse(await list.Content.ReadAsStringAsync(Ct))
            .RootElement[0].GetProperty("id").GetString();

        // Every sensor read throws: the runtime reads no value for those items, and the frame still renders.
        using var response = await client.GetAsync($"/api/profiles/{id}/thumbnail", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);

        // The profile still reads.
        using var doc = await client.GetAsync($"/api/profiles/{id}", Ct);
        Assert.Equal(HttpStatusCode.OK, doc.StatusCode);
    }

    [Fact]
    public async Task ConfiguredKestrelEndpointsAndUrls_AreIgnored_OnlyLoopbackIsBound()
    {
        var port = EngineRuntime.FindFreePort();
        var lanPort = EngineRuntime.FindFreePort();
        var urlsPort = EngineRuntime.FindFreePort();
        using var factory = new KestrelEngineFactory(port, new Dictionary<string, string?>
        {
            ["Kestrel:Endpoints:Lan:Url"] = $"http://0.0.0.0:{lanPort}",
            ["urls"] = $"http://0.0.0.0:{urlsPort}",
            ["http_ports"] = urlsPort.ToString(),
        });
        factory.UseKestrel();
        factory.StartServer();

        var addresses = factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
        Assert.Equal([$"http://127.0.0.1:{port}"], addresses);

        Assert.True(await CanConnect(port));
        Assert.False(await CanConnect(lanPort));
        Assert.False(await CanConnect(urlsPort));
    }

    private static async Task<bool> CanConnect(int port)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(IPAddress.Loopback, port, Ct);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = Environment.TickCount64 + 5000;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
                Assert.Fail("Timed out waiting for the condition.");
            await Task.Delay(20, Ct);
        }
    }

    private sealed class ThrowingReader : ISensorReader
    {
        public bool TryRead(SensorKey key, out SensorReading reading) => throw new InvalidOperationException("sensor failure");
    }

    /// <summary>The engine on real Kestrel, with extra (hostile) configuration layered on.</summary>
    private sealed class KestrelEngineFactory(int port, IDictionary<string, string?> extra) : Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "infopanel-kestrel-tests-" + Guid.NewGuid().ToString("N"));

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Engine:DataDir", Path.Combine(_root, "data"));
            builder.UseSetting("Engine:Token", EngineFactory.Token);
            builder.UseSetting("Engine:Port", port.ToString());
            builder.UseSetting("Engine:NoBrowser", "true");
            builder.UseSetting("Engine:NoTray", "true");
            builder.UseSetting("Engine:NoPanels", "true");
            builder.UseSetting("Engine:MockSeed", "42");
            builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(extra));
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }
}
