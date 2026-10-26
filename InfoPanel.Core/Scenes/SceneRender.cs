using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using SkiaSharp;

namespace InfoPanel.Scenes;

/// <summary>
/// The runtime's internal entry point for tests and tools: render a document, or a single scene wrapped in a
/// one-item document, at a scale to a bitmap or PNG. Not exposed by the engine's API or command line.
/// </summary>
internal static class SceneRender
{
    /// <summary>A one-off render of <paramref name="document"/> (state at init, elapsed 0).</summary>
    public static SKBitmap Document(JsonElement document, float scale, DateTimeOffset now, SceneRuntimeOptions? options = null)
    {
        using var snapshot = SceneRuntime.RenderOnce(document, now, options);
        return snapshot.Render(scale);
    }

    public static byte[] DocumentPng(JsonElement document, float scale, DateTimeOffset now, SceneRuntimeOptions? options = null)
    {
        using var snapshot = SceneRuntime.RenderOnce(document, now, options);
        return snapshot.RenderPng(scale);
    }

    /// <summary>
    /// <paramref name="scene"/> as the only item of a <paramref name="width"/> × <paramref name="height"/> document, placed
    /// at <paramref name="x"/>, <paramref name="y"/>, with an optional layout box and bindings (input name → sensor key).
    /// </summary>
    public static JsonElement WrapScene(JsonElement scene, int width, int height, double x = 0, double y = 0,
        double? layoutW = null, double? layoutH = null, IReadOnlyDictionary<string, string>? bindings = null,
        string background = "#ffffffff", double rotation = 0)
    {
        var item = new JsonObject
        {
            ["kind"] = "scene",
            ["id"] = "item",
            ["placement"] = new JsonObject { ["x"] = x, ["y"] = y, ["rotation"] = rotation },
            ["scene"] = JsonNode.Parse(scene.GetRawText()),
        };
        if (layoutW is not null || layoutH is not null)
        {
            var layout = new JsonObject();
            if (layoutW is { } w) layout["w"] = w;
            if (layoutH is { } h) layout["h"] = h;
            item["layout"] = layout;
        }
        if (bindings is { Count: > 0 })
        {
            var b = new JsonObject();
            foreach (var (name, key) in bindings) b[name] = new JsonObject { ["key"] = key };
            item["bindings"] = b;
        }
        var doc = new JsonObject
        {
            ["format"] = "infopanel.document/1",
            ["id"] = "00000000-0000-4000-8000-000000000001",
            ["profile"] = new JsonObject { ["name"] = "Scene", ["width"] = width, ["height"] = height, ["background"] = background },
            ["items"] = new JsonArray(item),
        };
        return JsonSerializer.SerializeToElement(doc);
    }
}
