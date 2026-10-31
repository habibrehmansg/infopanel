using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using InfoPanel.Hosting;
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
        Assert.Equal([new EditorLink($"{EngineFactory.CrossOrigin}/#engine=http://127.0.0.1:{EngineFactory.Port}&token={EngineFactory.Token}", WebClient: true)],
            browser.Links);
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
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
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
    /// doesn't get a fresh answer timeout of its own. The launch ends within the budget, with the folder still held.
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
            var handOff = TimeSpan.FromSeconds(2);
            var waited = Stopwatch.StartNew();
            var launch = SecondLaunch.HandOffAsync(lockFile, Mcp.TestRunFolder.Path, data, openEditor: true, handOff);
            await Task.Delay(handOff - TimeSpan.FromMilliseconds(500), Ct);
            channel = FakeChannel.Start(LocalChannel.EndpointFor(Mcp.TestRunFolder.Path, data), hello: true);

            var outcome = await launch.WaitAsync(TimeSpan.FromSeconds(30), Ct);
            waited.Stop();

            Assert.True(channel.Met.Task.IsCompleted, "The launch never asked the channel that appeared.");
            Assert.Null(outcome.Lock);
            Assert.False(outcome.Opened);
            Assert.True(outcome.Held);
            Assert.True(waited.Elapsed < handOff + TimeSpan.FromSeconds(1.5), $"The hand-off took {waited.Elapsed.TotalMilliseconds:F0} ms of a {handOff.TotalMilliseconds:F0} ms budget.");
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
