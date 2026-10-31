using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using InfoPanel.Hosting;
using InfoPanel.Preview.Video;
using InfoPanel.Tests.Preview;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InfoPanel.Tests.Preview.Video;

/// <summary>
/// Where the engine finds ffmpeg (<c>--ffmpeg</c>, then <c>PATH</c>, then the folders package managers put it in,
/// which an app started from the desktop doesn't have on its <c>PATH</c>), the software encoder's place in the chain,
/// and its probe against stand-ins: one that exits 0 without encoding, one that hangs. Stand-ins are shell scripts, so
/// those tests are Unix only.
/// </summary>
[Collection(FfmpegCollection.Name)]
public class FfmpegDiscoveryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void SkipOnWindows() => Assert.SkipWhen(OperatingSystem.IsWindows(), "The stand-in ffmpeg is a shell script.");

    // ---- discovery ----

    [Fact]
    public void TheConfiguredPath_IsTheOnlyOneLookedAt()
    {
        SkipOnWindows();
        using var folder = new StandIns();
        var configured = folder.Write("ffmpeg", "exit 0", "configured");
        var onPath = folder.Write("ffmpeg", "exit 0", "path");
        var known = folder.Write("ffmpeg", "exit 0", "known");
        var path = Path.GetDirectoryName(onPath);
        string[] wellKnown = [Path.GetDirectoryName(known)!];

        Assert.Equal(configured, new FfmpegDiscovery(configured, path, wellKnown).Find());
        // Given and missing: nothing else is tried.
        Assert.Null(new FfmpegDiscovery(Path.Combine(folder.Path, "missing", "ffmpeg"), path, wellKnown).Find());
    }

    [Fact]
    public void ThenPath_ThenTheWellKnownFolders()
    {
        SkipOnWindows();
        using var folder = new StandIns();
        var first = folder.Write("ffmpeg", "exit 0", "first");
        var second = folder.Write("ffmpeg", "exit 0", "second");
        var known = folder.Write("ffmpeg", "exit 0", "known");
        var empty = Path.Combine(folder.Path, "empty");
        Directory.CreateDirectory(empty);
        var path = string.Join(Path.PathSeparator, empty, Path.GetDirectoryName(first), Path.GetDirectoryName(second));
        string[] wellKnown = [Path.GetDirectoryName(known)!];

        Assert.Equal(first, new FfmpegDiscovery(null, path, wellKnown).Find());
        Assert.Equal(known, new FfmpegDiscovery(null, empty, wellKnown).Find());
    }

    [Fact]
    public void TheWellKnownFolders_AreLookedInWithoutPath()
    {
        SkipOnWindows();
        // An app started from the Dock or Finder has a PATH without Homebrew's folder.
        using var folder = new StandIns();
        var known = folder.Write("ffmpeg", "exit 0", "known");
        string[] wellKnown = [Path.Combine(folder.Path, "absent"), Path.GetDirectoryName(known)!];
        var empty = Path.Combine(folder.Path, "empty");
        Directory.CreateDirectory(empty);
        Assert.Equal(known, new FfmpegDiscovery(null, null, wellKnown).Find());
        Assert.Equal(known, new FfmpegDiscovery(null, empty, wellKnown).Find());
    }

    [Fact]
    public void RelativePathEntries_AndFilesThatArentExecutable_AreSkipped()
    {
        SkipOnWindows();
        using var folder = new StandIns();
        var plain = Path.Combine(folder.Path, "plain");
        Directory.CreateDirectory(plain);
        File.WriteAllText(Path.Combine(plain, "ffmpeg"), "not executable");
        File.SetUnixFileMode(Path.Combine(plain, "ffmpeg"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var directory = Path.Combine(folder.Path, "ffmpeg-dir");
        Directory.CreateDirectory(Path.Combine(directory, "ffmpeg"));
        var known = folder.Write("ffmpeg", "exit 0", "known");
        // "." and "bin" would be looked up against whatever the current folder is.
        var path = string.Join(Path.PathSeparator, ".", "bin", plain, directory);
        Assert.Equal(known, new FfmpegDiscovery(null, path, [Path.GetDirectoryName(known)!]).Find());
        Assert.Null(new FfmpegDiscovery(null, path, []).Find());
    }

    [Fact]
    public void OnUnix_TheWellKnownFolders_AreHomebrewsAndUsrLocalBin()
    {
        if (OperatingSystem.IsWindows())
            Assert.Empty(FfmpegDiscovery.WellKnownDirectories);
        else
            Assert.Equal(["/opt/homebrew/bin", "/usr/local/bin"], FfmpegDiscovery.WellKnownDirectories);
        var discovery = FfmpegDiscovery.ForThisComputer("/somewhere/ffmpeg");
        Assert.Equal("/somewhere/ffmpeg", discovery.Configured);
        Assert.Equal(Environment.GetEnvironmentVariable("PATH"), discovery.SearchPath);
        Assert.Equal(FfmpegDiscovery.WellKnownDirectories, discovery.WellKnown);
    }

    [Fact]
    public void NoneFound_TheBackendIsUnavailable_AndSaysWhereItLooked()
    {
        using var folder = new StandIns();
        var logger = new RecordingLogger();
        var backend = new FfmpegH264(new FfmpegDiscovery(null, Path.Combine(folder.Path, "a"), [Path.Combine(folder.Path, "b")]), logger);
        Assert.Equal(H264Probe.Unavailable, backend.Probe());
        Assert.Null(backend.Executable);
        Assert.Contains(logger.Messages, m => m.Contains("No ffmpeg", StringComparison.Ordinal) && m.Contains(Path.Combine(folder.Path, "b"), StringComparison.Ordinal));
    }

    // ---- the chain ----

    [Fact]
    public void TheChain_PutsFfmpegAfterTheHardwareEncoder()
    {
        var discovery = new FfmpegDiscovery(null, null, []);
        string[] Names(string mode) => [.. H264Backends.ForThisComputer(mode, NullLogger.Instance, discovery).Chain.Select(b => b.Name)];
        if (OperatingSystem.IsMacOS())
        {
            Assert.Equal(["VideoToolbox", "ffmpeg"], Names("auto"));
            Assert.Equal(["VideoToolbox"], Names("hardware"));
        }
        else
        {
            Assert.Equal(["ffmpeg"], Names("auto"));
            Assert.Empty(Names("hardware"));
        }

        Assert.Equal(["ffmpeg"], Names("software"));
        Assert.Empty(Names("off"));
        Assert.Equal(H264Kind.Software, H264Backends.ForThisComputer("software", NullLogger.Instance, discovery).Chain[0].Kind);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheFfmpegFlag_TakesAFileThatExists(bool inline)
    {
        using var folder = new StandIns();
        var file = folder.Write("ffmpeg", "exit 0");
        string[] args = inline ? [$"--ffmpeg={file}"] : ["--ffmpeg", file];
        Assert.Equal(Path.GetFullPath(file), Load(args, out var errors)!.Ffmpeg);
        Assert.Empty(errors);
        Assert.Null(Load([], out _)!.Ffmpeg);

        Assert.Null(Load(["--ffmpeg", Path.Combine(folder.Path, "missing")], out errors));
        Assert.Contains(errors, e => e.Contains("--ffmpeg", StringComparison.Ordinal) && e.Contains("does not exist", StringComparison.Ordinal));
        Assert.Null(Load(["--ffmpeg", folder.Path], out errors));
        Assert.Contains(errors, e => e.Contains("--ffmpeg", StringComparison.Ordinal));
        Assert.Contains("--ffmpeg <path>", EngineCommandLine.Usage, StringComparison.Ordinal);
    }

    private static EngineOptions? Load(string[] args, out IReadOnlyList<string> errors)
    {
        var parsed = EngineCommandLine.Parse(args);
        Assert.Null(parsed.Error);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(parsed.Settings).Build();
        return EngineOptions.Load(configuration, out errors);
    }

    // ---- the probe against stand-ins ----

    [Fact]
    public void AStandInThatExitsWithoutEncoding_IsUnavailable()
    {
        SkipOnWindows();
        using var folder = new StandIns();
        // It even says it is ffmpeg, and exits 0 for every command line.
        var standIn = folder.Write("ffmpeg", "echo 'ffmpeg version 9.9.9 Copyright (c) the stand-in'\nexit 0");
        var backend = new FfmpegH264(FfmpegDiscovery.At(standIn), NullLogger.Instance);
        var watch = Stopwatch.StartNew();
        Assert.Equal(H264Probe.Unavailable, backend.Probe());
        Assert.True(watch.Elapsed < FfmpegH264.ProbeBound, "an encode that ends without an avcC is found at once");
    }

    [Fact]
    public void AStandInThatWritesNoAvcc_IsUnavailable()
    {
        SkipOnWindows();
        using var folder = new StandIns();
        // An FLV header and nothing else, then exit 0.
        var standIn = folder.Write("ffmpeg", "case \"$*\" in *-version*) echo 'ffmpeg version 9.9.9'; exit 0;; esac\nprintf 'FLV\\001\\001\\000\\000\\000\\011\\000\\000\\000\\000'\ncat > /dev/null\nexit 0");
        Assert.Equal(H264Probe.Unavailable, new FfmpegH264(FfmpegDiscovery.At(standIn), NullLogger.Instance).Probe());
    }

    [Fact]
    public async Task AStandInThatHangs_IsKilledAtFiveSeconds()
    {
        SkipOnWindows();
        using var folder = new StandIns();
        var pidFile = Path.Combine(folder.Path, "pid");
        var standIn = folder.Write("ffmpeg", $"echo $$ >> '{pidFile}'\nexec sleep 60");
        var backend = new FfmpegH264(FfmpegDiscovery.At(standIn), NullLogger.Instance);
        var watch = Stopwatch.StartNew();
        Assert.Equal(H264Probe.Unavailable, backend.Probe());
        Assert.InRange(watch.Elapsed, FfmpegH264.ProbeBound - TimeSpan.FromMilliseconds(100), FfmpegH264.ProbeBound + TimeSpan.FromSeconds(2));
        Assert.Equal(TimeSpan.FromSeconds(5), FfmpegH264.ProbeBound);

        var pids = (await File.ReadAllLinesAsync(pidFile, Ct)).Select(l => int.Parse(l, System.Globalization.CultureInfo.InvariantCulture)).ToList();
        Assert.NotEmpty(pids);
        var gone = Stopwatch.StartNew();
        while (pids.Any(FfmpegH264Tests.IsRunning) && gone.Elapsed < TimeSpan.FromSeconds(2))
            await Task.Delay(20, Ct);
        Assert.All(pids, pid => Assert.False(FfmpegH264Tests.IsRunning(pid), $"the stand-in {pid} was killed"));
    }

    [Fact]
    public void AVersionAnsweredWithAFlood_IsReadInBoundedMemory()
    {
        SkipOnWindows();
        using var folder = new StandIns();
        // -version answers with 128 MiB on each output, in one line each, then exits 0; the encode exits at once.
        var flood = FfmpegH264Tests.FloodCommand(folder.Path);
        var standIn = folder.Write("ffmpeg", $"case \"$*\" in *-version*) printf 'ffmpeg version 9.9.9 '; {flood}; {flood} >&2; exit 0;; esac\nexit 0");
        var backend = new FfmpegH264(FfmpegDiscovery.At(standIn), NullLogger.Instance);
        var before = GC.GetTotalAllocatedBytes(precise: true);
        var watch = Stopwatch.StartNew();
        Assert.Equal(H264Probe.Unavailable, backend.Probe());
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        Assert.True(allocated < FfmpegH264Tests.FloodAllocationBound, $"reading two {FfmpegH264Tests.Flood}-byte lines allocated {allocated} bytes");
        Assert.True(watch.Elapsed < FfmpegH264.ProbeBound, "both outputs were read to their end, not cut off at the bound");
    }

    [Fact]
    public async Task StoppingTheEngine_DuringTheProbe_LeavesNoFfmpegBehind()
    {
        SkipOnWindows();
        using var folder = new StandIns();
        var pidFile = Path.Combine(folder.Path, "pid");
        var standIn = folder.Write("ffmpeg", $"echo $$ >> '{pidFile}'\nexec sleep 60");
        using var engine = new EngineFactory();
        var withStandIn = engine.WithWebHostBuilder(b => b.ConfigureTestServices(services => services.AddSingleton(_ =>
            new H264Backends([new FfmpegH264(FfmpegDiscovery.At(standIn), NullLogger.Instance)], NullLogger.Instance))));
        int pid;
        try
        {
            // A video subscription reaches the encoder, whose probe starts the stand-in; the subscription ends at once.
            await using (var client = await PreviewClient.ConnectAsync(withStandIn, Ct))
            {
                await client.ReceiveTextAsync("hello", Ct);
                using var api = withStandIn.CreateClient();
                api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", EngineFactory.Token);
                var profiles = await api.GetFromJsonAsync<JsonElement>("/api/profiles", Ct);
                var id = profiles.EnumerateArray().First().GetProperty("id").GetString()!;
                await client.SendAsync(new { type = "subscribe", profileId = id, scale = 0.5, encoding = "h264" }, Ct);
                var started = Stopwatch.StartNew();
                while (!(File.Exists(pidFile) && (await File.ReadAllTextAsync(pidFile, Ct)).Trim().Length > 0) && started.Elapsed < TimeSpan.FromSeconds(10))
                    await Task.Delay(10, Ct);
                Assert.True(File.Exists(pidFile), "the probe started ffmpeg");
            }

            pid = int.Parse((await File.ReadAllLinesAsync(pidFile, Ct))[0], System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(FfmpegH264Tests.IsRunning(pid), "the probe's ffmpeg runs");
        }
        catch
        {
            withStandIn.Dispose();
            throw;
        }

        // The engine stops well within the probe's 5 s, and leaves no ffmpeg behind.
        var watch = Stopwatch.StartNew();
        withStandIn.Dispose();
        Assert.True(watch.Elapsed < FfmpegH264.ProbeBound, $"the engine stopped in {watch.Elapsed}");
        Assert.False(FfmpegH264Tests.IsRunning(pid), $"ffmpeg {pid} still runs after the engine stopped");
    }

    [Fact]
    public async Task StoppingTheEngine_WithAnEstablishedSoftwareStream_LeavesNoFfmpegBehind()
    {
        TestFfmpeg.SkipWithout();
        var backend = new FfmpegH264(FfmpegDiscovery.At(TestFfmpeg.Path), NullLogger.Instance);
        using var engine = new EngineFactory();
        var withFfmpeg = engine.WithWebHostBuilder(b => b.ConfigureTestServices(services => services.AddSingleton(_ =>
            new H264Backends([backend], NullLogger.Instance))));
        PreviewClient? client = null;
        try
        {
            // A video subscription whose frames come from ffmpeg: its session is established.
            client = await PreviewClient.ConnectAsync(withFfmpeg, Ct);
            await client.ReceiveTextAsync("hello", Ct);
            using (var api = withFfmpeg.CreateClient())
            {
                api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", EngineFactory.Token);
                var profiles = await api.GetFromJsonAsync<JsonElement>("/api/profiles", Ct);
                var id = profiles.EnumerateArray().First().GetProperty("id").GetString()!;
                await client.SendAsync(new { type = "subscribe", profileId = id, scale = 0.5, encoding = "h264" }, Ct);
            }

            var (first, _) = await client.ReceiveFrameAsync(Ct, h => h.Encoding == "h264", TimeSpan.FromSeconds(15));
            Assert.Equal(("software", true), (first.Encoder, first.Keyframe));
            var pid = Assert.Single(backend.ProcessIds);
            Assert.True(FfmpegH264Tests.IsRunning(pid), "the stream's ffmpeg runs");

            // The engine stops with the client still connected, never answering the close: the stop itself ends the
            // stream and waits until its ffmpeg is killed and collected.
            withFfmpeg.Dispose();
            Assert.False(FfmpegH264Tests.IsRunning(pid), $"ffmpeg {pid} still runs after the engine stopped");
        }
        finally
        {
            withFfmpeg.Dispose();
            try
            {
                if (client != null)
                    await client.DisposeAsync();
            }
            catch (IOException)
            {
                // The engine is gone: its end of the socket may have been closed under the client's close.
            }
        }
    }

    [Fact]
    public async Task TheProbe_RunsOffTheCallersThread_AndTheResultIsCached()
    {
        SkipOnWindows();
        using var folder = new StandIns();
        var count = Path.Combine(folder.Path, "count");
        var standIn = folder.Write("ffmpeg", $"echo x >> '{count}'\nexec sleep 60");
        var backend = new FfmpegH264(FfmpegDiscovery.At(standIn), NullLogger.Instance);
        var backends = new H264Backends([backend], NullLogger.Instance);

        // The stream's loop asks; the answer comes from a background task, never the asking thread.
        var thread = Environment.CurrentManagedThreadId;
        var watch = Stopwatch.StartNew();
        var probe = backends.ProbeAsync(backend);
        Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds(500), "asking returns at once");
        Assert.False(probe.IsCompleted);
        Assert.Equal(H264Probe.Unavailable, await probe.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        Assert.NotEqual(thread, backend.ProbeThread);
        Assert.Same(probe, backends.ProbeAsync(backend));
        Assert.True(backends.NoneLeft);
        Assert.Single(await File.ReadAllLinesAsync(count, Ct));
    }
}
