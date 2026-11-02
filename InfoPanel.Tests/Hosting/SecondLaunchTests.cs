using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using InfoPanel.Hosting;
using InfoPanel.Tests.Desktop;
using InfoPanel.Tests.Plugins;
using InfoPanel.Tests.Shell;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace InfoPanel.Tests.Hosting;

/// <summary>
/// A second launch: the user starts InfoPanel again on a data folder an engine holds. The first engine runs in process
/// (holding the lock and the folder's local channel, its browser launcher recording), the second is the built engine as
/// a child process on the same folder: it asks the first to open the editor, says so, and exits 3; with
/// <c>--no-browser</c> it asks nothing. The first engine serves no web client, so the editor it opens is its one cross
/// origin's pairing link.
/// </summary>
public sealed class SecondLaunchTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The pause between a hand-off's looks for the channel (<see cref="SecondLaunch"/>'s).</summary>
    private static readonly TimeSpan Look = TimeSpan.FromMilliseconds(250);

    private static WebApplicationFactory<Program> First(EngineFactory engine, RecordingBrowserLauncher browser) =>
        engine.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Engine:WebRoot", "");
            builder.ConfigureTestServices(services => services.AddSingleton<BrowserLauncher>(browser));
        });

    [Fact]
    public async Task ASecondLaunch_OpensTheEditorInTheRunningEngine_AndExits3()
    {
        using var engine = new EngineFactory();
        var browser = new RecordingBrowserLauncher();
        using var first = First(engine, browser);
        await WaitForChannel(first);

        var (exit, said) = await Launch(engine.DataDir);

        Assert.Equal(3, exit);
        Assert.Contains("InfoPanel is already running; opened the editor", said);
        Assert.DoesNotContain(EngineFactory.Token, said);
        var link = Assert.Single(browser.Links);
        Assert.True(link.WebClient);
        Assert.Matches(
            $"^{System.Text.RegularExpressions.Regex.Escape($"{EngineFactory.CrossOrigin}/#engine=http://127.0.0.1:{EngineFactory.Port}&code=")}[A-Za-z0-9_-]{{22}}$",
            link.Url);
        Assert.DoesNotContain(EngineFactory.Token, link.Url);
    }

    [Fact]
    public async Task ASecondLaunch_WithNoBrowser_AsksNothing_AndExits3()
    {
        using var engine = new EngineFactory();
        var browser = new RecordingBrowserLauncher();
        using var first = First(engine, browser);
        await WaitForChannel(first);

        var (exit, said) = await Launch(engine.DataDir, "--no-browser");

        Assert.Equal(3, exit);
        Assert.DoesNotContain("opened the editor", said);
        Assert.Empty(browser.Links);
    }

    [Fact]
    public async Task TheChannel_OpensTheEditorOnlyForALaunch_NotForAnAiAppsFront()
    {
        using var engine = new EngineFactory();
        var browser = new RecordingBrowserLauncher();
        using var first = First(engine, browser);
        var endpoint = await WaitForChannel(first);

        await using (var front = await LocalChannelClient.TryConnectAsync(endpoint, TimeSpan.FromSeconds(5), Ct))
        {
            var answer = await front!.SendAsync(new JsonObject { ["op"] = "open-editor" }, Ct);
            Assert.NotNull(answer["error"]);
        }
        await using (var launch = await LocalChannelClient.TryConnectAsync(endpoint, TimeSpan.FromSeconds(5), Ct, client: LocalChannel.LaunchClient))
        {
            var list = await launch!.SendAsync(new JsonObject { ["op"] = "list" }, Ct);
            Assert.NotNull(list["error"]);
            Assert.Empty(browser.Links);
            var answer = await launch.SendAsync(new JsonObject { ["op"] = "open-editor" }, Ct);
            Assert.True(answer["opened"]!.GetValue<bool>());
        }

        Assert.Single(browser.Links);
    }

    /// <summary>
    /// A launch while the engine holding the folder stops: its channel is closed but it hasn't let go of the folder's lock
    /// yet (it keeps it through its clean-up). Nothing answers, so the launch waits for the folder, takes it once it's
    /// free and starts, rather than exiting 3 with the folder free a moment later. The test holds the lock itself, with no
    /// channel, and lets go of it a second after the launch.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ALaunchWhileTheEngineStops_WaitsForTheFolder_AndStarts(bool noBrowser)
    {
        var root = Path.Combine(Path.GetTempPath(), "infopanel-second-launch-" + Guid.NewGuid().ToString("N"));
        var data = Path.Combine(root, "data");
        Directory.CreateDirectory(data);
        Process? process = null;
        var said = new System.Text.StringBuilder();
        try
        {
            var stopping = InstanceLock.TryAcquire(Path.Combine(data, "engine.lock"), out var error);
            Assert.True(stopping != null, error);
            process = Start(data, said, [.. noBrowser ? ["--no-browser"] : Array.Empty<string>(), "--offline", "--no-plugins"]);
            await Task.Delay(1000, Ct);
            stopping!.Dispose();

            var endpoint = LocalChannel.EndpointFor(Mcp.TestRunFolder.Path, data);
            // A sanity limit, not the behaviour under test (the launch either starts or exits, and either ends the wait):
            // the launch is the built engine as a child process, and under parallel full runs this Mac's program launches
            // and file opens can stall for tens of seconds (its endpoint protection authorises each one); 30 s didn't
            // always cover its start. The wait still ends as soon as its channel answers.
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(120);
            int? answering = null;
            while (answering == null)
            {
                if (process.HasExited)
                {
                    lock (said)
                        Assert.Fail($"The launch exited ({process.ExitCode}) instead of starting: {said}");
                }
                Assert.True(DateTime.UtcNow < deadline, $"The launch never opened its channel: {said}");
                await using (var client = await LocalChannelClient.TryConnectAsync(endpoint, TimeSpan.FromSeconds(2), Ct))
                    answering = client?.Pid;
                if (answering == null)
                    await Task.Delay(100, Ct);
            }

            Assert.Equal(process.Id, answering);
        }
        finally
        {
            if (process is { HasExited: false })
                process.Kill(entireProcessTree: true);
            process?.WaitForExit(10_000);
            process?.Dispose();
            try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// A launch that meets the engine stopping mid-hello: the engine accepts the launch's connection, then closes it
    /// before its hello, and lets go of the folder a moment later. A closed connection is the engine going, not a refusal,
    /// so the launch looks again and takes the folder rather than exiting 3.
    /// </summary>
    [Fact]
    public async Task AChannelThatClosesBeforeItsHello_IsAnEngineStopping_AndTheLaunchTakesTheFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "infopanel-second-launch-" + Guid.NewGuid().ToString("N"));
        var data = Path.Combine(root, "data");
        Directory.CreateDirectory(data);
        var lockFile = Path.Combine(data, "engine.lock");
        var stopping = InstanceLock.TryAcquire(lockFile, out var error);
        Assert.True(stopping != null, error);
        var channel = FakeChannel.Start(LocalChannel.EndpointFor(Mcp.TestRunFolder.Path, data), hello: false);
        try
        {
            var launch = SecondLaunch.HandOffAsync(lockFile, Mcp.TestRunFolder.Path, data, openEditor: true, TimeSpan.FromSeconds(5));
            await channel.Met.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
            await Task.Delay(500, Ct);
            await channel.DisposeAsync();
            stopping!.Dispose();

            var outcome = await launch.WaitAsync(TimeSpan.FromSeconds(30), Ct);
            using var taken = outcome.Lock;
            Assert.True(taken != null, $"The launch didn't take the folder: {outcome}");
        }
        finally
        {
            await channel.DisposeAsync();
            stopping?.Dispose();
            try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// The hand-off is one budget: a channel that appears near its end, says hello and never answers the launch's request
    /// doesn't get a fresh answer timeout of its own. The launch ends within the budget, with the folder still held. The
    /// hand-off runs on the test's clock, moved only once the launch waits on it, so a late wake of this test can't use up
    /// the budget before the channel appears.
    /// </summary>
    [Fact]
    public async Task AChannelThatAppearsNearTheEnd_AndNeverAnswers_DoesntStretchTheHandOff()
    {
        var root = Path.Combine(Path.GetTempPath(), "infopanel-second-launch-" + Guid.NewGuid().ToString("N"));
        var data = Path.Combine(root, "data");
        Directory.CreateDirectory(data);
        var lockFile = Path.Combine(data, "engine.lock");
        var holder = InstanceLock.TryAcquire(lockFile, out var error);
        Assert.True(holder != null, error);
        FakeChannel? channel = null;
        try
        {
            var time = new ManualTime(DateTimeOffset.UnixEpoch);
            var handOff = TimeSpan.FromSeconds(2);
            var appears = handOff - TimeSpan.FromMilliseconds(500);
            var launch = SecondLaunch.HandOffAsync(lockFile, Mcp.TestRunFolder.Path, data, openEditor: true, handOff, time);

            // The launch looks for the channel, then pauses a look's length: the clock moves a pause at a time, once the
            // launch waits on it, and the channel appears for the look at `appears`.
            var now = TimeSpan.Zero;
            while (now < appears)
            {
                now += Look;
                await Eventually.Until(() => time.HasTimerDueAt(now), $"the launch pauses until {now.TotalMilliseconds} ms");
                if (now == appears)
                    channel = FakeChannel.Start(LocalChannel.EndpointFor(Mcp.TestRunFolder.Path, data), hello: true);
                time.Advance(Look);
            }

            // A look's connection has a real-time limit of its own: one that ran out pauses again, and the clock moves on to
            // the next look while one is left before the budget's end.
            var met = channel!.Met.Task;
            while (now + Look < handOff)
            {
                var next = now + Look;
                await Eventually.Until(() => met.IsCompleted || time.HasTimerDueAt(next), "the launch asks the channel, or pauses");
                if (met.IsCompleted)
                    break;
                now = next;
                time.Advance(Look);
            }
            await Eventually.Until(() => met.IsCompleted, "the launch asks the channel that appeared");

            // The answer's deadline is set before the launch connects, so by the time the channel is asked it is armed: on
            // the launch's clock, at the budget's end. A fresh timeout of its own, or one on another clock (a real one,
            // which the generous wait below would let run out), sets no timer there.
            Assert.True(time.TimersDueAt(handOff) == 1, $"the answer's deadline isn't set on the launch's clock at the end of its {handOff.TotalMilliseconds:F0} ms budget");

            // The channel never answers. At the budget's end the launch's wait for the answer is over: the clock moves no
            // further, so a fresh answer timeout would leave the launch waiting.
            time.Advance(handOff - now);
            await Eventually.Until(() => launch.IsCompleted, $"the launch ends at the end of its {handOff.TotalMilliseconds:F0} ms budget");
            var outcome = await launch;

            Assert.Null(outcome.Lock);
            Assert.False(outcome.Opened);
            Assert.True(outcome.Held);
            Assert.Equal(handOff, time.GetUtcNow() - DateTimeOffset.UnixEpoch);
        }
        finally
        {
            if (channel != null)
                await channel.DisposeAsync();
            holder?.Dispose();
            try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// A data folder's channel as an engine in a hand-off can show it: one that reads a launch's hello and closes the
    /// connection without its own (an engine stopping), or one that says hello, reads the launch's request and never
    /// answers it (<c>hello: true</c>). <see cref="Met"/> completes once a launch got that far.
    /// </summary>
    private sealed class FakeChannel : IAsyncDisposable
    {
        private readonly LocalChannelEndpoint _endpoint;
        private readonly bool _hello;
        private readonly Socket? _listener;
        private readonly CancellationTokenSource _stop = new();
        private Task _accepting = Task.CompletedTask;
        private int _disposed;

        private FakeChannel(LocalChannelEndpoint endpoint, bool hello, Socket? listener)
        {
            _endpoint = endpoint;
            _hello = hello;
            _listener = listener;
        }

        public TaskCompletionSource Met { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public static FakeChannel Start(LocalChannelEndpoint endpoint, bool hello)
        {
            LocalChannel.CreateRunDirectory(endpoint.RunDirectory);
            Socket? listener = null;
            if (endpoint.SocketPath is { } path)
            {
                File.Delete(path);
                listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                listener.Bind(new UnixDomainSocketEndPoint(path));
                listener.Listen(16);
            }

            var channel = new FakeChannel(endpoint, hello, listener);
            channel._accepting = channel.AcceptAsync();
            return channel;
        }

        private async Task AcceptAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                Stream stream;
                try
                {
                    if (_listener != null)
                    {
                        stream = new NetworkStream(await _listener.AcceptAsync(_stop.Token), ownsSocket: true);
                    }
                    else
                    {
                        var pipe = new NamedPipeServerStream(_endpoint.PipeName!, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                        try
                        {
                            await pipe.WaitForConnectionAsync(_stop.Token);
                        }
                        catch
                        {
                            await pipe.DisposeAsync();
                            throw;
                        }
                        stream = pipe;
                    }
                }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException or IOException)
                {
                    return;
                }

                _ = ServeAsync(stream);
            }
        }

        private async Task ServeAsync(Stream stream)
        {
            try
            {
                await ReadLineAsync(stream);
                if (_hello)
                {
                    await stream.WriteAsync("{\"engineVersion\":\"test\"}\n"u8.ToArray(), _stop.Token);
                    await ReadLineAsync(stream);
                    Met.TrySetResult();
                    await Task.Delay(Timeout.Infinite, _stop.Token);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException or IOException)
            {
            }
            finally
            {
                await stream.DisposeAsync();
                if (!_hello)
                    Met.TrySetResult();
            }
        }

        /// <summary>Reads up to and including the next line's end, byte by byte, so nothing the launch sent is left unread.</summary>
        private async Task ReadLineAsync(Stream stream)
        {
            var one = new byte[1];
            do
            {
                if (await stream.ReadAsync(one, _stop.Token) == 0)
                    throw new EndOfStreamException();
            }
            while (one[0] != (byte)'\n');
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            await _stop.CancelAsync();
            _listener?.Dispose();
            await _accepting;
            if (_endpoint.SocketPath is { } path)
                File.Delete(path);
            _stop.Dispose();
        }
    }

    /// <summary>The built engine on <paramref name="data"/> as a launch, running; what it says goes into <paramref name="said"/>.</summary>
    private static Process Start(string data, System.Text.StringBuilder said, params string[] extra)
    {
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "InfoPanel.exe" : "InfoPanel"))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in new[] { "--data-dir", data, "--v1-dir", data + "-v1", "--port", EngineRuntime.FindFreePort().ToString(), "--no-windows", "--no-tray", "--mock" }.Concat(extra))
            start.ArgumentList.Add(arg);
        var process = Process.Start(start)!;
        process.OutputDataReceived += (_, e) => { lock (said) said.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { lock (said) said.AppendLine(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private static async Task<LocalChannelEndpoint> WaitForChannel(WebApplicationFactory<Program> first)
    {
        var channel = first.Services.GetRequiredService<LocalChannelService>();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (channel.Server == null)
        {
            Assert.True(DateTime.UtcNow < deadline, "The first engine's channel never opened.");
            await Task.Delay(20, Ct);
        }

        return channel.Server.Endpoint;
    }

    /// <summary>The built engine on <paramref name="data"/> as a second launch: its exit code and everything it said.</summary>
    private static async Task<(int Exit, string Said)> Launch(string data, params string[] extra)
    {
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "InfoPanel.exe" : "InfoPanel"))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in new[] { "--data-dir", data, "--v1-dir", data + "-v1", "--port", EngineRuntime.FindFreePort().ToString(), "--no-windows", "--no-tray", "--mock" }.Concat(extra))
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(Ct);
        var errors = process.StandardError.ReadToEndAsync(Ct);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        limit.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(limit.Token);
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }

        return (process.ExitCode, await output + await errors);
    }
}
