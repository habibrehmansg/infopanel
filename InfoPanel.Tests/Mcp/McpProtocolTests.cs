using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using InfoPanel.Hosting;
using InfoPanel.Mcp.Front;
using Xunit;

namespace InfoPanel.Tests.Mcp;

/// <summary>The MCP server over in-memory streams, with a stand-in for the engine.</summary>
public sealed class McpProtocolTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Initialize_EchoesAVersionItKnows_AndAnswersAnyOtherWithItsNewest()
    {
        await using var front = new Front(new FakeBackend());
        await front.SendAsync("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"t","version":"1"}}}""");
        var known = await front.NextAsync();
        Assert.Equal(1, known["id"]!.GetValue<int>());
        Assert.Equal("2025-03-26", known["result"]!["protocolVersion"]!.GetValue<string>());
        Assert.True(known["result"]!["capabilities"]!["tools"]!["listChanged"]!.GetValue<bool>());
        Assert.Equal("infopanel", known["result"]!["serverInfo"]!["name"]!.GetValue<string>());
        Assert.Equal("built-in instructions", known["result"]!["instructions"]!.GetValue<string>());

        await front.SendAsync("""{"jsonrpc":"2.0","id":"two","method":"initialize","params":{"protocolVersion":"1999-01-01"}}""");
        var unknown = await front.NextAsync();
        Assert.Equal("two", unknown["id"]!.GetValue<string>());
        Assert.Equal(McpServer.ProtocolVersions[^1], unknown["result"]!["protocolVersion"]!.GetValue<string>());
    }

    [Fact]
    public async Task Ping_AnUnknownMethod_UnreadableJson_AndABatch()
    {
        await using var front = new Front(new FakeBackend());
        await front.SendAsync("""{"jsonrpc":"2.0","id":7,"method":"ping"}""");
        var ping = await front.NextAsync();
        Assert.Equal(7, ping["id"]!.GetValue<int>());
        Assert.Empty(ping["result"]!.AsObject());

        await front.SendAsync("""{"jsonrpc":"2.0","id":8,"method":"resources/list"}""");
        var unknown = await front.NextAsync();
        Assert.Equal(8, unknown["id"]!.GetValue<int>());
        Assert.Equal(-32601, unknown["error"]!["code"]!.GetValue<int>());

        await front.SendAsync("""{"jsonrpc":"2.0","id":9,"method":""" + "\n");
        var parse = await front.NextAsync();
        Assert.True(parse.AsObject().ContainsKey("id"));
        Assert.Null(parse["id"]);
        Assert.Equal(-32700, parse["error"]!["code"]!.GetValue<int>());

        await front.SendAsync("""[{"jsonrpc":"2.0","id":10,"method":"ping"}]""");
        Assert.Equal(-32600, (await front.NextAsync())["error"]!["code"]!.GetValue<int>());

        await front.SendAsync("""{"jsonrpc":"2.0","id":11,"method":"tools/call","params":{"arguments":{}}}""");
        var noName = await front.NextAsync();
        Assert.Equal(11, noName["id"]!.GetValue<int>());
        Assert.Equal(-32602, noName["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public async Task Notifications_AreNeverAnswered()
    {
        await using var front = new Front(new FakeBackend());
        await front.SendAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
        await front.SendAsync("""{"jsonrpc":"2.0","method":"notifications/something-new","params":{}}""");
        await front.SendAsync("""{"jsonrpc":"2.0","method":"ping"}""");
        await front.SendAsync("""{"jsonrpc":"2.0","id":1,"method":"ping"}""");
        Assert.Equal(1, (await front.NextAsync())["id"]!.GetValue<int>());
        Assert.Null(await front.NextAsync(TimeSpan.FromMilliseconds(300)));
    }

    [Fact]
    public async Task ACall_DoesntHoldUpPing_AndACancelledCallIsNotAnswered()
    {
        var backend = new FakeBackend();
        await using var front = new Front(backend);
        await front.SendAsync("""{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"slow","arguments":{}}}""");
        await backend.SlowStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await front.SendAsync("""{"jsonrpc":"2.0","id":6,"method":"ping"}""");
        Assert.Equal(6, (await front.NextAsync())["id"]!.GetValue<int>());

        await front.SendAsync("""{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":5,"reason":"user"}}""");
        await Task.Delay(100, Ct);
        backend.ReleaseSlow.TrySetResult();
        await front.SendAsync("""{"jsonrpc":"2.0","id":7,"method":"ping"}""");
        Assert.Equal(7, (await front.NextAsync())["id"]!.GetValue<int>());
        Assert.Null(await front.NextAsync(TimeSpan.FromMilliseconds(300)));
    }

    [Fact]
    public async Task ToolsList_IsTheBuiltInOneWithoutAnEngine_TheEnginesWithOne_AndAChangeIsAnnounced()
    {
        var backend = new FakeBackend();
        await using var front = new Front(backend);
        await front.SendAsync("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");
        var builtIn = await front.NextAsync();
        Assert.Equal("built_in", builtIn["result"]!["tools"]![0]!["name"]!.GetValue<string>());

        // A call reaches the engine; afterwards its list reads differently, so the client hears of it.
        backend.Engine = new JsonObject { ["tools"] = new JsonArray(new JsonObject { ["name"] = "from_engine" }), ["instructions"] = "live" };
        await front.SendAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"echo","arguments":{"a":1}}}""");
        var call = await front.NextAsync();
        Assert.Equal(2, call["id"]!.GetValue<int>());
        Assert.Equal("echo {\"a\":1}", call["result"]!["content"]![0]!["text"]!.GetValue<string>());
        Assert.False(call["result"]!["isError"]!.GetValue<bool>());
        var changed = await front.NextAsync();
        Assert.Equal("notifications/tools/list_changed", changed["method"]!.GetValue<string>());
        Assert.False(changed.AsObject().ContainsKey("id"));

        await front.SendAsync("""{"jsonrpc":"2.0","id":3,"method":"tools/list"}""");
        Assert.Equal("from_engine", (await front.NextAsync())["result"]!["tools"]![0]!["name"]!.GetValue<string>());

        // The same list again: no announcement.
        await front.SendAsync("""{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"fail"}}""");
        var failed = await front.NextAsync();
        Assert.True(failed["result"]!["isError"]!.GetValue<bool>());
        Assert.Null(await front.NextAsync(TimeSpan.FromMilliseconds(300)));
    }

    /// <summary>
    /// Codex p1 #4: params of any shape, for every method, are answered (a JSON-RPC error where the shape is wrong) or
    /// ignored (a notification), and the server goes on serving: a ping after each one is answered.
    /// </summary>
    [Fact]
    public async Task ParamsOfAnyShape_ForEveryMethod_NeverStopTheServer()
    {
        string[] shapes =
        [
            "[]", "[1,2]", "\"text\"", "7", "true", "null", "{}",
            """{"protocolVersion":[]}""", """{"protocolVersion":{"a":1}}""", """{"protocolVersion":7}""",
            """{"name":[]}""", """{"name":{}}""", """{"name":"echo","arguments":[]}""", """{"name":"echo","arguments":"x"}""",
            """{"requestId":[]}""", """{"requestId":{"a":[1]}}""", """{"requestId":null}""",
            """{"name":"echo","arguments":{"a":1}}""",
        ];
        // What each method answers for a params of the wrong kind (not an object, where it needs one).
        var expected = new Dictionary<string, Func<string, int?>>
        {
            ["initialize"] = p => p.StartsWith('{') || p == "null" ? null : -32602,
            ["ping"] = _ => null,
            ["tools/list"] = _ => null,
            ["tools/call"] = p => p.StartsWith("""{"name":"echo","arguments":{""", StringComparison.Ordinal) ? null : -32602,
        };

        await using var front = new Front(new FakeBackend());
        var id = 0;
        foreach (var shape in shapes)
        {
            foreach (var (method, error) in expected)
            {
                await front.SendAsync($$"""{"jsonrpc":"2.0","id":{{++id}},"method":"{{method}}","params":{{shape}}}""");
                var answer = await front.NextAsync();
                Assert.Equal(id, answer["id"]!.GetValue<int>());
                var code = answer["error"]?["code"]?.GetValue<int>();
                Assert.True(error(shape) == code, $"{method} with params {shape}: {answer.ToJsonString()}");
                if (method == "tools/call" && code == null)
                    Assert.NotNull(answer["result"]);
            }

            foreach (var notification in new[] { "notifications/cancelled", "notifications/initialized", "notifications/something-new" })
                await front.SendAsync($$"""{"jsonrpc":"2.0","method":"{{notification}}","params":{{shape}}}""");
            await front.SendAsync($$"""{"jsonrpc":"2.0","id":{{++id}},"method":"ping"}""");
            var ping = await front.NextAsync();
            Assert.Equal(id, ping["id"]!.GetValue<int>());
            Assert.NotNull(ping["result"]);
        }

        // A member given twice is an invalid request, answered with a null id (which member counts can't be told, so a
        // notification's too); a method or id of the wrong kind too.
        foreach (var odd in new[]
        {
            """{"jsonrpc":"2.0","id":1,"id":2,"method":"ping"}""",
            """{"jsonrpc":"2.0","id":1,"method":"ping","method":"initialize"}""",
            """{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":1,"requestId":2}}""",
            """{"jsonrpc":"2.0","id":3,"method":7}""",
            """{"jsonrpc":"2.0","id":[],"method":{}}""",
        })
        {
            await front.SendAsync(odd);
            Assert.Equal(-32600, (await front.NextAsync())["error"]!["code"]!.GetValue<int>());
        }

        await front.SendAsync("""{"jsonrpc":"2.0","id":"last","method":"ping"}""");
        Assert.Equal("last", (await front.NextAsync())["id"]!.GetValue<string>());
        Assert.False(front.Run.IsCompleted);
    }

    [Fact]
    public async Task TheServer_EndsWhenItsInputEnds()
    {
        var front = new Front(new FakeBackend());
        await front.DisposeAsync();
        Assert.True(front.Run.IsCompletedSuccessfully);
    }

    /// <summary>The server over two in-memory pipes.</summary>
    private sealed class Front : IAsyncDisposable
    {
        private readonly Pipe _in = new();
        private readonly Pipe _out = new();
        private readonly LineReader _reader;

        public Front(IMcpBackend backend)
        {
            Run = new McpServer(_in.Reader.AsStream(), _out.Writer.AsStream(), backend, TextWriter.Null).RunAsync(Ct);
            _reader = new LineReader(_out.Reader.AsStream(), 1 << 24);
        }

        public Task Run { get; }

        public async Task SendAsync(string line) => await _in.Writer.WriteAsync(Encoding.UTF8.GetBytes(line.EndsWith('\n') ? line : line + "\n"), Ct);

        public async Task<JsonNode> NextAsync() => await NextAsync(TimeSpan.FromSeconds(10)) ?? throw new TimeoutException("No message.");

        private Task<byte[]?>? _pending;

        public async Task<JsonNode?> NextAsync(TimeSpan wait)
        {
            _pending ??= _reader.ReadLineAsync(Ct).AsTask();
            if (await Task.WhenAny(_pending, Task.Delay(wait, Ct)) != _pending)
                return null;
            var line = await _pending;
            _pending = null;
            return line == null ? null : JsonNode.Parse(line);
        }

        public async ValueTask DisposeAsync()
        {
            await _in.Writer.CompleteAsync();
            await Run.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        }
    }

    private sealed class FakeBackend : IMcpBackend
    {
        public JsonObject? Engine { get; set; }

        public TaskCompletionSource SlowStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseSlow { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<JsonObject?> TryListAsync(CancellationToken ct) => Task.FromResult((JsonObject?)Engine?.DeepClone().AsObject());

        public JsonObject BuiltInList() =>
            new() { ["tools"] = new JsonArray(new JsonObject { ["name"] = "built_in" }), ["instructions"] = "built-in instructions" };

        public async Task<JsonObject> CallAsync(string tool, JsonElement arguments, CancellationToken ct)
        {
            if (tool == "slow")
            {
                SlowStarted.TrySetResult();
                await ReleaseSlow.Task;
            }

            return new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = $"{tool} {arguments.GetRawText()}" }),
                ["isError"] = tool == "fail",
            };
        }
    }
}
