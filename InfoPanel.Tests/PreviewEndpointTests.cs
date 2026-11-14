using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using InfoPanel.Api;
using InfoPanel.Documents;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;
using Xunit;
using static InfoPanel.Tests.TestDocs;

namespace InfoPanel.Tests;

/// <summary>
/// <c>POST /api/preview</c> (spec/docs/api.md, "Previews"): a document the request gives, drawn with the readings it
/// gives, as an animated WebP loop (each state's first frame whole and not blended) or a PNG still (the loop's first
/// frame), every frame opaque on the request's background when it names one; bounded (width, the height it gives,
/// states, frames, the answer's bytes with the quality ladder, one recording at a time, the time it takes), checked as a
/// create checks a document, the <c>edit</c> scope's; stores, lists and announces nothing.
/// </summary>
public sealed class PreviewEndpointTests(EngineFactory factory) : IClassFixture<EngineFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Key = "test:v";

    /// <summary>A bar as wide as the reading <c>v</c> and 10 high, white, at the top left of a 120 × 20 canvas whose background is <paramref name="background"/>.</summary>
    private static JsonObject Bar(string background = "#00000000", int width = 120, int height = 20)
    {
        var scene = JsonNode.Parse(
            """{"format": "infopanel.scene/1", "inputs": {"v": {"type": "number"}}, "nodes": [{"kind": "rect", "w": {"op": "input", "name": "v"}, "h": 10, "fill": "#ffffff"}]}""")!.AsObject();
        var document = Doc("Bar", width, height, Scene("bar", scene: scene, extra: new JsonObject { ["bindings"] = new JsonObject { ["v"] = new JsonObject { ["key"] = Key } } }));
        document["profile"]!["background"] = background;
        return document;
    }

    private static JsonObject State(int frames, double? value) => new()
    {
        ["frames"] = frames,
        ["readings"] = value is { } v ? new JsonObject { [Key] = new JsonObject { ["value"] = v } } : new JsonObject(),
    };

    private static JsonObject Request(JsonObject document, string answer, int width = 240, string? background = "#203040",
        long maxBytes = 1024 * 1024, params JsonObject[] states)
    {
        var request = new JsonObject
        {
            ["document"] = document.DeepClone(),
            ["width"] = width,
            ["answer"] = answer,
            ["states"] = new JsonArray([.. (states.Length > 0 ? states : [State(3, 30), State(2, 60), State(2, null)]).Select(s => s.DeepClone())]),
            ["maxBytes"] = maxBytes,
        };
        if (background != null)
            request["background"] = background;
        return request;
    }

    private static async Task<HttpResponseMessage> Post(HttpClient api, JsonObject request) =>
        await api.PostAsync("/api/preview", Json(request), Ct);

    private static async Task<JsonElement> Loop(HttpClient api, JsonObject request)
    {
        using var response = await Post(api, request);
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode} {text}");
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<byte[]> Still(HttpClient api, JsonObject request)
    {
        using var response = await Post(api, request);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync(Ct)}");
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        return await response.Content.ReadAsByteArrayAsync(Ct);
    }

    /// <summary>A 422's errors, as (path, code, message).</summary>
    private static async Task<List<(string Path, string Code, string Message)>> Refused(HttpClient api, JsonObject request)
    {
        using var response = await Post(api, request);
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, $"{(int)response.StatusCode} {text}");
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        return [.. JsonDocument.Parse(text).RootElement.GetProperty("errors").EnumerateArray()
            .Select(e => (e.GetProperty("path").GetString()!, e.GetProperty("code").GetString()!, e.GetProperty("message").GetString()!))];
    }

    /// <summary>The WebP's ANMF chunks' payload offsets, in order.</summary>
    private static List<int> Frames(byte[] webp)
    {
        var anmf = new List<int>();
        for (var at = 12; at + 8 <= webp.Length;)
        {
            var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(webp.AsSpan(at + 4));
            if (Encoding.ASCII.GetString(webp, at, 4) == "ANMF") anmf.Add(at + 8);
            at += 8 + size + (size & 1);
        }
        return anmf;
    }

    private static SKBitmap Decode(byte[] bytes, int frame = 0)
    {
        using var codec = SKCodec.Create(new SKMemoryStream(bytes));
        Assert.NotNull(codec);
        var bitmap = new SKBitmap(new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        Assert.Equal(SKCodecResult.Success, codec.GetPixels(bitmap.Info, bitmap.GetPixels(), new SKCodecOptions(frame)));
        return bitmap;
    }

    /// <summary>How many pixels of the row at <paramref name="y"/> are white.</summary>
    private static int Lit(SKBitmap bitmap, int y = 5) => Enumerable.Range(0, bitmap.Width).Count(x => bitmap.GetPixel(x, y) == SKColors.White);

    [Fact]
    public async Task ALoop_IsAnAnimatedWebp_WithEachStatesFirstFrameWhole_OpaqueOnTheBackground()
    {
        using var api = factory.CreateApiClient();
        var loop = await Loop(api, Request(Bar(), "loop"));
        Assert.Equal("lossless", loop.GetProperty("quality").GetString());
        Assert.Equal(240, loop.GetProperty("width").GetInt32());
        Assert.Equal(40, loop.GetProperty("height").GetInt32());
        var webp = Convert.FromBase64String(loop.GetProperty("webp").GetString()!);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(webp, 0, 4));
        Assert.Equal("WEBP", Encoding.ASCII.GetString(webp, 8, 4));
        var keyFrames = loop.GetProperty("keyFrames").EnumerateArray().Select(k => k.GetInt32()).ToArray();
        Assert.Equal(3, keyFrames.Length);
        Assert.Equal(0, keyFrames[0]);

        var anmf = Frames(webp);
        int[] lit = [60, 120, 0];
        for (var s = 0; s < keyFrames.Length; s++)
        {
            var p = anmf[keyFrames[s]];
            Assert.True(webp.AsSpan(p, 6).ToArray().All(b => b == 0), $"state {s}'s first frame at 0,0");
            Assert.Equal(239, webp[p + 6] | webp[p + 7] << 8 | webp[p + 8] << 16);
            Assert.Equal(39, webp[p + 9] | webp[p + 10] << 8 | webp[p + 11] << 16);
            Assert.True((webp[p + 15] & 0x02) != 0, $"state {s}'s first frame isn't blended");
            using var frame = Decode(webp, keyFrames[s]);
            Assert.Equal(lit[s], Lit(frame));
            // Opaque on the request's background where the document's own (transparent) shows.
            Assert.Equal(new SKColor(0x20, 0x30, 0x40), frame.GetPixel(239, 30));
        }
    }

    [Fact]
    public async Task AStill_IsAPngOfTheLoopsFirstFrame()
    {
        using var api = factory.CreateApiClient();
        var loop = await Loop(api, Request(Bar(), "loop"));
        using var first = Decode(Convert.FromBase64String(loop.GetProperty("webp").GetString()!));
        var png = await Still(api, Request(Bar(), "still"));
        Assert.Equal("\x89PNG", Encoding.Latin1.GetString(png, 0, 4));
        using var still = Decode(png);
        Assert.Equal(new SKSizeI(240, 40), new SKSizeI(still.Width, still.Height));
        Assert.True(first.GetPixelSpan().SequenceEqual(still.GetPixelSpan()), "the still is the loop's first frame");
        Assert.Equal(60, Lit(still));
    }

    [Fact]
    public async Task WithoutABackground_TheDocumentsOwnBackgroundIsKept()
    {
        using var api = factory.CreateApiClient();
        using (var transparent = Decode(await Still(api, Request(Bar(), "still", background: null))))
            Assert.Equal(0, transparent.GetPixel(239, 30).Alpha);
        using var own = Decode(await Still(api, Request(Bar("#102030"), "still", background: null)));
        Assert.Equal(new SKColor(0x10, 0x20, 0x30), own.GetPixel(239, 30));
    }

    [Fact]
    public async Task TheWidthStatesAndFrames_AreBounded()
    {
        using var api = factory.CreateApiClient();
        Assert.Contains(await Refused(api, Request(Bar(), "loop", width: 1281)), e => e.Path == "/width" && e.Code == "schema");
        Assert.Contains(await Refused(api, Request(Bar(), "loop", width: 15)), e => e.Path == "/width" && e.Code == "schema");
        // 1,280 wide is allowed; its height, by the canvas, is held to 1,280 too.
        await Still(api, Request(Bar(), "still", width: 1280));
        Assert.Equal([("/width", "preview.height")], (await Refused(api, Request(Bar(width: 100, height: 1000), "still", width: 200))).Select(e => (e.Path, e.Code)));
        var six = Enumerable.Range(0, 6).Select(_ => State(1, 10)).ToArray();
        Assert.Contains(await Refused(api, Request(Bar(), "loop", states: six)), e => e.Path == "/states" && e.Code == "schema");
        Assert.Contains(await Refused(api, Request(Bar(), "loop", states: [State(51, 10)])), e => e.Path == "/states/0/frames" && e.Code == "schema");
        Assert.Equal([("/states", "preview.frames")], (await Refused(api, Request(Bar(), "loop", states: [State(30, 10), State(21, 20)]))).Select(e => (e.Path, e.Code)));
        // 50 frames, 5 states: the most a loop holds.
        await Loop(api, Request(Bar(), "loop", states: [State(10, 10), State(10, 20), State(10, 30), State(10, 40), State(10, 50)]));
    }

    [Fact]
    public async Task ADocumentIsCheckedAsACreateChecksIt()
    {
        using var api = factory.CreateApiClient();
        var document = Bar();
        document.Remove("profile");
        var errors = await Refused(api, Request(document, "loop"));
        Assert.NotEmpty(errors);
        Assert.All(errors, e => Assert.StartsWith("/document", e.Path, StringComparison.Ordinal));
        // Its own id may be left out, as a create's.
        var withoutId = Bar();
        withoutId.Remove("id");
        await Still(api, Request(withoutId, "still"));
        Assert.Contains(await Refused(api, Request(Bar(), "film")), e => e.Path == "/answer" && e.Code == "schema");
    }

    [Fact]
    public async Task TheLoop_TakesTheBestQualityThatFits_ElseNamesTheSizeReached()
    {
        using var api = factory.CreateApiClient();
        // A 1,280-wide bar that changes every frame: no two frames alike.
        var states = Enumerable.Range(0, 5).Select(s => new JsonObject
        {
            ["frames"] = 10,
            ["readings"] = new JsonObject { [Key] = new JsonObject { ["value"] = 10 + s * 20 } },
        }).ToArray();
        var gradient = Bar("#00000000");
        var lossless = await Loop(api, Request(gradient, "loop", width: 1280, maxBytes: 8 * 1024 * 1024, states: states));
        Assert.Equal("lossless", lossless.GetProperty("quality").GetString());
        var size = Convert.FromBase64String(lossless.GetProperty("webp").GetString()!).Length;

        var exact = await Loop(api, Request(gradient, "loop", width: 1280, maxBytes: size, states: states));
        Assert.Equal("lossless", exact.GetProperty("quality").GetString());

        var smaller = await Loop(api, Request(gradient, "loop", width: 1280, maxBytes: size - 1, states: states));
        Assert.NotEqual("lossless", smaller.GetProperty("quality").GetString());
        Assert.True(Convert.FromBase64String(smaller.GetProperty("webp").GetString()!).Length <= size - 1);

        var errors = await Refused(api, Request(gradient, "loop", width: 1280, maxBytes: 100, states: states));
        var (path, code, message) = Assert.Single(errors);
        Assert.Equal(("/maxBytes", "preview.size"), (path, code));
        Assert.Matches(@"\d+ bytes", message);
    }

    [Fact]
    public async Task OneRecordingRunsAtATime_ASecondIsRefused()
    {
        using var api = factory.CreateApiClient();
        var gate = factory.Services.GetRequiredService<PreviewGate>();
        Assert.True(gate.TryEnter());
        try
        {
            using var response = await Post(api, Request(Bar(), "still"));
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        }
        finally
        {
            gate.Exit();
        }
        await Still(api, Request(Bar(), "still"));
    }

    [Fact]
    public async Task ItNeedsTheEditScope()
    {
        using var session = factory.CreateApiClient();
        using var anonymous = factory.CreateRawClient();
        using (var response = await Post(anonymous, Request(Bar(), "still")))
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var read = await session.PostAsync("/api/tokens", JsonContent.Create(new { name = "preview-read", scopes = new[] { "read" } }), Ct);
        var token = JsonDocument.Parse(await read.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("token").GetString();
        using var reader = factory.CreateRawClient();
        reader.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using (var response = await Post(reader, Request(Bar(), "still")))
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ARecordingOverItsTime_IsStoppedAndRefused()
    {
        var data = Path.Combine(factory.Root, "preview-time");
        using var slow = factory.WithWebHostBuilder(b => b.UseSetting("Engine:DataDir", data)
            .ConfigureTestServices(s => s.AddSingleton(new PreviewLimits { Time = TimeSpan.Zero })));
        using var api = slow.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = false });
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", EngineFactory.Token);
        Assert.Equal([("", "preview.time")], (await Refused(api, Request(Bar(), "loop"))).Select(e => (e.Path, e.Code)));
        // The gate is let go: the next one runs (and is refused for its time again, not as busy).
        Assert.Equal([("", "preview.time")], (await Refused(api, Request(Bar(), "still"))).Select(e => (e.Path, e.Code)));
    }

    [Fact]
    public async Task ARecording_StoresListsAndAnnouncesNothing()
    {
        using var api = factory.CreateApiClient();
        var workspace = factory.Services.GetRequiredService<Workspace>();
        var changes = 0;
        void Changed(object? sender, ProfilesChangedEventArgs e) => Interlocked.Increment(ref changes);
        var listed = await api.GetStringAsync("/api/profiles", Ct);
        var files = DataFolder();
        workspace.ProfilesChanged += Changed;
        try
        {
            await Loop(api, Request(Bar(), "loop"));
            await Still(api, Request(Bar(), "still"));
        }
        finally
        {
            workspace.ProfilesChanged -= Changed;
        }
        Assert.Equal(0, changes);
        Assert.Equal(listed, await api.GetStringAsync("/api/profiles", Ct));
        Assert.Equal(files, DataFolder());
    }

    /// <summary>A PNG of <paramref name="size"/> square pixels, all <paramref name="colour"/>.</summary>
    private static byte[] Png(SKColor colour, int size)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul));
        bitmap.Erase(colour);
        using var image = SKImage.FromBitmap(bitmap);
        return image.Encode(SKEncodedImageFormat.Png, 100).ToArray();
    }

    private static async Task<string> Upload(HttpClient api, byte[] bytes)
    {
        using var upload = await api.PostAsync("/api/blobs", new ByteArrayContent(bytes), Ct);
        Assert.True(upload.IsSuccessStatusCode, upload.StatusCode.ToString());
        return JsonDocument.Parse(await upload.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("id").GetString()!;
    }

    /// <summary>A canvas <paramref name="width"/> × 10 with each asset drawn at its own place, 10 pixels apart.</summary>
    private static JsonObject Pictures(int width, IReadOnlyList<string> assets)
    {
        var scene = new JsonObject
        {
            ["format"] = "infopanel.scene/1",
            ["nodes"] = new JsonArray([.. assets.Select((a, i) => (JsonNode)new JsonObject
            {
                ["kind"] = "media", ["x"] = i * 10, ["y"] = 0, ["source"] = new JsonObject { ["asset"] = a },
            })]),
        };
        return Doc("Pictures", width, 10, Scene("p", scene: scene));
    }

    /// <summary>The shared media cache's assets and bytes: what the running outputs' pictures hold.</summary>
    private (int Assets, long Bytes) Shared()
    {
        var media = factory.Services.GetRequiredService<InfoPanel.Images.MediaSampleCache>();
        var assets = (int)typeof(InfoPanel.Images.MediaSampleCache)
            .GetProperty("AssetCount", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(media)!;
        return (assets, media.Bytes);
    }

    [Fact]
    public async Task APictureStoredJustBefore_IsDrawn_NotItsPlaceholder()
    {
        using var api = factory.CreateApiClient();
        var colour = new SKColor(255, 64, 0);
        var asset = await Upload(api, Png(colour, 10));
        var shared = Shared();
        using var still = Decode(await Still(api, Request(Pictures(40, [asset]), "still", width: 40)));
        Assert.Equal(colour, still.GetPixel(5, 5));
        Assert.Equal(shared, Shared());
    }

    [Fact]
    public async Task ManyColdPictures_LeaveTheSharedCacheAlone_AndPastTheRecordingsBound_DrawAsMissing()
    {
        using var api = factory.CreateApiClient();
        var count = InfoPanel.Output.LoopMedia.MaxPictures + 8;
        var colours = Enumerable.Range(0, count).Select(i => new SKColor((byte)(i + 1), 200, 100)).ToList();
        var assets = new List<string>();
        foreach (var colour in colours)
            assets.Add(await Upload(api, Png(colour, 10)));
        var shared = Shared();
        using var still = Decode(await Still(api, Request(Pictures(count * 10, assets), "still", width: count * 10)));
        Assert.Equal(shared, Shared());
        for (var i = 0; i < count; i++)
        {
            if (i < InfoPanel.Output.LoopMedia.MaxPictures)
                Assert.Equal(colours[i], still.GetPixel(i * 10 + 5, 5));
            else
                Assert.NotEqual(colours[i], still.GetPixel(i * 10 + 5, 5));
        }
    }

    [Fact]
    public async Task ARecordingPastItsTimeMidWork_IsRefused_AndHoldsTheGateUntilItsThreadHasEnded()
    {
        using var entered = new SemaphoreSlim(0);
        using var release = new SemaphoreSlim(0);
        var held = 0;
        void Drawn(CancellationToken cancel)
        {
            // The first two recordings outlive their time here, as a long encoding would, until the test lets them go.
            if (Interlocked.Increment(ref held) > 2)
                return;
            cancel.WaitHandle.WaitOne(TimeSpan.FromSeconds(30));
            entered.Release();
            release.Wait(TimeSpan.FromSeconds(30));
        }
        var data = Path.Combine(factory.Root, "preview-mid");
        using var slow = factory.WithWebHostBuilder(b => b.UseSetting("Engine:DataDir", data)
            .ConfigureTestServices(s => s.AddSingleton(new PreviewLimits { Time = TimeSpan.FromSeconds(3), Drawn = Drawn })));
        using var api = slow.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = false });
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", EngineFactory.Token);
        foreach (var answer in new[] { "loop", "still" })
        {
            var first = Refused(api, Request(Bar(), answer));
            Assert.True(await entered.WaitAsync(TimeSpan.FromSeconds(30), Ct), "the recording reached its time");
            // Past its time but its thread still runs: the gate is held.
            using (var second = await Post(api, Request(Bar(), "still")))
                Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
            release.Release();
            Assert.Equal([("", "preview.time")], (await first).Select(e => (e.Path, e.Code)));
        }
        await Still(api, Request(Bar(), "still"));
    }

    [Fact]
    public async Task EveryIntegerSpellingTheFormatTakes_IsRead_ANonWholeOneIsRefused()
    {
        using var api = factory.CreateApiClient();
        var document = Bar();
        document["profile"]!["width"] = JsonNode.Parse("1.2e2");
        document["profile"]!["height"] = JsonNode.Parse("20.0");
        var request = Request(document, "loop", states: [State(1, 30), State(2, 60)]);
        request["width"] = JsonNode.Parse("240.0");
        request["maxBytes"] = JsonNode.Parse("1048576.0");
        request["states"]![0]!["frames"] = JsonNode.Parse("1e0");
        var loop = await Loop(api, request);
        Assert.Equal(240, loop.GetProperty("width").GetInt32());
        Assert.Equal(40, loop.GetProperty("height").GetInt32());
        Assert.Equal(2, loop.GetProperty("keyFrames").GetArrayLength());
        request["answer"] = "still";
        Assert.Equal(60, Lit(Decode(await Still(api, request))));

        foreach (var (path, set) in new (string, Action<JsonObject>)[]
        {
            ("/width", r => r["width"] = JsonNode.Parse("240.5")),
            ("/maxBytes", r => r["maxBytes"] = JsonNode.Parse("1048576.5")),
            ("/states/0/frames", r => r["states"]![0]!["frames"] = JsonNode.Parse("1.5")),
        })
        {
            var wrong = (JsonObject)request.DeepClone();
            set(wrong);
            Assert.Contains(await Refused(api, wrong), e => e.Path == path);
        }
        var fractional = (JsonObject)request.DeepClone();
        fractional["document"]!["profile"]!["width"] = JsonNode.Parse("120.5");
        Assert.Contains(await Refused(api, fractional), e => e.Path.StartsWith("/document/profile/width", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ADocumentLoadWarning_WritesNothingOfItToTheLog()
    {
        using var api = factory.CreateApiClient();
        const string Locale = "zq";
        var document = Bar();
        document["profile"]!["locale"] = Locale;
        await Still(api, Request(document, "still"));
        await Loop(api, Request(document, "loop"));
        Assert.DoesNotContain(factory.Services.GetRequiredService<InfoPanel.Hosting.RecentLog>().After(0),
            e => e.Message.Contains($"'{Locale}'", StringComparison.Ordinal));
        var logs = Path.Combine(factory.DataDir, "logs");
        var written = Directory.Exists(logs)
            ? Directory.EnumerateFiles(logs, "*", SearchOption.AllDirectories).Select(f =>
            {
                using var reader = new StreamReader(new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
                return reader.ReadToEnd();
            }).ToList()
            : [];
        Assert.DoesNotContain(written, text => text.Contains($"'{Locale}'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheDocumentsDepth_IsCountedFromItsOwnRoot_AsACreateCountsIt()
    {
        using var api = factory.CreateApiClient();
        var depth = factory.Services.GetRequiredService<Workspace>().Limits.JsonDepth;
        // The document is 1 deep, its profile 2, the editor's value 3: n objects inside it make the document 3 + n deep.
        string Body(JsonObject envelope, int deep)
        {
            var document = Bar();
            document["profile"]!["editor"] = "EDITOR";
            envelope["document"] = document;
            var n = deep - 3;
            var editor = string.Concat(Enumerable.Repeat("{\"a\":", n)) + "{}" + new string('}', n);
            return envelope.ToJsonString().Replace("\"EDITOR\"", editor, StringComparison.Ordinal);
        }
        async Task<HttpResponseMessage> Send(string path, string body) =>
            await api.PostAsync(path, new StringContent(body, Encoding.UTF8, "application/json"), Ct);
        JsonObject Preview() => new() { ["width"] = 240, ["answer"] = "still", ["states"] = new JsonArray(State(1, 30)), ["maxBytes"] = 1024 * 1024 };

        using (var created = await Send("/api/profiles", Body(new JsonObject { ["id"] = NewId() }, depth)))
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using (var drawn = await Send("/api/preview", Body(Preview(), depth)))
            Assert.True(drawn.StatusCode == HttpStatusCode.OK, $"{(int)drawn.StatusCode} {await drawn.Content.ReadAsStringAsync(Ct)}");

        foreach (var (path, envelope) in new[] { ("/api/profiles", new JsonObject { ["id"] = NewId() }), ("/api/preview", Preview()) })
        {
            using var refused = await Send(path, Body(envelope, depth + 1));
            var text = await refused.Content.ReadAsStringAsync(Ct);
            Assert.True(refused.StatusCode == HttpStatusCode.UnprocessableEntity, $"{path}: {(int)refused.StatusCode} {text}");
            var error = Assert.Single(JsonDocument.Parse(text).RootElement.GetProperty("errors").EnumerateArray());
            Assert.Equal(("/document", "limit.jsonDepth"), (error.GetProperty("path").GetString(), error.GetProperty("code").GetString()));
        }
    }

    /// <summary>Every file in the data folder but the log's, with its size and time written.</summary>
    private List<string> DataFolder() =>
        [.. Directory.EnumerateFiles(factory.DataDir, "*", SearchOption.AllDirectories)
            .Where(f => !Path.GetRelativePath(factory.DataDir, f).StartsWith("logs", StringComparison.Ordinal))
            .Select(f => new FileInfo(f))
            .Select(f => $"{Path.GetRelativePath(factory.DataDir, f.FullName)} {f.Length} {f.LastWriteTimeUtc:O}")
            .Order(StringComparer.Ordinal)];
}
