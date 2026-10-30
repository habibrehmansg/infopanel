using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using InfoPanel.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InfoPanel.Tests.Mcp;

/// <summary>
/// The local channel (<see cref="LocalChannel"/>): where it is, who may reach it, its hello and limits, and that one
/// connection's trouble leaves the others alone. Over real Unix sockets in a short temp run folder.
/// </summary>
public sealed class LocalChannelTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ipc-" + Guid.NewGuid().ToString("N")[..8]);

    private string Run => Path.Combine(_root, "run");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string Data(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void TheEndpoint_DiffersPerDataFolder_IsTheSameThroughALink_AndFitsASocketPath()
    {
        var a = LocalChannel.EndpointFor(Run, Data("a"));
        var b = LocalChannel.EndpointFor(Run, Data("b"));
        Assert.NotEqual(a.Name, b.Name);
        Assert.Equal(16, a.Hash.Length);
        Assert.Matches("^[0-9a-f]{16}$", a.Hash);

        if (!OperatingSystem.IsWindows())
        {
            var link = Path.Combine(_root, "link-to-a");
            Directory.CreateSymbolicLink(link, Path.Combine(_root, "a"));
            Assert.Equal(a.Name, LocalChannel.EndpointFor(Run, link).Name);
            Assert.Equal(Path.Combine(Path.GetFullPath(Run), a.Hash + ".sock"), a.SocketPath);

            // The default run folder fits with room for a long user name: ~/Library/Application Support/InfoPanel/run/{16}.sock.
            var real = LocalChannel.EndpointFor(LocalChannel.DefaultRunDirectory(), Data("c"));
            Assert.True(Encoding.UTF8.GetByteCount(real.SocketPath!) <= LocalChannel.MaxSocketPathBytes, real.SocketPath);
            Assert.StartsWith(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InfoPanel", "run"), real.SocketPath);

            // A run folder that makes the path longer than a socket allows is refused, not cut.
            var deep = Path.Combine(_root, new string('d', 120));
            var refused = Assert.Throws<LocalChannelException>(() => LocalChannel.EndpointFor(deep, Data("a")));
            Assert.Contains("103", refused.Message);
        }
    }

    /// <summary>
    /// Codex p1 #6: on a volume that folds case (macOS's default), every spelling of a data folder names one channel, the
    /// filesystem's own; on one that keeps case apart, two spellings are two folders and two channels.
    /// </summary>
    [Fact]
    public void TheEndpoint_IsTheFilesystemsOwnSpelling_SoCaseAliasesShareIt_WhereTheVolumeFoldsCase()
    {
        var data = Data("Case-Data");
        var alias = Path.Combine(_root, "case-DATA");
        var shouted = Path.Combine(_root.ToUpperInvariant(), "CASE-data");
        var folds = Directory.Exists(alias);
        var expected = LocalChannel.EndpointFor(Run, data).Hash;
        string[] spellings = [alias, shouted, alias + Path.DirectorySeparatorChar];
        foreach (var spelling in spellings)
        {
            if (folds)
                Assert.Equal(expected, LocalChannel.EndpointFor(Run, spelling).Hash);
            else if (!OperatingSystem.IsWindows())
                Assert.NotEqual(expected, LocalChannel.EndpointFor(Run, spelling).Hash);
        }

        // A folder not made yet keeps its own spelling below the part that exists, which is spelled as stored: the
        // front (before the engine makes it) and the engine (after) name one channel.
        var later = LocalChannel.EndpointFor(Run, Path.Combine(data, "Later")).Hash;
        if (folds)
            Assert.Equal(later, LocalChannel.EndpointFor(Run, Path.Combine(alias, "Later")).Hash);
        Directory.CreateDirectory(Path.Combine(data, "Later"));
        Assert.Equal(later, LocalChannel.EndpointFor(Run, Path.Combine(data, "Later")).Hash);
    }

    [Fact]
    public async Task TheRunFolder_IsOwnerOnly_AndTheSocketToo_EvenWhenTheFolderWasThereWider()
    {
        if (OperatingSystem.IsWindows())
            return;

        await using (var server = Start(Data("a")))
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Run));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(server.Endpoint.SocketPath!) & ~UnixFileMode.None);
        }

        File.SetUnixFileMode(Run, (UnixFileMode)0b111_101_101); // 0755, as another tool might leave it
        await using (var server = Start(Data("a")))
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Run));
    }

    [Fact]
    public async Task AStaleSocket_IsReplacedByTheLockHolder_AndNeverRemovedByACompanion()
    {
        if (OperatingSystem.IsWindows())
            return;

        var data = Data("a");
        var endpoint = LocalChannel.EndpointFor(Run, data);
        Directory.CreateDirectory(Run);
        using (var crashed = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified))
        {
            // .NET removes the file of a socket it closes, so it is bound elsewhere and moved into place: closed, it is
            // left there as a crash leaves it.
            var elsewhere = Path.Combine(Run, "crashed.sock");
            crashed.Bind(new UnixDomainSocketEndPoint(elsewhere));
            crashed.Listen(1);
            File.Move(elsewhere, endpoint.SocketPath!);
        }

        Assert.True(File.Exists(endpoint.SocketPath));
        // A companion finds nothing there and leaves the file alone.
        Assert.Null(await LocalChannelClient.TryConnectAsync(endpoint, TimeSpan.FromSeconds(2), Ct));
        Assert.True(File.Exists(endpoint.SocketPath));

        // The engine holding the folder's lock replaces it and answers there.
        await using var server = Start(data);
        await using var client = await Connect(server);
        Assert.Equal(Environment.ProcessId, client.Pid);

        // A plain file there (left by anything) is replaced too.
        await server.DisposeAsync();
        Assert.False(File.Exists(endpoint.SocketPath));
        File.WriteAllText(endpoint.SocketPath!, "not a socket");
        await using var again = Start(data);
        await using var second = await Connect(again);
        Assert.Equal(1, (await second.SendAsync(new JsonObject { ["op"] = "list" }, Ct))["tools"]!.AsArray().Count);
    }

    [Fact]
    public async Task TheHello_NamesTheEngine_AndAnotherChannelVersionOrClientIsRefused()
    {
        if (OperatingSystem.IsWindows())
            return;

        await using var server = Start(Data("a"));
        await using (var client = await Connect(server))
        {
            Assert.Equal(1, client.Hello["channel"]!.GetValue<int>());
            Assert.False(string.IsNullOrEmpty(client.Hello["engineVersion"]!.GetValue<string>()));
            Assert.False(string.IsNullOrEmpty(client.Hello["engineBuild"]!.GetValue<string>()));
        }

        var (version, versionClosed) = await RawHello(server, """{"channel":2,"client":"mcp"}""");
        Assert.Equal(1, version["channel"]!.GetValue<int>());
        Assert.Contains("channel 2", version["refused"]!.GetValue<string>());
        Assert.True(versionClosed);

        var (client2, clientClosed) = await RawHello(server, """{"channel":1,"client":"someone"}""");
        Assert.Equal("unknown client", client2["refused"]!.GetValue<string>());
        Assert.True(clientClosed);
    }

    [Fact]
    public async Task AnswersComeInOrderOnAConnection_ListAndCallReachTheHandler()
    {
        if (OperatingSystem.IsWindows())
            return;

        var handler = new FakeHandler();
        await using var server = Start(Data("a"), handler);
        using var socket = await RawConnect(server);
        await using var stream = new NetworkStream(socket);
        await stream.WriteAsync(Encoding.UTF8.GetBytes("""{"channel":1,"client":"mcp"}""" + "\n"), Ct);
        // Two requests at once: the first one's handler takes longer, but its answer still comes first.
        await stream.WriteAsync(Encoding.UTF8.GetBytes(
            """{"id":"a","op":"call","tool":"slow","arguments":{"n":1}}""" + "\n" +
            """{"id":"b","op":"list"}""" + "\n" +
            """{"id":"c","op":"nonsense"}""" + "\n"), Ct);
        var reader = new LineReader(stream, 1 << 20);
        Assert.NotNull(await reader.ReadLineAsync(Ct)); // hello
        var first = JsonNode.Parse((await reader.ReadLineAsync(Ct))!)!;
        var second = JsonNode.Parse((await reader.ReadLineAsync(Ct))!)!;
        var third = JsonNode.Parse((await reader.ReadLineAsync(Ct))!)!;
        Assert.Equal("a", first["id"]!.GetValue<string>());
        Assert.Equal("slow:{\"n\":1}", first["content"]![0]!["text"]!.GetValue<string>());
        Assert.Equal("b", second["id"]!.GetValue<string>());
        Assert.Equal("fake", second["tools"]![0]!["name"]!.GetValue<string>());
        Assert.Equal("c", third["id"]!.GetValue<string>());
        Assert.Contains("unknown op", third["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task ALineOverTheCap_ClosesThatConnection_AndTheOthersGoOn()
    {
        if (OperatingSystem.IsWindows())
            return;

        await using var server = Start(Data("a"), options: new LocalChannelServerOptions { MaxLineBytes = 4096 });
        await using var other = await Connect(server);

        using var socket = await RawConnect(server);
        await using var stream = new NetworkStream(socket);
        await stream.WriteAsync(Encoding.UTF8.GetBytes("""{"channel":1,"client":"mcp"}""" + "\n"), Ct);
        var reader = new LineReader(stream, 1 << 20);
        Assert.NotNull(await reader.ReadLineAsync(Ct));
        var big = """{"id":1,"op":"call","tool":"x","arguments":{"s":""" + "\"" + new string('x', 5000) + "\"}}\n";
        try
        {
            await stream.WriteAsync(Encoding.UTF8.GetBytes(big), Ct);
        }
        catch (IOException)
        {
            // The engine may close before it reads everything.
        }
        Assert.Null(await ReadOrEnd(reader));

        // A line within the cap is answered on the other connection.
        var answer = await other.SendAsync(new JsonObject { ["op"] = "call", ["tool"] = "x", ["arguments"] = new JsonObject { ["s"] = new string('y', 3000) } }, Ct);
        Assert.False(answer["isError"]!.GetValue<bool>());
    }

    /// <summary>
    /// Codex p1 #8: an answer longer than a line may be is never sent (the companion would take the engine for gone): it is
    /// an error result that says so, and the connection goes on.
    /// </summary>
    [Fact]
    public async Task AnAnswerOverTheCap_IsAnErrorResultInstead_AndTheConnectionGoesOn()
    {
        if (OperatingSystem.IsWindows())
            return;

        await using var server = Start(Data("a"), options: new LocalChannelServerOptions { MaxLineBytes = 4096 });
        await using var client = await Connect(server);
        var big = await client.SendAsync(new JsonObject { ["op"] = "call", ["tool"] = "big" }, Ct);
        Assert.True(big["isError"]!.GetValue<bool>());
        Assert.Contains("too long to send", big["content"]![0]!["text"]!.GetValue<string>());

        var next = await client.SendAsync(new JsonObject { ["op"] = "call", ["tool"] = "x" }, Ct);
        Assert.Equal("x:{}", next["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task EightConnectionsAtMost_ANinthIsRefused_AndAClosedOneFreesItsPlace()
    {
        if (OperatingSystem.IsWindows())
            return;

        await using var server = Start(Data("a"));
        var clients = new List<LocalChannelClient>();
        try
        {
            for (var i = 0; i < LocalChannel.MaxConnections; i++)
                clients.Add(await Connect(server));

            var refused = await Assert.ThrowsAsync<LocalChannelException>(() => LocalChannelClient.TryConnectAsync(server.Endpoint, TimeSpan.FromSeconds(5), Ct));
            Assert.Contains("too many", refused.Message);

            await clients[0].DisposeAsync();
            clients.RemoveAt(0);
            LocalChannelClient? ninth = null;
            await WaitUntil(async () =>
            {
                try
                {
                    ninth = await LocalChannelClient.TryConnectAsync(server.Endpoint, TimeSpan.FromSeconds(5), Ct);
                }
                catch (LocalChannelException)
                {
                }
                return ninth != null;
            });
            clients.Add(ninth!);
            foreach (var client in clients)
                Assert.Equal(1, (await client.SendAsync(new JsonObject { ["op"] = "list" }, Ct))["tools"]!.AsArray().Count);
        }
        finally
        {
            foreach (var client in clients)
                await client.DisposeAsync();
        }
    }

    [Fact]
    public async Task ADroppedConnection_LeavesTheOthersAnswering()
    {
        if (OperatingSystem.IsWindows())
            return;

        var handler = new FakeHandler();
        await using var server = Start(Data("a"), handler);
        await using var keep = await Connect(server);

        // One companion sends a slow call and goes away mid-answer.
        var dropped = await RawConnect(server);
        var stream = new NetworkStream(dropped, ownsSocket: true);
        await stream.WriteAsync(Encoding.UTF8.GetBytes("""{"channel":1,"client":"mcp"}""" + "\n" + """{"id":1,"op":"call","tool":"slow"}""" + "\n"), Ct);
        await handler.SlowStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        dropped.Close(0);
        await stream.DisposeAsync();

        var answer = await keep.SendAsync(new JsonObject { ["op"] = "call", ["tool"] = "x" }, Ct);
        Assert.Equal("x:{}", answer["content"]![0]!["text"]!.GetValue<string>());
        Assert.True(keep.IsAlive);
    }

    [Fact]
    public async Task ATestEngine_OpensItsChannelInTheTestRunFolder_AndClosesIt()
    {
        if (OperatingSystem.IsWindows())
            return;

        string socket;
        using (var factory = new EngineFactory())
        {
            using (var client = factory.CreateApiClient())
                Assert.True((await client.GetAsync("/api/session", Ct)).IsSuccessStatusCode);
            var service = factory.Services.GetRequiredService<LocalChannelService>();
            await WaitUntil(() => Task.FromResult(service.Server != null));
            socket = service.Server!.Endpoint.SocketPath!;
            Assert.StartsWith(Path.GetFullPath(TestRunFolder.Path), socket);
            await using var channel = await LocalChannelClient.TryConnectAsync(service.Server.Endpoint, TimeSpan.FromSeconds(5), Ct);
            Assert.NotNull(channel);
        }

        Assert.False(File.Exists(socket));
    }

    /// <summary>
    /// Codex p1 #2: whatever listens at the socket's path must be this user's process. A test has no second user, so the
    /// peer's user id is injected; the real check (this user, said by the system) is what every other test connects with.
    /// </summary>
    [Fact]
    public async Task ACompanion_RefusesAChannelHeldByAnotherUser_OrOneWhoseUserItCantTell()
    {
        if (OperatingSystem.IsWindows())
            return;

        await using var server = Start(Data("a"));
        using (var raw = await RawConnect(server))
            Assert.Equal(UnixIdentity.CurrentUser, UnixIdentity.PeerOf(raw));

        var other = await Assert.ThrowsAsync<LocalChannelException>(() =>
            LocalChannelClient.TryConnectAsync(server.Endpoint, TimeSpan.FromSeconds(5), Ct, _ => UnixIdentity.CurrentUser + 1));
        Assert.Contains("another user", other.Message);
        var unknown = await Assert.ThrowsAsync<LocalChannelException>(() =>
            LocalChannelClient.TryConnectAsync(server.Endpoint, TimeSpan.FromSeconds(5), Ct, _ => null));
        Assert.Contains("another user", unknown.Message);

        // Refused before the hello: the engine served nobody, and this user's own companion still gets in.
        await using var client = await Connect(server);
        Assert.Equal(Environment.ProcessId, client.Pid);
    }

    /// <summary>Codex p1 #2: a run folder another user owns, or one others may write in, is never trusted.</summary>
    [Fact]
    public async Task ARunFolderOthersCanWriteIn_OrAnotherUserOwns_IsRefusedByBothSides()
    {
        if (OperatingSystem.IsWindows())
            return;

        await using var server = Start(Data("a"));
        foreach (var mode in new[] { 0b111_111_111, 0b111_111_000, 0b111_000_010 }) // 0777, 0770, 0702
        {
            File.SetUnixFileMode(Run, (UnixFileMode)mode);
            var refused = await Assert.ThrowsAsync<LocalChannelException>(() => LocalChannelClient.TryConnectAsync(server.Endpoint, TimeSpan.FromSeconds(5), Ct));
            Assert.Contains("others can write", refused.Message);
        }

        File.SetUnixFileMode(Run, (UnixFileMode)0b111_101_101); // 0755: readable, but only this user writes there
        await using (var client = await Connect(server))
            Assert.Equal(Environment.ProcessId, client.Pid);

        var owned = Assert.Throws<LocalChannelException>(() => LocalChannel.CheckRunDirectory(Run, UnixIdentity.CurrentUser + 1));
        Assert.Contains("another user", owned.Message);
        var engine = Assert.Throws<LocalChannelException>(() => LocalChannel.CreateRunDirectory(Run, UnixIdentity.CurrentUser + 1));
        Assert.Contains("another user", engine.Message);

        // A link there is refused too, and no folder at all is simply no channel.
        var link = Path.Combine(_root, "run-link");
        Directory.CreateSymbolicLink(link, Run);
        Assert.Throws<LocalChannelException>(() => LocalChannel.CheckRunDirectory(link));
        Assert.Null(await LocalChannelClient.TryConnectAsync(LocalChannel.EndpointFor(Path.Combine(_root, "absent"), Data("b")), TimeSpan.FromSeconds(2), Ct));
    }

    private LocalChannelServer Start(string data, ILocalChannelHandler? handler = null, LocalChannelServerOptions? options = null) =>
        LocalChannelServer.Start(LocalChannel.EndpointFor(Run, data), handler ?? new FakeHandler(), NullLogger.Instance, options);

    private static async Task<LocalChannelClient> Connect(LocalChannelServer server) =>
        await LocalChannelClient.TryConnectAsync(server.Endpoint, TimeSpan.FromSeconds(5), Ct) ?? throw new InvalidOperationException("No channel.");

    private static async Task<Socket> RawConnect(LocalChannelServer server)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(server.Endpoint.SocketPath!), Ct);
        return socket;
    }

    /// <summary>Sends a raw hello: the engine's answer, and whether it closed the connection after it.</summary>
    private static async Task<(JsonNode Answer, bool Closed)> RawHello(LocalChannelServer server, string hello)
    {
        using var socket = await RawConnect(server);
        await using var stream = new NetworkStream(socket);
        await stream.WriteAsync(Encoding.UTF8.GetBytes(hello + "\n"), Ct);
        var reader = new LineReader(stream, 1 << 20);
        var answer = JsonNode.Parse((await reader.ReadLineAsync(Ct))!)!;
        return (answer, await ReadOrEnd(reader) == null);
    }

    private static async Task<byte[]?> ReadOrEnd(LineReader reader)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            return await reader.ReadLineAsync(timeout.Token);
        }
        catch (IOException)
        {
            return null; // reset by the engine
        }
    }

    private static async Task WaitUntil(Func<Task<bool>> condition)
    {
        var deadline = Environment.TickCount64 + 10_000;
        while (!await condition())
        {
            if (Environment.TickCount64 > deadline)
                Assert.Fail("Timed out.");
            await Task.Delay(50, Ct);
        }
    }

    /// <summary>One tool, <c>fake</c>; a call answers <c>{tool}:{arguments}</c>, the tool <c>slow</c> after 300 ms.</summary>
    private sealed class FakeHandler : ILocalChannelHandler
    {
        public TaskCompletionSource SlowStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<JsonObject> ListAsync(CancellationToken ct) =>
            Task.FromResult(new JsonObject { ["tools"] = new JsonArray(new JsonObject { ["name"] = "fake" }), ["instructions"] = "test" });

        public async Task<JsonObject> CallAsync(string tool, JsonElement arguments, CancellationToken ct)
        {
            if (tool == "slow")
            {
                SlowStarted.TrySetResult();
                await Task.Delay(300, ct);
            }

            return new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = tool == "big" ? new string('x', 8000) : $"{tool}:{arguments.GetRawText()}",
                }),
                ["isError"] = false,
            };
        }
    }
}
