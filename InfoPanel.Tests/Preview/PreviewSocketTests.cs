using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using InfoPanel.Hosting;
using InfoPanel.Output;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;
using Xunit;

namespace InfoPanel.Tests.Preview;

/// <summary><c>/ws</c> end to end on the test server, with the engine's own render loops (real clock).</summary>
public class PreviewSocketTests(EngineFactory factory) : IClassFixture<EngineFactory>
{
    private static readonly byte[] PngSignature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private RenderService Render => factory.Services.GetRequiredService<RenderService>();

    private async Task<PreviewClient> Connect()
    {
        var client = await PreviewClient.ConnectAsync(factory, Ct);
        var hello = await client.ReceiveTextAsync("hello", Ct);
        Assert.Equal(1, hello.GetProperty("protocol").GetInt32());
        return client;
    }

    private async Task<Guid> ExampleProfileId()
    {
        using var api = factory.CreateApiClient();
        var profiles = await api.GetFromJsonAsync<JsonElement>("/api/profiles", Ct);
        return profiles.EnumerateArray().First(p => p.GetProperty("name").GetString() == "Wide bar").GetProperty("id").GetGuid();
    }

    /// <summary>A new 480×320 profile with one shape; returns its id and the shape's.</summary>
    private async Task<(Guid Profile, string Shape)> CreateProfile(string name)
    {
        using var api = factory.CreateApiClient();
        using var created = await api.PostAsJsonAsync("/api/profiles", new { name, width = 480, height = 320 }, Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        using var added = await api.PostAsJsonAsync($"/api/profiles/{id}/items", new { kind = "shape" }, Ct);
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        var shape = (await added.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("items")[0].GetProperty("id").GetString()!;
        return (id, shape);
    }

    [Fact]
    public async Task Connect_IsAcceptedWithInfopanelV1Only_AndSaysHello()
    {
        await using var client = await PreviewClient.ConnectAsync(factory, Ct);
        Assert.Equal(EngineRuntime.WebSocketProtocol, client.Socket.SubProtocol);

        var first = await client.ReceiveAsync(Ct);
        Assert.Equal("""{"type":"hello","protocol":1}""", first.Json!.Value.GetRawText());
    }

    [Theory]
    [InlineData("infopanel.v1", "auth.wrong")]
    [InlineData("infopanel.v1", "auth.wrong", "auth.")]
    [InlineData("auth." + EngineFactory.Token)]
    public async Task Connect_WithoutBothSubprotocols_IsRejected(params string[] protocols)
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(() => PreviewClient.ConnectAsync(factory, Ct, protocols));
        Assert.Contains("401", error.Message);
    }

    [Fact]
    public async Task Subscribe_SendsTheDocState_AndAPngFrameWithBounds()
    {
        var id = await ExampleProfileId();
        await using var client = await Connect();
        await client.SendAsync(new { type = "subscribe", profileId = id, scale = 1, maxFps = 30 }, Ct);

        var doc = await client.ReceiveTextAsync("doc", Ct);
        Assert.Equal(id, doc.GetProperty("profileId").GetGuid());
        Assert.True(doc.GetProperty("revision").GetInt64() >= 1);
        foreach (var key in new[] { "dirty", "canUndo", "canRedo" })
            Assert.True(doc.GetProperty(key).ValueKind is JsonValueKind.True or JsonValueKind.False, key);

        var (header, png) = await client.ReceiveFrameAsync(Ct);
        Assert.Equal(id, header.ProfileId);
        Assert.Equal("png", header.Encoding);
        Assert.Equal((1f, 1920, 480, 1920, 480), (header.Scale, header.PixelWidth, header.PixelHeight, header.ProfileWidth, header.ProfileHeight));
        Assert.True(png.Span.StartsWith(PngSignature));
        Assert.NotEmpty(header.Bounds);
        Assert.Contains(header.Bounds, b => b.IsGroup);

        using var bitmap = SKBitmap.Decode(png.ToArray());
        Assert.Equal((1920, 480), (bitmap.Width, bitmap.Height));
    }

    [Fact]
    public async Task APatch_IsFollowedWithin500ms_ByAFrameOfThatRevision_AndADocEvent()
    {
        var (id, shape) = await CreateProfile("Patch preview");
        await using var client = await Connect();
        await client.SendAsync(new { type = "subscribe", profileId = id, scale = 1, maxFps = 30 }, Ct);
        await client.ReceiveFrameAsync(Ct);

        using var api = factory.CreateApiClient();
        using var response = await api.PatchAsJsonAsync($"/api/profiles/{id}/items",
            new { patches = new[] { new { id = shape, set = new { x = 200 } } } }, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var revision = (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("revision").GetInt64();
        var acknowledged = Stopwatch.StartNew();

        var gotFrame = false;
        var gotDoc = false;
        while (!(gotFrame && gotDoc))
        {
            var message = await client.ReceiveAsync(Ct, TimeSpan.FromMilliseconds(500) - acknowledged.Elapsed);
            if (message.Frame is { } header && header.Revision >= revision)
            {
                Assert.Contains(header.Bounds, b => b.Id == Guid.Parse(shape) && b.X == 200);
                gotFrame = true;
            }
            else if (message.Json is { } json && json.GetProperty("type").GetString() == "doc" && json.GetProperty("revision").GetInt64() >= revision)
            {
                Assert.True(json.GetProperty("dirty").GetBoolean());
                Assert.True(json.GetProperty("canUndo").GetBoolean());
                gotDoc = true;
            }
        }

        Assert.True(acknowledged.ElapsedMilliseconds < 500, $"{acknowledged.ElapsedMilliseconds} ms");

        // Undo (another cause of a revision change) also sends a doc event.
        using var undo = await api.PostAsync($"/api/profiles/{id}/undo", null, Ct);
        var undone = (await undo.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("revision").GetInt64();
        var doc = await client.ReceiveTextAsync("doc", Ct, d => d.GetProperty("revision").GetInt64() == undone);
        Assert.True(doc.GetProperty("canRedo").GetBoolean());
    }

    [Fact]
    public async Task View_ChangesTheFrameSize()
    {
        var (id, _) = await CreateProfile("View preview");
        await using var client = await Connect();
        await client.SendAsync(new { type = "subscribe", profileId = id, scale = 1, maxFps = 30 }, Ct);
        var (first, _) = await client.ReceiveFrameAsync(Ct);
        Assert.Equal((480, 320), (first.PixelWidth, first.PixelHeight));

        await client.SendAsync(new { type = "view", scale = 0.5, maxFps = 10 }, Ct);
        var (half, png) = await client.ReceiveFrameAsync(Ct, h => h.Scale == 0.5f);
        Assert.Equal((240, 160), (half.PixelWidth, half.PixelHeight));
        using var bitmap = SKBitmap.Decode(png.ToArray());
        Assert.Equal(240, bitmap.Width);

        await client.SendAsync(new { type = "view", scale = 2 }, Ct);
        var (twice, _) = await client.ReceiveFrameAsync(Ct, h => h.Scale == 2);
        Assert.Equal((960, 640), (twice.PixelWidth, twice.PixelHeight));
    }

    [Fact]
    public async Task CreatingAProfile_SendsAProfilesEvent()
    {
        await using var client = await Connect();
        using var api = factory.CreateApiClient();
        var before = factory.Services.GetRequiredService<InfoPanel.Editing.Workspace>().Version;

        using var created = await api.PostAsJsonAsync("/api/profiles", new { name = "Profiles event", width = 100, height = 100 }, Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var message = await client.ReceiveTextAsync("profiles", Ct, m => m.GetProperty("version").GetInt64() > before);
        Assert.True(message.GetProperty("version").GetInt64() > before);
    }

    [Theory]
    [InlineData("""{"type":"subscribe","profileId":"6f1b8a3e-0000-4000-8000-000000000000"}""", "No profile")]
    [InlineData("""{"type":"subscribe","profileId":"nope"}""", "profileId")]
    [InlineData("""{"type":"subscribe"}""", "profileId")]
    [InlineData("""not json""", "JSON")]
    [InlineData("""[1]""", "JSON")]
    [InlineData("""{"type":7}""", "JSON")]
    [InlineData("""{"type":"dance"}""", "Unknown message type 'dance'")]
    [InlineData("""{"type":"view","scale":2}""", "subscribe first")]
    public async Task BadMessages_GetAnError_AndTheConnectionStaysUsable(string text, string expected)
    {
        await using var client = await Connect();
        await client.SendTextAsync(text, Ct);

        var error = await client.ReceiveTextAsync("error", Ct);
        Assert.Contains(expected, error.GetProperty("message").GetString());

        await client.SendAsync(new { type = "subscribe", profileId = await ExampleProfileId(), scale = 0.25 }, Ct);
        await client.ReceiveFrameAsync(Ct);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData("2")]
    public async Task BadScale_IsAnError(object scale)
    {
        await using var client = await Connect();
        await client.SendAsync(new { type = "subscribe", profileId = await ExampleProfileId(), scale }, Ct);
        var error = await client.ReceiveTextAsync("error", Ct);
        Assert.Contains("scale", error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task BinaryMessages_GetAnError()
    {
        await using var client = await Connect();
        await client.Socket.SendAsync(new byte[] { 1, 2, 3 }, WebSocketMessageType.Binary, true, Ct);
        var error = await client.ReceiveTextAsync("error", Ct);
        Assert.Contains("binary", error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task AnOversizedMessage_ClosesTheConnection()
    {
        await using var client = await Connect();
        var text = "{\"type\":\"subscribe\",\"pad\":\"" + new string('x', 5000) + "\"}";
        await client.SendTextAsync(text, Ct);

        while (true)
        {
            var message = await client.ReceiveAsync(Ct);
            if (message.CloseStatus is { } status)
            {
                Assert.Equal(WebSocketCloseStatus.MessageTooBig, status);
                break;
            }
        }
    }

    [Fact]
    public async Task Unsubscribing_OrClosing_DetachesTheSink_SoTheLoopStops()
    {
        var (id, _) = await CreateProfile("Detach preview");

        var client = await Connect();
        await client.SendAsync(new { type = "subscribe", profileId = id }, Ct);
        await client.ReceiveFrameAsync(Ct);
        Assert.True(Render.IsRendering(id));

        await client.SendAsync(new { type = "unsubscribe" }, Ct);
        await WaitUntil(() => !Render.IsRendering(id));

        await client.SendAsync(new { type = "subscribe", profileId = id }, Ct);
        await client.ReceiveFrameAsync(Ct);
        Assert.True(Render.IsRendering(id));

        await client.DisposeAsync(); // a clean close
        await WaitUntil(() => !Render.IsRendering(id));

        // An aborted connection detaches too.
        var aborted = await Connect();
        await aborted.SendAsync(new { type = "subscribe", profileId = id }, Ct);
        await aborted.ReceiveFrameAsync(Ct);
        aborted.Socket.Abort();
        await WaitUntil(() => !Render.IsRendering(id));
    }

    [Fact]
    public async Task DeletingTheSubscribedProfile_IsAnError_AndStopsItsFrames()
    {
        var (id, _) = await CreateProfile("Delete preview");
        await using var client = await Connect();
        await client.SendAsync(new { type = "subscribe", profileId = id }, Ct);
        await client.ReceiveFrameAsync(Ct);

        using var api = factory.CreateApiClient();
        using var deleted = await api.DeleteAsync($"/api/profiles/{id}", Ct);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        var error = await client.ReceiveTextAsync("error", Ct);
        Assert.Contains("deleted", error.GetProperty("message").GetString());
        Assert.False(Render.IsRendering(id));
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (!condition())
            await Task.Delay(10, timeout.Token);
    }
}
