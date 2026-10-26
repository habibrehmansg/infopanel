using InfoPanel.Core.Tests.Persistence;
using InfoPanel.Editing;
using InfoPanel.Models;
using InfoPanel.Sensors;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace InfoPanel.Core.Tests.Editing;

public class ItemJsonTests
{
    private static List<DisplayItem> FixtureItems() =>
        V1Format.ReadItems(Path.Combine(AppContext.BaseDirectory, V1Format.ItemsFixture));

    private static IEnumerable<DisplayItem> Walk(IEnumerable<DisplayItem> items)
    {
        foreach (var item in items)
        {
            yield return item;
            if (item is GroupDisplayItem group)
            {
                foreach (var child in Walk(group.DisplayItems))
                    yield return child;
            }
        }
    }

    private static IEnumerable<ItemNode> WalkNodes(IEnumerable<ItemNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var child in WalkNodes(node.Children ?? []))
                yield return child;
        }
    }

    [Fact]
    public void ItemKinds_CoverEveryModelType()
    {
        Assert.Equal(14, ItemKinds.All.Count);
        Assert.Equal(14, ItemKinds.All.Select(k => k.Id).Distinct().Count());
        Assert.Equal(
            ["text", "sensor", "clock", "calendar", "bar", "graph", "donut", "shape", "group", "table", "image", "sensorImage", "httpImage", "gauge"],
            ItemKinds.All.Select(k => k.Id));

        foreach (var item in Walk(FixtureItems()))
        {
            var kind = ItemKinds.Of(item);
            Assert.Equal(item.GetType(), kind.ClrType);
            Assert.True(ItemKinds.TryGet(kind.Id, out var byId));
            Assert.Same(kind, byId);
        }

        Assert.False(ItemKinds.TryGet("Text", out _));
    }

    [Fact]
    public void PropertyMap_BuildsForEveryKindAndProfile()
    {
        foreach (var kind in ItemKinds.All)
        {
            var map = PropertyMap.For(kind.ClrType);
            Assert.NotEmpty(map.Properties);
            Assert.Equal(map.Properties.Count, map.Properties.Select(p => p.Name).Distinct().Count());
            Assert.All(map.Properties, p => Assert.Equal(JsonNamingPolicy.CamelCase.ConvertName(p.ClrName), p.Name));
            Assert.DoesNotContain(map.Properties, p => p.ClrName is "Guid" or "DisplayItems" or "Images" or "Kind" or "Profile" or "ProfileGuid");
            Assert.Equal(["name", "hidden", "x", "y"], map.Properties.Take(4).Select(p => p.Name));
        }

        var profile = PropertyMap.For(typeof(Profile));
        Assert.DoesNotContain(profile.Properties, p => p.ClrName is "Guid" or "TargetWindow");
        Assert.True(profile.TryGet("backgroundColor", out _));
    }

    [Fact]
    public void PropertyMap_TypesAndEnums()
    {
        var sensor = PropertyMap.For(typeof(SensorDisplayItem));

        Assert.True(sensor.TryGet("x", out var x));
        Assert.Equal(PropertyJsonType.Int, x.Type);
        Assert.True(sensor.TryGet("id", out var id));
        Assert.Equal(PropertyJsonType.UInt, id.Type);
        Assert.True(sensor.TryGet("bold", out var bold));
        Assert.Equal(PropertyJsonType.Bool, bold.Type);
        Assert.True(sensor.TryGet("name", out var name));
        Assert.Equal(PropertyJsonType.String, name.Type);
        Assert.True(sensor.TryGet("valueType", out var valueType));
        Assert.Equal(PropertyJsonType.Enum, valueType.Type);
        Assert.Equal(["NOW", "MIN", "MAX", "AVERAGE"], valueType.EnumValues);
        Assert.Null(name.EnumValues);

        Assert.True(PropertyMap.For(typeof(Profile)).TryGet("fontScale", out var fontScale));
        Assert.Equal(PropertyJsonType.Float, fontScale.Type);
        Assert.True(PropertyMap.For(typeof(GaugeDisplayItem)).TryGet("minValue", out var gaugeMin));
        Assert.Equal(PropertyJsonType.Double, gaugeMin.Type);
    }

    [Fact]
    public void PropertyMap_ReadOnlyProperties()
    {
        static bool IsReadOnly(Type type, string name) =>
            PropertyMap.For(type).TryGet(name, out var p) ? p.ReadOnly : throw new KeyNotFoundException($"{type.Name}.{name}");

        Assert.True(IsReadOnly(typeof(DonutDisplayItem), "radius"));
        Assert.True(IsReadOnly(typeof(ImageDisplayItem), "readOnly"));
        Assert.True(IsReadOnly(typeof(HttpImageDisplayItem), "readOnly"));
        Assert.True(IsReadOnly(typeof(GroupDisplayItem), "displayItemsCount"));
        Assert.True(IsReadOnly(typeof(HttpImageDisplayItem), "type"));
        Assert.True(IsReadOnly(typeof(HttpImageDisplayItem), "httpUrl"));
        Assert.False(IsReadOnly(typeof(ImageDisplayItem), "type"));
        Assert.False(IsReadOnly(typeof(ImageDisplayItem), "httpUrl"));
        Assert.False(IsReadOnly(typeof(TextDisplayItem), "x"));
    }

    [Fact]
    public void Manifest_HasDefaultsFromAFreshInstance()
    {
        var manifest = ItemKinds.BuildManifest();

        Assert.Equal(14, manifest.Count);
        foreach (var kind in manifest)
        {
            Assert.Equal(kind.Properties.Select(p => p.Name), kind.Defaults.Select(d => d.Key));
            Assert.All(kind.Properties.Where(p => p.Type == PropertyJsonType.Enum), p => Assert.NotEmpty(p.EnumValues!));
        }

        var text = manifest.Single(k => k.Kind == "text");
        Assert.Equal("TextDisplayItem", text.ClrType);
        Assert.Equal(100, (int)text.Defaults["x"]!);
        Assert.Equal(new TextDisplayItem().FontSize, (int)text.Defaults["fontSize"]!);

        var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"type\":\"enum\"", json);
    }

    [Fact]
    public void ToNode_OverTheV1Fixture()
    {
        using var mock = new MockSensorSource(1);
        var hub = new SensorHub([mock], new SensorHubOptions { SimulateUnknown = true });
        var items = FixtureItems();

        var nodes = items.Select(i => ItemJson.ToNode(i, hub)).ToList();
        var all = WalkNodes(nodes).ToList();
        var models = Walk(items).ToList();

        Assert.Equal(models.Count, all.Count);
        Assert.Equal(14, all.Select(n => n.Kind).Distinct().Count());
        Assert.Equal(models.Select(m => m.Guid.ToString()), all.Select(n => n.Id));

        foreach (var (node, model) in all.Zip(models))
        {
            Assert.Equal(ItemKinds.Of(model).Id, node.Kind);
            Assert.Equal(model.Name, (string?)node.Props["name"]);
            Assert.Equal(model.X, (int)node.Props["x"]!);
            Assert.False(node.Props.ContainsKey("guid"));
            Assert.Equal(model is GroupDisplayItem, node.Children != null);
            Assert.Equal(model is GaugeDisplayItem, node.Images != null);
            Assert.Equal(SensorKeys.Of(model) != null, node.Meta.Sensor != null);
        }

        var outer = nodes.First(n => n.Kind == "group");
        Assert.Equal("Outer Group", (string?)outer.Props["name"]);
        Assert.Contains(outer.Children!, c => c.Kind == "group" && c.Children!.Count == 2);

        var gauge = all.Single(n => n.Kind == "gauge");
        Assert.Equal(["g0.png", "g1.png", "g2.png"], gauge.Images!.Select(i => (string?)i.Props["filePath"]));
        Assert.All(gauge.Images!, i => Assert.Equal("image", i.Kind));

        // Enums are names; HTTP image URL is always null.
        Assert.Contains(all, n => n.Props["sensorType"]?.GetValue<string>() == "HwInfo");
        Assert.Null(all.Single(n => n.Kind == "httpImage").Props["httpUrl"]);

        // Sensor status: mock keys are live, Windows bindings simulated (mock mode), unknown mock keys unavailable.
        var statuses = all.Where(n => n.Meta.Sensor != null).ToLookup(n => n.Meta.Sensor!.Key, n => n.Meta.Sensor!.Status);
        Assert.Equal(SensorStatus.Live, statuses["plugin:/infopanel.mock/gpu/gpu-temp"].First());
        Assert.Equal(SensorStatus.Simulated, statuses["libre:/amdcpu/0/load/0"].First());
        Assert.Equal(SensorStatus.Simulated, statuses["hwinfo:1:4026532608:1:16777221"].First());
        Assert.Equal(SensorStatus.Unavailable, statuses["plugin:/infopanel.mock/processes/top"].First());
    }

    [Fact]
    public void ToNode_SerializesToThePlanShape()
    {
        var hub = new SensorHub([], new SensorHubOptions { SimulateUnknown = true });
        var item = new SensorDisplayItem("CPU", new Profile("P", 100, 100)) { SensorType = Enums.SensorType.Libre, LibreSensorId = "/cpu/0" };

        var json = JsonSerializer.SerializeToNode(ItemJson.ToNode(item, hub),
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull })!.AsObject();

        Assert.Equal(["id", "kind", "props", "meta"], json.Select(p => p.Key));
        Assert.Equal("sensor", (string?)json["kind"]);
        Assert.Equal("libre:/cpu/0", (string?)json["meta"]!["sensor"]!["key"]);
        Assert.Equal("simulated", (string?)json["meta"]!["sensor"]!["status"]);
        Assert.Equal("Libre", (string?)json["props"]!["sensorType"]);
    }

    [Fact]
    public void BindingProps_AreCamelCasedItemProps()
    {
        var binding = ItemJson.BindingProps(SensorKey.HwInfo(-1, 5, 6, 7), "Temp");

        Assert.Equal("Temp", (string?)binding["sensorName"]);
        Assert.Equal("HwInfo", (string?)binding["sensorType"]);
        Assert.Equal(-1, (int)binding["hwInfoRemoteIndex"]!);
        Assert.Equal(5u, (uint)binding["id"]!);
        Assert.Equal(7u, (uint)binding["entryId"]!);

        var sensorMap = PropertyMap.For(typeof(SensorDisplayItem));
        Assert.All(binding, p => Assert.True(sensorMap.TryGet(p.Key, out _), p.Key));
    }

    [Fact]
    public void ProfileProps()
    {
        var profile = new Profile("Desk", 1920, 480) { BackgroundColor = "#FF000000" };

        var props = ItemJson.ProfileProps(profile);

        Assert.Equal("Desk", (string?)props["name"]);
        Assert.Equal(1920, (int)props["width"]!);
        Assert.Equal("#FF000000", (string?)props["backgroundColor"]);
        Assert.False(props.ContainsKey("guid"));
        Assert.IsType<JsonValue>(props["fontScale"], exactMatch: false);
    }
}
