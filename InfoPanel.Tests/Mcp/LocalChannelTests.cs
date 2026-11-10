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
            // left there as a crash leaves it. It never listens: a copy of this process forked by another test's program
            // launch holds its descriptor until that launch's exec, which can stall for seconds, and a listening copy
            // would take the companion's connect and then drop it.
            var elsewhere = Path.Combine(Run, "crashed.sock");
            crashed.Bind(new UnixDomainSocketEndPoint(elsewhere));
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
        Assert.True((await second.SendAsync(new JsonObject { ["op"] = LocalChannel.OpenEditorOp }, Ct))["opened"]!.GetValue<bool>());
    }

    /// <summary>
    /// The channel speaks version 2 since it stopped carrying tool lists and calls: a companion of version 1 (an AI app's
    /// front from before) is refused at its hello, and so is an AI app's front of any version.
    /// </summary>
    [Fact]
    public async Task TheHello_NamesTheEngine_AndAnotherChannelVersionOrClientIsRefused()
    {
        if (OperatingSystem.IsWindows())
            return;

        await using var server = Start(Data("a"));
        await using (var client = await LocalChannelClient.TryConnectAsync(server.Endpoint, TimeSpan.FromSeconds(5), Ct, client: LocalChannel.LaunchClient))
        {
            Assert.Equal(2, client!.Hello["channel"]!.GetValue<int>());
            Assert.False(string.IsNullOrEmpty(client.Hello["engineVersion"]!.GetValue<string>()));
            Assert.False(string.IsNullOrEmpty(client.Hello["engineBuild"]!.GetValue<string>()));
        }

        var (version, versionClosed) = await RawHello(server, """{"channel":1,"client":"launch"}""");
        Assert.Equal(2, version["channel"]!.GetValue<int>());
        Assert.Contains("channel 1", version["refused"]!.GetValue<string>());
        Assert.True(versionClosed);

        foreach (var someone in new[] { "someone", "mcp" })
        {
            var (client2, clientClosed) = await RawHello(server, $$"""{"channel":2,"client":"{{someone}}"}""");
            Assert.Equal("unknown client", client2["refused"]!.GetValue<string>());
            Assert.True(clientClosed);
        }
    }

    /// <summary>
    /// A launch's requests are answered in order on its connection: <c>open-editor</c> opens the editor, and anything else
    /// (the tool list and calls channel 1 carried included) is an error that opens nothing.
    /// </summary>
    [Fact]
    public async Task ALaunchsRequests_AreAnsweredInOrder_OnlyOpenEditorOpensTheEditor()
    {
        if (OperatingSystem.IsWindows())
            return;

        var editor = new FakeEditor { Slow = true };
        await using var server = Start(Data("a"), editor);
        using var socket = await RawConnect(server);
        await using var stream = new NetworkStream(socket);
        await stream.WriteAsync(Encoding.UTF8.GetBytes("""{"channel":2,"client":"launch"}""" + "\n"), Ct);
        // Three requests at once: the first one takes longer, but its answer still comes first.
        await stream.WriteAsync(Encoding.UTF8.GetBytes(
            """{"id":"a","op":"open-editor"}""" + "\n" +
            """{"id":"b","op":"list"}""" + "\n" +
            """{"id":"c","op":"call","tool":"list_profiles","arguments":{}}""" + "\n"), Ct);
        var reader = new LineReader(stream, 1 << 20);
        Assert.NotNull(await reader.ReadLineAsync(Ct)); // hello
        var first = JsonNode.Parse((await reader.ReadLineAsync(Ct))!)!;
        var second = JsonNode.Parse((await reader.ReadLineAsync(Ct))!)!;
        var third = JsonNode.Parse((await reader.ReadLineAsync(Ct))!)!;
        Assert.Equal("a", first["id"]!.GetValue<string>());
        Assert.True(first["opened"]!.GetValue<bool>());
        Assert.Equal("b", second["id"]!.GetValue<string>());
        Assert.Equal("unknown op 'list'", second["error"]!.GetValue<string>());
        Assert.Null(second["tools"]);
        Assert.Equal("c", third["id"]!.GetValue<string>());
        Assert.Equal("unknown op 'call'", third["error"]!.GetValue<string>());
        Assert.Null(third["content"]);
        Assert.Equal(1, editor.Opened);
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
        await stream.WriteAsync(Encoding.UTF8.GetBytes("""{"channel":2,"client":"launch"}""" + "\n"), Ct);
        var reader = new LineReader(stream, 1 << 20);
        Assert.NotNull(await reader.ReadLineAsync(Ct));
        var big = """{"id":1,"op":"open-editor","pad":""" + "\"" + new string('x', 5000) + "\"}\n";
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
        var answer = await other.SendAsync(new JsonObject { ["op"] = LocalChannel.OpenEditorOp, ["pad"] = new string('y', 3000) }, Ct);
        Assert.True(answer["opened"]!.GetValue<bool>());
    }

    /// <summary>
    /// After the hello the channel's one request, <c>{"id","op":"open-editor"}</c>, is under 100 bytes: with the default
    /// limits a 64 KiB line closes the connection rather than being buffered and answered (8 connections may each send one).
    /// </summary>
    [Fact]
    public async Task WithTheDefaultLimits_ALineOfAFewKiBAfterTheHello_ClosesTheConnection()
    {
        if (OperatingSystem.IsWindows())
            return;

        var editor = new FakeEditor();
        await using var server = Start(Data("a"), editor);
        using var socket = await RawConnect(server);
        await using var stream = new NetworkStream(socket);
        await stream.WriteAsync(Encoding.UTF8.GetBytes("""{"channel":2,"client":"launch"}""" + "\n"), Ct);
        var reader = new LineReader(stream, 1 << 20);
        Assert.NotNull(await reader.ReadLineAsync(Ct)); // hello

        var big = """{"id":1,"op":"open-editor","pad":""" + "\"" + new string('x', 64 * 1024) + "\"}\n";
        try
        {
            await stream.WriteAsync(Encoding.UTF8.GetBytes(big), Ct);
        }
        catch (IOException)
        {
            // The engine may close before it reads everything.
        }

        var answer = await ReadOrEnd(reader);
        Assert.True(answer == null, "a 64 KiB line was answered: " + (answer == null ? "" : Encoding.UTF8.GetString(answer)));
        Assert.Equal(0, editor.Opened);
    }

    /// <summary>
    /// A launch's request within the cap whose answer is over it (the error echoes the op) gets a short error instead, which
    /// echoes nothing large, and the connection goes on: a line over the cap would make the client take the engine for gone.
    /// </summary>
    [Fact]
    public async Task AnAnswerOverTheCap_IsAShortErrorInstead_AndTheConnectionGoesOn()
    {
        if (OperatingSystem.IsWindows())
            return;

        var editor = new FakeEditor();
        await using var server = Start(Data("a"), editor, new LocalChannelServerOptions { MaxLineBytes = 4096 });
        using var socket = await RawConnect(server);
        await using var stream = new NetworkStream(socket);
        await stream.WriteAsync(Encoding.UTF8.GetBytes("""{"channel":2,"client":"launch"}""" + "\n"), Ct);
        var reader = new LineReader(stream, 1 << 20);
        Assert.NotNull(await reader.ReadLineAsync(Ct)); // hello

        var op = new string('x', 4070); // the request is 4086 bytes, the error that echoes the op is more than 4096
        await stream.WriteAsync(Encoding.UTF8.GetBytes($$"""{"id":1,"op":"{{op}}"}""" + "\n" + """{"id":2,"op":"open-editor"}""" + "\n"), Ct);
        var raw = (await reader.ReadLineAsync(Ct))!;
        Assert.True(raw.Length < 1024);
        var tooLong = JsonNode.Parse(raw)!;
        Assert.Equal(1, tooLong["id"]!.GetValue<int>());
        Assert.Contains("too long", tooLong["error"]!.GetValue<string>());
        Assert.DoesNotContain("xxxx", tooLong.ToJsonString());

        var next = JsonNode.Parse((await reader.ReadLineAsync(Ct))!)!;
        Assert.Equal(2, next["id"]!.GetValue<int>());
        Assert.True(next["opened"]!.GetValue<bool>());
        Assert.Equal(1, editor.Opened);
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
                Assert.True((await client.SendAsync(new JsonObject { ["op"] = LocalChannel.OpenEditorOp }, Ct))["opened"]!.GetValue<bool>());
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

        var editor = new FakeEditor { Slow = true };
        await using var server = Start(Data("a"), editor);
        await using var keep = await Connect(server);

        // One launch asks to open the editor, which takes a while, and goes away mid-answer.
        var dropped = await RawConnect(server);
        var stream = new NetworkStream(dropped, ownsSocket: true);
        await stream.WriteAsync(Encoding.UTF8.GetBytes("""{"channel":2,"client":"launch"}""" + "\n" + """{"id":1,"op":"open-editor"}""" + "\n"), Ct);
        await editor.SlowStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        dropped.Close(0);
        await stream.DisposeAsync();

        var answer = await keep.SendAsync(new JsonObject { ["op"] = LocalChannel.OpenEditorOp }, Ct);
        Assert.True(answer["opened"]!.GetValue<bool>());
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

    private LocalChannelServer Start(string data, FakeEditor? editor = null, LocalChannelServerOptions? options = null) =>
        LocalChannelServer.Start(LocalChannel.EndpointFor(Run, data), NullLogger.Instance, options, (editor ?? new FakeEditor()).OpenAsync);

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

    /// <summary>What a launch's <c>open-editor</c> runs: counts the opens; <see cref="Slow"/>, each takes 300 ms.</summary>
    private sealed class FakeEditor
    {
        private int _opened;

        public bool Slow { get; init; }

        public int Opened => Volatile.Read(ref _opened);

        public TaskCompletionSource SlowStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task OpenAsync(CancellationToken ct)
        {
            if (Slow)
            {
                SlowStarted.TrySetResult();
                await Task.Delay(300, ct);
            }

            Interlocked.Increment(ref _opened);
        }
    }
}
