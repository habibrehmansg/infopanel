using System.Text.Json;
using InfoPanel.Api;
using InfoPanel.Hosting;
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
