using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using InfoPanel.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace InfoPanel.Tests;

/// <summary>
/// Opt-in, <c>INFOPANEL_PREVIEW_SIZES=&lt;file&gt;</c>: 5-second loops (four states of 1.25 s) of the reference widgets at
/// 800 pixels wide and of the first-run example documents at 1,280, each drawn through <c>POST /api/preview</c> in this
/// process at every rung of the quality ladder (a cap of 8 MiB, then just under each size answered), with the time each
/// request took, written to the file. A record of sizes, not a check of any cap.
/// </summary>
public sealed class PreviewSizeTests(EngineFactory factory) : IClassFixture<EngineFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly int[] Frames = [13, 12, 13, 12];
    private static readonly double[] Values = [22, 48, 71, 93];

    [Fact]
    public async Task Sizes_AreRecorded()
    {
        var output = Environment.GetEnvironmentVariable("INFOPANEL_PREVIEW_SIZES");
        Assert.SkipWhen(string.IsNullOrEmpty(output), "Records preview sizes: set INFOPANEL_PREVIEW_SIZES=<file> to run it.");

        var root = Editing.LiveEngine.RepoRoot();
        var widgets = Path.Combine(root, "InfoPanel.Core.Tests", "Scenes", "Reference", "widgets");
        var examples = Path.Combine(root, "InfoPanel.Core", "Documents", "Examples");
        (string File, int Width, long Cap)[] documents =
        [
            (Path.Combine(widgets, "peak-bar.document.json"), 800, 384 * 1024),
            (Path.Combine(widgets, "segment-gauge.document.json"), 800, 384 * 1024),
            (Path.Combine(widgets, "sparkline-card.document.json"), 800, 384 * 1024),
            (Path.Combine(examples, "macos.document.json"), 1280, 1024 * 1024),
            (Path.Combine(examples, "wide-bar.document.json"), 1280, 1024 * 1024),
        ];

        using var host = factory.WithWebHostBuilder(b => b.UseSetting("Engine:DataDir", Path.Combine(factory.Root, "preview-sizes"))
            .ConfigureTestServices(s => s.AddSingleton(new PreviewLimits { Time = TimeSpan.FromMinutes(10) })));
        using var api = host.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = false });
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", EngineFactory.Token);
        // The examples' blobs, as a first run stores them, so their pictures draw.
        foreach (var blob in Directory.EnumerateFiles(Path.Combine(examples, "blobs")))
            (await api.PostAsync("/api/blobs", new ByteArrayContent(await File.ReadAllBytesAsync(blob, Ct)), Ct)).EnsureSuccessStatusCode();

        var lines = new List<string>
        {
            $"# Preview sizes through POST /api/preview (in process), {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC, {Environment.OSVersion}, {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}",
            "# 5 s loops at 10 fps: four states of 13, 12, 13 and 12 frames; every bound key reads 22, 48, 71, then 93 (a number, unit %); no history; no background colour.",
            "# Each size is the WebP's bytes at that quality: lossless from a cap of 8 MiB, lossy 85 from a cap of lossless - 1, lossy 70 from a cap of lossy 85 - 1",
            "# (or the size the refusal names). 'n/a' means the rung before it was smaller already. ms: the lossless request's time, drawing and encoding.",
            "",
            "document | canvas | picture | lossless B | lossy 85 B | lossy 70 B | proposed cap B | best fitting the cap | ms",
        };
        foreach (var (file, width, cap) in documents)
        {
            var document = JsonNode.Parse(await File.ReadAllTextAsync(file, Ct))!.AsObject();
            var keys = Keys(document).Distinct(StringComparer.Ordinal).ToList();
            var states = new JsonArray();
            for (var s = 0; s < Frames.Length; s++)
            {
                var readings = new JsonObject();
                foreach (var key in keys)
                    readings[key] = new JsonObject { ["value"] = Values[s], ["unit"] = "%" };
                states.Add(new JsonObject { ["frames"] = Frames[s], ["readings"] = readings });
            }
            JsonObject Request(long maxBytes) => new()
            {
                ["document"] = document.DeepClone(),
                ["width"] = width,
                ["answer"] = "loop",
                ["states"] = states.DeepClone(),
                ["maxBytes"] = maxBytes,
            };

            var clock = Stopwatch.StartNew();
            var (lossless, quality, picture) = await Loop(api, Request(8 * 1024 * 1024));
            var ms = clock.ElapsedMilliseconds;
            Assert.Equal("lossless", quality);
            var sizes = new Dictionary<string, long?> { ["lossless"] = lossless, ["lossy85"] = null, ["lossy70"] = null };
            var cap2 = lossless - 1;
            while (cap2 > 0)
            {
                var (size, q, _) = await Loop(api, Request(cap2));
                if (q == null)
                {
                    sizes["lossy70"] = size;
                    break;
                }
                sizes[q] = size;
                if (q == "lossy70")
                    break;
                cap2 = size - 1;
            }
            var canvas = document["profile"]!;
            var best = sizes.Where(p => p.Value <= cap).Select(p => p.Key).FirstOrDefault() ?? "none";
            string Cell(string q) => sizes[q]?.ToString(CultureInfo.InvariantCulture) ?? "n/a";
            lines.Add($"{Path.GetFileName(file)} | {canvas["width"]}x{canvas["height"]} | {picture} | {Cell("lossless")} | {Cell("lossy85")} | {Cell("lossy70")} | {cap} | {best} | {ms}");
        }
        await File.WriteAllLinesAsync(output!, lines, Ct);
    }

    /// <summary>The loop's size, quality and picture size; a refusal for size gives the size it names and no quality.</summary>
    private static async Task<(long Size, string? Quality, string Picture)> Loop(HttpClient api, JsonObject request)
    {
        using var response = await api.PostAsync("/api/preview", new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json"), Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        var body = JsonDocument.Parse(text).RootElement;
        if (response.StatusCode == HttpStatusCode.UnprocessableEntity)
        {
            var error = body.GetProperty("errors")[0];
            Assert.Equal("preview.size", error.GetProperty("code").GetString());
            return (long.Parse(Regex.Match(error.GetProperty("message").GetString()!, @"(\d+) bytes").Groups[1].Value, CultureInfo.InvariantCulture), null, "");
        }
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode} {text}");
        return (Convert.FromBase64String(body.GetProperty("webp").GetString()!).LongLength, body.GetProperty("quality").GetString(),
            $"{body.GetProperty("width").GetInt32()}x{body.GetProperty("height").GetInt32()}");
    }

    /// <summary>Every binding key in the document, at any depth.</summary>
    private static IEnumerable<string> Keys(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var (name, value) in o)
                {
                    if (name == "bindings" && value is JsonObject bindings)
                    {
                        foreach (var (_, binding) in bindings)
                            if (binding?["key"]?.GetValue<string>() is { } key)
                                yield return key;
                    }
                    else
                    {
                        foreach (var key in Keys(value))
                            yield return key;
                    }
                }
                break;
            case JsonArray a:
                foreach (var item in a)
                    foreach (var key in Keys(item))
                        yield return key;
                break;
        }
    }
}
