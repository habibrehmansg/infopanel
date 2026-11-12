using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using InfoPanel.Hosting;
using InfoPanel.Models;
using InfoPanel.Sensors;
using InfoPanel.Spec;
using InfoPanel.Tests.Mcp;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;
using Xunit;

namespace InfoPanel.Tests;

/// <summary>
/// A source of one image sensor, <see cref="Key"/>: listed typed image, its reading <see cref="Current"/> (the reference of
/// its picture now), and the pictures it holds by reference. It counts its reads (a request for a picture is no read).
/// </summary>
internal sealed class PictureSource(string current, Dictionary<string, byte[]> pictures) : ISensorSource, ISensorStatusSource, ISensorImages
{
    public static readonly SensorKey Key = SensorKey.Plugin("/test.pictures/cover");

    public string Current => current;

    public int Reads;

    public string Id => "test.pictures";

    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    public IReadOnlyList<SensorInfo> GetSensors() => [new SensorInfo(Key, Id, "Pictures", "Cover", "")];

    public bool Handles(SensorKey key) => key.Id.StartsWith("/test.pictures/", StringComparison.Ordinal);

    public bool TryRead(SensorKey key, out SensorReading reading)
    {
        var state = ReadState(key);
        reading = state.Reading;
        return state.HasReading;
    }

    public SensorStatus GetStatus(SensorKey key) => ReadState(key).Status;

    public SensorState ReadState(SensorKey key)
    {
        Interlocked.Increment(ref Reads);
        return key == Key ? new SensorState(SensorStatus.Live, SensorKind.Image, new SensorReading(current)) : SensorState.Without(SensorStatus.Unavailable);
    }

    public SensorState PeekState(SensorKey key) =>
        key == Key ? new SensorState(SensorStatus.Live, SensorKind.Image, new SensorReading(current)) : SensorState.Without(SensorStatus.Unavailable);

    public bool TryGetImage(SensorKey key, string reference, out ReadOnlyMemory<byte> bytes)
    {
        bytes = key == Key && pictures.TryGetValue(reference, out var held) ? held : default;
        return key == Key && pictures.ContainsKey(reference);
    }
}

/// <summary>
/// <c>GET /api/sensors/image?key&amp;ref</c> (spec/docs/api.md, "Sensor pictures"): an image sensor's picture as its source
/// holds it, PNG, JPEG, GIF or WebP within the media limits and 8 MiB, with that format's type and <c>no-store</c>; 404
/// for any miss (no image sensor, no picture under the reference, bytes that aren't one of those pictures or are over a
/// limit), 400 for a key or reference absent, empty, given twice or too long; every token's (<c>read</c>); no read of the
/// sensor, and nothing logged of the reference.
/// </summary>
public sealed class SensorImageEndpointTests(EngineFactory factory) : IClassFixture<EngineFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly byte[] Png = McpEditToolTests.Encoded(SKEncodedImageFormat.Png, 40, 20);
    private static readonly byte[] Jpeg = McpEditToolTests.Encoded(SKEncodedImageFormat.Jpeg, 40, 20);
    private static readonly byte[] WebP = McpEditToolTests.Encoded(SKEncodedImageFormat.Webp, 40, 20);
    private static readonly byte[] Gif = File.ReadAllBytes(Path.Combine(Editing.LiveEngine.RepoRoot(), "InfoPanel.Core.Tests", "Fixtures", "assets", "blink.gif"));

    /// <summary>A PNG of noise, which doesn't compress: over 8 MiB, within the media limits (32 MiB, 8,192 a side).</summary>
    private static byte[] LargePng()
    {
        using var bitmap = new SKBitmap(1700, 1700);
        var pixels = new byte[1700 * 1700 * 4];
        new Random(1).NextBytes(pixels);
        System.Runtime.InteropServices.Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static string QueryPath(string key, string reference) =>
        $"/api/sensors/image?key={Uri.EscapeDataString(key)}&ref={Uri.EscapeDataString(reference)}";

    private static string Of(string reference) => QueryPath(PictureSource.Key.ToString(), reference);

    private (WebApplicationFactory<Program> Engine, PictureSource Source) Engine(Dictionary<string, byte[]> pictures, string current = "png")
    {
        var source = new PictureSource(current, pictures);
        return (FixedSensorSource.Engine(factory, source), source);
    }

    [Fact]
    public async Task An_image_sensor_is_listed_typed_image_and_its_value_is_its_reference()
    {
        var (engine, _) = Engine(new() { ["3fa9c1d2e4b5a6f7"] = Png }, "3fa9c1d2e4b5a6f7");
        using var owned = engine;
        using var client = FixedSensorSource.Client(engine);

        var listed = JsonDocument.Parse(await client.GetStringAsync("/api/sensors", Ct)).RootElement.EnumerateArray()
            .Single(s => s.GetProperty("key").GetString() == PictureSource.Key.ToString());
        Assert.True(ReadEndpointTests.Valid(listed, "sensorEntry"));
        Assert.Equal("image", listed.GetProperty("type").GetString());

        var value = JsonDocument.Parse(await client.GetStringAsync("/api/sensors/values", Ct)).RootElement.EnumerateArray()
            .Single(s => s.GetProperty("key").GetString() == PictureSource.Key.ToString());
        Assert.True(ReadEndpointTests.Valid(value, "sensorValue"));
        Assert.Equal("3fa9c1d2e4b5a6f7", value.GetProperty("text").GetString());
        Assert.Equal(0, value.GetProperty("now").GetDouble());
        Assert.False(value.TryGetProperty("history", out _));
    }

    [Theory]
    [InlineData("png", "image/png")]
    [InlineData("jpeg", "image/jpeg")]
    [InlineData("gif", "image/gif")]
    [InlineData("webp", "image/webp")]
    public async Task The_picture_is_answered_exactly_as_held_with_its_formats_type_and_no_store(string reference, string type)
    {
        var (engine, source) = Engine(new() { ["png"] = Png, ["jpeg"] = Jpeg, ["gif"] = Gif, ["webp"] = WebP });
        using var owned = engine;
        using var client = FixedSensorSource.Client(engine);

        using var response = await client.GetAsync(Of(reference), Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(type, response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var expected = reference switch { "png" => Png, "jpeg" => Jpeg, "gif" => Gif, _ => WebP };
        Assert.Equal(expected, await response.Content.ReadAsByteArrayAsync(Ct));
        // Asking for the picture reads nothing: no reader is started for it.
        Assert.Equal(0, source.Reads);
    }

    [Fact]
    public async Task Every_miss_is_404_no_image_sensor_no_picture_under_the_reference_not_one_of_the_four_formats_or_over_a_limit()
    {
        var svg = "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"10\" height=\"10\"><rect width=\"10\" height=\"10\"/></svg>"u8.ToArray();
        var (engine, _) = Engine(new()
        {
            ["png"] = Png,
            ["words"] = "not a picture"u8.ToArray(),
            ["svg"] = svg,
            ["wide"] = McpEditToolTests.Encoded(SKEncodedImageFormat.Png, SpecLimits.Default.MediaSide + 1, 1),
            ["large"] = LargePng(),
            ["empty"] = [],
        });
        using var owned = engine;
        using var client = FixedSensorSource.Client(engine);

        async Task Missed(string path)
        {
            using var response = await client.GetAsync(path, Ct);
            Assert.True(response.StatusCode == HttpStatusCode.NotFound, $"{path}: {(int)response.StatusCode}");
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            var body = await response.Content.ReadAsStringAsync(Ct);
            Assert.True(ReadEndpointTests.Valid(JsonDocument.Parse(body).RootElement, "problem"));
        }

        // The source's picture is answered; each of these isn't.
        using (var hit = await client.GetAsync(Of("png"), Ct))
            Assert.Equal(HttpStatusCode.OK, hit.StatusCode);
        await Missed(Of("absent"));
        foreach (var reference in new[] { "words", "svg", "wide", "large", "empty" })
            await Missed(Of(reference));
        // Another key of the source, a key no source handles, a source without pictures (the mock's), a key that isn't one.
        await Missed(QueryPath("plugin:/test.pictures/other", "png"));
        await Missed(QueryPath("plugin:/nobody/cover", "png"));
        await Missed(QueryPath("infopanel:/mock/cpu/cpu-load", "png"));
        await Missed(QueryPath("not a key", "png"));
        // The longest key and reference the request takes: a miss, not a bad query.
        await Missed(QueryPath("plugin:/test.pictures/" + new string('k', 512 - "plugin:/test.pictures/".Length), "png"));
        await Missed(Of(new string('r', 2048)));
        // The large picture is a picture within the media limits: over 8 MiB is what refuses it.
        Assert.InRange(LargePng().Length, 8 * 1024 * 1024 + 1, SpecLimits.Default.MediaBytes);
        Assert.Empty(MediaValidator.Check(LargePng()).Errors);
    }

    [Fact]
    public async Task A_key_or_reference_absent_empty_given_twice_or_too_long_is_400()
    {
        var (engine, _) = Engine(new() { ["png"] = Png });
        using var owned = engine;
        using var client = FixedSensorSource.Client(engine);
        var key = Uri.EscapeDataString(PictureSource.Key.ToString());

        foreach (var query in new[]
                 {
                     "", $"?key={key}", "?ref=png", $"?key=&ref=png", $"?key={key}&ref=",
                     $"?key={key}&key={key}&ref=png", $"?key={key}&ref=png&ref=png",
                     $"?key={new string('k', 513)}&ref=png", $"?key={key}&ref={new string('r', 2049)}",
                 })
        {
            using var response = await client.GetAsync("/api/sensors/image" + query, Ct);
            Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{query}: {(int)response.StatusCode}");
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        }
    }

    [Fact]
    public async Task The_key_and_reference_limits_count_code_points_not_UTF16_units()
    {
        var (engine, _) = Engine(new() { ["png"] = Png });
        using var owned = engine;
        using var client = FixedSensorSource.Client(engine);
        const string Prefix = "plugin:/test.pictures/";
        const string Face = "\U0001F600";

        async Task<HttpStatusCode> Status(string key, string reference)
        {
            using var response = await client.GetAsync(QueryPath(key, reference), Ct);
            return response.StatusCode;
        }

        // Supplementary characters are two UTF-16 units but one code point: exactly the limit reaches the miss, one over is a bad query.
        var key = Prefix + string.Concat(Enumerable.Repeat(Face, 512 - Prefix.Length));
        var reference = string.Concat(Enumerable.Repeat(Face, 2048));
        Assert.Equal(HttpStatusCode.NotFound, await Status(key, "png"));
        Assert.Equal(HttpStatusCode.BadRequest, await Status(key + Face, "png"));
        Assert.Equal(HttpStatusCode.NotFound, await Status(PictureSource.Key.ToString(), reference));
        Assert.Equal(HttpStatusCode.BadRequest, await Status(PictureSource.Key.ToString(), reference + Face));
    }

    [Fact]
    public async Task Every_token_with_read_gets_the_picture_and_none_without_a_token()
    {
        var (engine, _) = Engine(new() { ["png"] = Png });
        using var owned = engine;
        using var session = FixedSensorSource.Client(engine);
        using var anonymous = engine.CreateClient();
        foreach (var scopes in new[] { new[] { "read" }, new[] { "edit" } })
        {
            using var created = await session.PostAsync("/api/tokens", JsonContent.Create(new { name = "pictures-" + scopes[0], scopes }), Ct);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var token = JsonDocument.Parse(await created.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("token").GetString();
            using var integration = engine.CreateClient();
            integration.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var answer = await integration.GetAsync(Of("png"), Ct);
            Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
            Assert.Equal(Png, await answer.Content.ReadAsByteArrayAsync(Ct));
        }

        using var refused = await anonymous.GetAsync(Of("png"), Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
    }

    [Fact]
    public async Task Nothing_logs_a_reference()
    {
        const string reference = "a1b2c3d4e5f60718";
        var (engine, _) = Engine(new() { [reference] = Png, ["other0123456789ab"] = "not a picture"u8.ToArray() });
        using var owned = engine;
        using var client = FixedSensorSource.Client(engine);
        using (var answered = await client.GetAsync(Of(reference), Ct))
            Assert.Equal(HttpStatusCode.OK, answered.StatusCode);
        (await client.GetAsync(Of("other0123456789ab"), Ct)).Dispose();
        (await client.GetAsync(Of("missing0123456789"), Ct)).Dispose();

        var logged = string.Join("\n", engine.Services.GetRequiredService<RecentLog>().After(0).Select(e => e.Message + e.Exception));
        foreach (var named in new[] { reference, "other0123456789ab", "missing0123456789" })
            Assert.DoesNotContain(named, logged, StringComparison.OrdinalIgnoreCase);
    }
}
