using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using InfoPanel.Core.Tests.Images;
using InfoPanel.Spec;
using SkiaSharp;
using Xunit;

namespace InfoPanel.Tests.Preview;

/// <summary>
/// Web images end to end on a test engine (spec/docs/providers.md): the frame header's <c>itemBounds[].media</c>, a
/// frame sent when only an item's media status changed, the <c>media.localNetwork</c> setting, and an engine started
/// offline (every test engine) refusing everything past this computer. The images come from a server the test starts
/// on 127.0.0.1.
/// </summary>
public sealed class WebImageTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static byte[] Png(SKColor color)
    {
        using var bitmap = new SKBitmap(8, 6);
        bitmap.Erase(color);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static JsonObject WebImage(string address, int refresh = 0) => new()
    {
        ["format"] = "infopanel.scene/1",
        ["requires"] = new JsonArray("media.provider"),
        ["nodes"] = new JsonArray(new JsonObject
        {
            ["kind"] = "media",
            ["w"] = 80,
            ["h"] = 60,
            ["source"] = new JsonObject { ["provider"] = "http", ["ref"] = address, ["refresh"] = refresh },
        }),
    };

    private static async Task<(PreviewClient Client, string Id)> Show(EngineFactory factory, HttpClient api, string address, int refresh = 0)
    {
        var id = await TestDocs.CreateAsync(api, TestDocs.Doc("Web image", 120, 100, TestDocs.Scene("radar", 10, 10, WebImage(address, refresh))), Ct);
        var client = await PreviewClient.ConnectAsync(factory, Ct);
        await client.SendAsync(new { type = "subscribe", profileId = id, scale = 1, maxFps = 30 }, Ct);
        return (client, id);
    }

    private static ItemMedia? MediaOf(FrameHeader header) => header.ItemBounds.Single(b => b.Id == "radar").Media;

    [Fact]
    public async Task A_404_is_the_items_media_and_its_frame_is_sent_though_only_the_status_changed()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new LocalHttpServer(async (r, s, t) =>
        {
            await gate.Task.WaitAsync(t);
            await LocalHttpServer.Answer(s, 404, contentType: null);
        });
        using var factory = new EngineFactory();
        using var api = factory.CreateApiClient();
        var (client, _) = await Show(factory, api, server.Url("/nothing.png"));
        await using (client)
        {
            var (loading, loadingPng) = await client.ReceiveFrameAsync(Ct, h => MediaOf(h)?.Code == "media.loading");
            gate.SetResult();
            var (failed, failedPng) = await client.ReceiveFrameAsync(Ct, h => MediaOf(h)?.Code == "media.provider", TimeSpan.FromSeconds(10));
            Assert.Equal(new ItemMedia { Code = "media.provider", Message = "The server answered 404." }, MediaOf(failed));
            Assert.Null(failed.ItemBounds.Single().Problem);
            // The missing-media placeholder both times: the same pixels, so only the status made this frame.
            Assert.Equal(loading.Rev, failed.Rev);
            using var a = SKBitmap.Decode(loadingPng.ToArray());
            using var b = SKBitmap.Decode(failedPng.ToArray());
            Assert.Equal(a.Bytes, b.Bytes);
        }
    }

    [Fact]
    public async Task An_image_shows_and_turning_the_local_network_off_blocks_it_until_it_is_on_again()
    {
        await using var server = new LocalHttpServer((r, s, t) => LocalHttpServer.Answer(s, 200, Png(SKColors.Lime)));
        using var factory = new EngineFactory();
        using var api = factory.CreateApiClient();
        var (client, _) = await Show(factory, api, server.Url("/radar.png"));
        await using (client)
        {
            var (shown, png) = await client.ReceiveFrameAsync(Ct, h => MediaOf(h) is null, TimeSpan.FromSeconds(10));
            using (var bitmap = SKBitmap.Decode(png.ToArray()))
                Assert.Equal(SKColors.Lime, bitmap.GetPixel(50, 40));

            using (var off = await api.PatchAsync("/api/settings", TestDocs.Json(JsonNode.Parse("""{"media":{"localNetwork":false}}""")!), Ct))
            {
                Assert.Equal(HttpStatusCode.OK, off.StatusCode);
                var body = JsonDocument.Parse(await off.Content.ReadAsStringAsync(Ct)).RootElement;
                Assert.False(body.GetProperty("media").GetProperty("localNetwork").GetBoolean());
            }
            var (blocked, _) = await client.ReceiveFrameAsync(Ct, h => MediaOf(h)?.Code == "media.address", TimeSpan.FromSeconds(10));
            Assert.Equal("127.0.0.1 is on this computer.", MediaOf(blocked)!.Message);

            using (var on = await api.PatchAsync("/api/settings", TestDocs.Json(JsonNode.Parse("""{"media":{"localNetwork":null}}""")!), Ct))
                Assert.Equal(HttpStatusCode.OK, on.StatusCode);
            var (back, backPng) = await client.ReceiveFrameAsync(Ct, h => MediaOf(h) is null, TimeSpan.FromSeconds(10));
            using (var bitmap = SKBitmap.Decode(backPng.ToArray()))
                Assert.Equal(SKColors.Lime, bitmap.GetPixel(50, 40));
        }
        Assert.Equal(2, server.Requests.Count);
    }

    [Fact]
    public async Task A_hostile_answers_message_is_cut_to_the_frame_headers_bound()
    {
        // An SVG whose unknown element has a 1,100-character name: the validator's message names it, and the frame header
        // holds a media message of at most 1,024 characters (the client checks every frame against the schema).
        var name = "x" + new string('a', 1099);
        var svg = Encoding.UTF8.GetBytes($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"8\" height=\"6\"><{name}/></svg>");
        await using var server = new LocalHttpServer((r, s, t) => LocalHttpServer.Answer(s, 200, svg, "image/svg+xml"));
        using var factory = new EngineFactory();
        using var api = factory.CreateApiClient();
        var (client, _) = await Show(factory, api, server.Url("/hostile.svg"));
        await using (client)
        {
            var (refused, _) = await client.ReceiveFrameAsync(Ct, h => MediaOf(h) is { Code: not "media.loading" }, TimeSpan.FromSeconds(10));
            var media = MediaOf(refused)!;
            Assert.Equal(1024, media.Message.EnumerateRunes().Count());
            Assert.EndsWith("…", media.Message);
            Assert.Contains("xaaaa", media.Message);
        }
    }

    [Theory]
    [InlineData("http://203.0.113.9/radar.png", "203.0.113.9 is outside this computer, and the engine runs offline.")]
    [InlineData("https://example.com/feed.png", "example.com is outside this computer, and the engine runs offline.")]
    public async Task A_test_engine_runs_offline_and_refuses_every_address_past_this_computer(string address, string why)
    {
        using var factory = new EngineFactory();
        using var api = factory.CreateApiClient();
        var (client, _) = await Show(factory, api, address);
        await using (client)
        {
            var (refused, _) = await client.ReceiveFrameAsync(Ct, h => MediaOf(h)?.Code == "media.address", TimeSpan.FromSeconds(10));
            Assert.Equal(why, MediaOf(refused)!.Message);
        }
    }

    [Theory]
    [InlineData("""{"media":{"localNetwork":"no"}}""", "/media/localNetwork")]
    [InlineData("""{"media":{"localNetwork":0}}""", "/media/localNetwork")]
    [InlineData("""{"media":{"publicNetwork":false}}""", "/media/publicNetwork")]
    [InlineData("""{"media":true}""", "/media")]
    public async Task A_media_setting_that_breaks_its_rule_is_422(string body, string path)
    {
        using var factory = new EngineFactory();
        using var api = factory.CreateApiClient();
        // The setting itself is one the engine takes.
        using (var valid = await api.PatchAsync("/api/settings", new StringContent("""{"media":{"localNetwork":false}}""", Encoding.UTF8, "application/json"), Ct))
            Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
        using var response = await api.PatchAsync("/api/settings", new StringContent(body, Encoding.UTF8, "application/json"), Ct);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement;
        Assert.Contains(problem.GetProperty("errors").EnumerateArray(), e => e.GetProperty("path").GetString() == path && e.GetProperty("code").GetString() == "schema");
    }

    [Fact]
    public async Task The_local_network_setting_is_on_by_default_stored_when_changed_and_read_again()
    {
        using var factory = new EngineFactory();
        using var api = factory.CreateApiClient();
        var settings = JsonDocument.Parse(await api.GetStringAsync("/api/settings", Ct)).RootElement;
        Assert.Equal("""{"localNetwork":true}""", settings.GetProperty("media").GetRawText());
        using (var off = await api.PatchAsync("/api/settings", TestDocs.Json(JsonNode.Parse("""{"media":{"localNetwork":false}}""")!), Ct))
            Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        var stored = JsonNode.Parse(File.ReadAllText(Path.Combine(factory.DataDir, "settings.json")))!;
        Assert.False(stored["media"]!["localNetwork"]!.GetValue<bool>());
        var store = InfoPanel.Settings.SettingsStore.Open(new InfoPanel.Persistence.DataPaths(factory.DataDir));
        Assert.False(store.Current.Settings.Media.LocalNetwork);
    }
}
