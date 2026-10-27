using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using InfoPanel.Api;
using InfoPanel.Hosting;
using InfoPanel.Output;
using InfoPanel.Preview;
using InfoPanel.Spec;
using InfoPanel.Tests.Preview;
using Xunit;

namespace InfoPanel.Tests.Contract;

/// <summary>
/// The preview socket against spec/openapi.json's <c>x-infopanel-socket</c>: the subprotocol is the engine's, the
/// messages the engine writes are <c>serverMessage</c>s, and the socket checks the test clients run catch what isn't.
/// </summary>
public class SocketConformanceTests(EngineFactory factory) : IClassFixture<EngineFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void TheSocketOperation_NamesTheEnginesSubprotocol_AndTheMessageDefinitions()
    {
        var socket = ApiContract.Instance.Socket;
        Assert.Equal(EngineRuntime.WebSocketProtocol, socket.Subprotocol);
        Assert.Equal(SpecSchemas.Api + "#/$defs/clientMessage", socket.ClientMessage);
        Assert.Equal(SpecSchemas.Api + "#/$defs/serverMessage", socket.ServerMessage);
        Assert.Equal(SpecSchemas.Api + "#/$defs/frameHeader", socket.FrameHeader);
        Assert.Equal(["auth", "subscribe", "view", "unsubscribe"], ApiContract.MessageTypes(socket.ClientMessage));
        Assert.Equal(["hello", "doc", "profiles", "error"], ApiContract.MessageTypes(socket.ServerMessage));
    }

    [Fact]
    public void TheMessagesTheEngineWrites_AreServerMessages()
    {
        // As the socket handler writes them: the spec model with the format's options.
        foreach (var message in new object[]
                 {
                     new HelloMessage { Type = "hello", Protocol = ReadEndpoints.Protocol },
                     new ProfilesMessage { Type = "profiles", Version = 1 },
                     new ErrorMessage { Type = "error", Message = "No profile 'x'." },
                     new DocEvent { Type = "doc", ProfileId = "3dd19c0e-675a-4591-b096-83417853861f", Rev = 1, Dirty = false, CanUndo = false, CanRedo = false },
                 })
        {
            Assert.Null(SocketContract.CheckServerText(JsonSerializer.Serialize(message, message.GetType(), SpecJson.Options)));
        }
    }

    [Theory]
    [InlineData("""{"type":"hello","protocol":2,"engine":"x"}""")]
    [InlineData("""{"type":"hello"}""")]
    [InlineData("""{"type":"welcome","protocol":2}""")]
    [InlineData("""{"type":"error","message":7}""")]
    [InlineData("""not json""")]
    public void AServerMessageOutsideTheSpec_IsCaught(string text) => Assert.NotNull(SocketContract.CheckServerText(text));

    [Theory]
    [InlineData("""{"type":"subscribe","profileId":"3dd19c0e-675a-4591-b096-83417853861f","scale":0}""")]
    [InlineData("""{"type":"subscribe"}""")]
    [InlineData("""{"type":"view","maxFps":2.5}""")]
    [InlineData("""{"type":"dance"}""")]
    public void AClientMessageOutsideTheSpec_IsCaught(string text) => Assert.NotNull(SocketContract.CheckClientText(text));

    [Fact]
    public void AClientMessageOverTheCap_IsCaught()
    {
        Assert.Null(SocketContract.CheckClientText("""{"type":"unsubscribe","pad":""}"""));
        Assert.NotNull(SocketContract.CheckClientText("{\"type\":\"unsubscribe\",\"pad\":\"" + new string('x', PreviewSocketHandler.MaxClientMessageBytes) + "\"}"));
    }

    [Fact]
    public void AFrameHeaderOutsideTheSpec_IsCaught()
    {
        var header = new FrameHeader
        {
            ProfileId = "3dd19c0e-675a-4591-b096-83417853861f", Rev = 1, Tick = 0, FrameId = 0, Encoding = "png", Scale = 1,
            PixelWidth = 10, PixelHeight = 10, ProfileWidth = 10, ProfileHeight = 10, ItemBounds = [],
        };
        Assert.Null(SocketContract.CheckFrame(FrameCodec.Encode(header, [1, 2, 3])));
        Assert.NotNull(SocketContract.CheckFrame(FrameCodec.Encode(header with { Scale = 5 }, [1, 2, 3])));
        Assert.NotNull(SocketContract.CheckFrame([1, 2, 3]));
    }

    [Fact]
    public async Task AWholeMaxFps_InTheSpellingsAnIntegerAllows_IsAccepted()
    {
        using var api = factory.CreateApiClient();
        var id = JsonDocument.Parse(await api.GetStringAsync("/api/profiles", Ct)).RootElement[0].GetProperty("id").GetString();
        await using var client = await PreviewClient.ConnectAsync(factory, Ct);
        await client.ReceiveTextAsync("hello", Ct);

        // clientMessage's maxFps is a JSON Schema integer, which 10.0 and 1e1 are.
        await client.SendTextAsync($$"""{"type":"subscribe","profileId":"{{id}}","scale":0.25,"maxFps":10.0}""", Ct);
        await client.ReceiveTextAsync("doc", Ct);
        await client.ReceiveFrameAsync(Ct);
        await client.SendTextAsync("""{"type":"view","maxFps":1e1}""", Ct);
        // The engine answers messages in order: an error for either would come before the resubscription's doc.
        await client.SendTextAsync($$"""{"type":"subscribe","profileId":"{{id}}","scale":0.25,"maxFps":3e1}""", Ct);
        await client.ReceiveTextAsync("doc", Ct);

        Assert.DoesNotContain("error", client.ReceivedTypes);
        Assert.Equal(["subscribe", "view"], client.SentTypes.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task MaxFps_IsReadAsClientMessagesInteger_Exactly()
    {
        using var api = factory.CreateApiClient();
        var id = JsonDocument.Parse(await api.GetStringAsync("/api/profiles", Ct)).RootElement[0].GetProperty("id").GetString();
        await using var client = await PreviewClient.ConnectAsync(factory, Ct);
        await client.ReceiveTextAsync("hello", Ct);
        await client.SendAsync(new { type = "subscribe", profileId = id, scale = 0.25, maxFps = 10 }, Ct);
        await client.ReceiveTextAsync("doc", Ct);

        // Whole numbers from 1 to 2^31 - 1, in any spelling: accepted (an error for any would come before the doc).
        foreach (var fps in new[] { "30.0", "3e1", "2147483647" })
            await client.SendTextAsync($$"""{"type":"view","maxFps":{{fps}}}""", Ct);
        await client.SendAsync(new { type = "subscribe", profileId = id, scale = 0.25, maxFps = 10 }, Ct);
        await client.ReceiveTextAsync("doc", Ct);
        Assert.DoesNotContain("error", client.ReceivedTypes);

        // Not an integer (however close to one a double would round it), or over the maximum: each answered with an error
        // about maxFps. Sent without a subscription and for an absent profile, so no frames follow either way: a value the
        // engine took would draw "No profile" instead.
        await client.SendAsync(new { type = "unsubscribe" }, Ct);
        foreach (var fps in new[] { "0.99999999999999999", "2147483647.0000001", "2147483648", "2.5" })
        {
            await client.SendTextAsync($$"""{"type":"subscribe","profileId":"00000000-0000-4000-8000-0000000000aa","maxFps":{{fps}}}""", Ct);
            await client.ReceiveTextAsync("error", Ct, e => e.GetProperty("message").GetString()!.Contains("maxFps", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task AnyPositiveScale_IsReadAndLimited_AsFrameSizeSays()
    {
        // A small canvas, so the largest scale is a small frame.
        using var api = factory.CreateApiClient();
        var id = TestDocs.NewId();
        using (var created = await api.PostAsync("/api/profiles", TestDocs.Json(new JsonObject { ["id"] = id, ["name"] = "Scales", ["width"] = 100, ["height"] = 50 }), Ct))
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        await using var client = await PreviewClient.ConnectAsync(factory, Ct);
        await client.ReceiveTextAsync("hello", Ct);
        await client.SendAsync(new { type = "subscribe", profileId = id, scale = 0.25, maxFps = 10 }, Ct);
        var (first, _) = await client.ReceiveFrameAsync(Ct, h => h.Scale == 0.25);

        // Beyond single precision either way: the largest and the smallest scale (docs/api.md#frame-size), not an error.
        var largest = FrameScale.Normalize(FrameScale.Max, first.ProfileWidth, first.ProfileHeight);
        var smallest = FrameScale.Normalize(FrameScale.Min, first.ProfileWidth, first.ProfileHeight);
        await client.SendTextAsync("""{"type":"view","scale":1e100}""", Ct);
        await client.ReceiveFrameAsync(Ct, h => h.Scale == largest);
        // 1e-100 is a frameRequestScale (JSON Schema compares numbers by value), but the engine's schema validator reads it
        // as the decimal 0 and calls it invalid, so it goes out past the client's clientMessage check.
        await client.Socket.SendAsync(System.Text.Encoding.UTF8.GetBytes("""{"type":"view","scale":1e-100}"""), System.Net.WebSockets.WebSocketMessageType.Text, true, Ct);
        await client.ReceiveFrameAsync(Ct, h => h.Scale == smallest);
        Assert.DoesNotContain("error", client.ReceivedTypes);
    }

    [Fact]
    public async Task ThePreviewSocket_SendsOnlyServerMessagesAndFrames()
    {
        using var api = factory.CreateApiClient();
        var profiles = JsonDocument.Parse(await api.GetStringAsync("/api/profiles", Ct)).RootElement;
        var id = profiles[0].GetProperty("id").GetString();

        // PreviewClient checks every message against the spec as it receives it.
        await using var client = await PreviewClient.ConnectAsync(factory, Ct);
        await client.ReceiveTextAsync("hello", Ct);
        await client.SendAsync(new { type = "subscribe", profileId = id, scale = 0.25, maxFps = 10 }, Ct);
        await client.ReceiveTextAsync("doc", Ct);
        await client.ReceiveFrameAsync(Ct);
        foreach (var type in new[] { "hello", "doc", "frame" })
            Assert.Contains(type, client.ReceivedTypes);
        Assert.Contains("subscribe", client.SentTypes);
    }
}
