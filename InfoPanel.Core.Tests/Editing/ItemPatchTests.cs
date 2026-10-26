using InfoPanel.Editing;
using InfoPanel.Enums;
using InfoPanel.Models;
using System.Text.Json.Nodes;
using Xunit;

namespace InfoPanel.Core.Tests.Editing;

/// <summary><see cref="ItemJson.Apply"/> and <see cref="ItemJson.ApplyProfile"/>: types, validation and read-back.</summary>
public class ItemPatchTests
{
    private static readonly Profile Profile = new("Patch", 400, 200);

    private static JsonObject Set(string json) => JsonNode.Parse(json)!.AsObject();

    [Fact]
    public void Apply_SetsEveryJsonType()
    {
        var text = new TextDisplayItem("Text", Profile);
        ItemJson.Apply(text, Set("""{"name":"Hello","x":-5,"fontSize":48,"bold":true,"color":"#FF112233","font":"Inter"}"""));
        Assert.Equal(("Hello", -5, 48, true, "#FF112233", "Inter"), (text.Name, text.X, text.FontSize, text.Bold, text.Color, text.Font));

        var sensor = new SensorDisplayItem("Sensor", Profile);
        ItemJson.Apply(sensor, Set("""
            {"sensorType":"HwInfo","id":4294967295,"instance":1,"entryId":3,"valueType":"MAX",
             "threshold1":80.5,"multiplicationModifier":1e3,"precision":2}
            """));
        Assert.Equal(SensorType.HwInfo, sensor.SensorType);
        Assert.Equal(uint.MaxValue, sensor.Id); // on sensor-bound kinds `id` is the HWiNFO sensor id
        Assert.Equal((1u, 3u), (sensor.Instance, sensor.EntryId));
        Assert.Equal(SensorValueType.MAX, sensor.ValueType);
        Assert.Equal((80.5, 1000.0, 2), (sensor.Threshold1, sensor.MultiplicationModifier, sensor.Precision));

        var graph = new GraphDisplayItem("Graph", Profile, GraphDisplayItem.GraphType.LINE);
        ItemJson.Apply(graph, Set("""{"type":"HISTOGRAM"}"""));
        Assert.Equal(GraphDisplayItem.GraphType.HISTOGRAM, graph.Type);

        var profile = new Profile("P", 10, 10);
        ItemJson.ApplyProfile(profile, Set("""{"fontScale":1.5,"triggerProcessNames":null,"name":"Renamed","showFps":true}"""));
        Assert.Equal((1.5f, null, "Renamed", true), (profile.FontScale, profile.TriggerProcessNames, profile.Name, profile.ShowFps));
    }

    [Fact]
    public void Apply_GoesThroughTheSetters_SoReadingBackShowsTheirClamping()
    {
        var donut = new DonutDisplayItem("Donut", Profile);
        ItemJson.Apply(donut, Set("""{"width":0,"span":400,"thickness":0}"""));
        Assert.Equal((100, 360, 10), (donut.Width, donut.Span, donut.Thickness)); // 1.x setters ignore out-of-range values

        var profile = new Profile("P", 10, 10);
        ItemJson.ApplyProfile(profile, Set("""{"color":"FF00FF00","width":0}"""));
        Assert.Equal("#FF00FF00", profile.Color); // the setter adds the '#'
        Assert.Equal(10, profile.Width);
        Assert.Equal("#FF00FF00", (string?)ItemJson.ProfileProps(profile)["color"]);
    }

    [Theory]
    [InlineData("text", """{"nope":1}""", "nope")]
    [InlineData("text", """{"guid":"00000000-0000-0000-0000-000000000001"}""", "guid")]
    [InlineData("text", """{"kind":"sensor"}""", "kind")]
    [InlineData("text", """{"id":"00000000-0000-0000-0000-000000000001"}""", "id")]
    [InlineData("group", """{"children":[]}""", "children")]
    [InlineData("gauge", """{"images":[]}""", "images")]
    [InlineData("text", """{"props":{}}""", "props")]
    [InlineData("text", """{"meta":{}}""", "meta")]
    [InlineData("donut", """{"radius":20}""", "radius")]
    [InlineData("group", """{"displayItemsCount":3}""", "displayItemsCount")]
    [InlineData("httpImage", """{"httpUrl":"http://x"}""", "httpUrl")]
    [InlineData("httpImage", """{"readOnly":false}""", "readOnly")]
    [InlineData("text", """{"x":"5"}""", "x")]
    [InlineData("text", """{"x":1.5}""", "x")]
    [InlineData("text", """{"x":2147483648}""", "x")]
    [InlineData("text", """{"x":true}""", "x")]
    [InlineData("text", """{"x":null}""", "x")]
    [InlineData("text", """{"x":[1]}""", "x")]
    [InlineData("text", """{"x":{"v":1}}""", "x")]
    [InlineData("text", """{"bold":1}""", "bold")]
    [InlineData("text", """{"bold":"true"}""", "bold")]
    [InlineData("text", """{"name":5}""", "name")]
    [InlineData("text", """{"name":null}""", "name")]
    [InlineData("sensor", """{"id":-1}""", "id")]
    [InlineData("sensor", """{"threshold1":"80"}""", "threshold1")]
    [InlineData("sensor", """{"valueType":"max"}""", "valueType")]
    [InlineData("sensor", """{"valueType":"Foo"}""", "valueType")]
    [InlineData("sensor", """{"valueType":1}""", "valueType")]
    [InlineData("sensor", """{"sensorType":"1"}""", "sensorType")]
    [InlineData("text", """{"color":"red"}""", "color")]
    [InlineData("text", """{"color":"#GG0000"}""", "color")]
    [InlineData("shape", """{"gradientColor2":"#FF00ZZ00"}""", "gradientColor2")]
    // Characters XML 1.0 can't carry: the profile could not be saved or snapshotted.
    [InlineData("text", """{"name":"\u0000"}""", "name")]
    [InlineData("text", """{"name":"bell \u0007"}""", "name")]
    [InlineData("text", """{"name":"\uFFFE"}""", "name")]
    [InlineData("text", """{"name":"lone \uD800 high"}""", "name")]
    [InlineData("text", """{"name":"lone \uDC00 low"}""", "name")]
    [InlineData("sensor", """{"unit":"\u001F"}""", "unit")]
    // Ranges the renderer copes with.
    [InlineData("graph", """{"type":"HISTOGRAM","thickness":2147483647,"step":2147483647}""", "thickness")]
    [InlineData("graph", """{"step":16385}""", "step")]
    [InlineData("donut", """{"thickness":-1}""", "thickness")]
    [InlineData("text", """{"fontSize":2049}""", "fontSize")]
    [InlineData("text", """{"x":1000001}""", "x")]
    [InlineData("text", """{"y":-1000001}""", "y")]
    [InlineData("text", """{"glowRadius":513}""", "glowRadius")]
    [InlineData("bar", """{"width":16385}""", "width")]
    [InlineData("shape", """{"height":-1}""", "height")]
    [InlineData("shape", """{"frameThickness":100000}""", "frameThickness")]
    [InlineData("image", """{"scale":1001}""", "scale")]
    [InlineData("image", """{"volume":101}""", "volume")]
    [InlineData("table", """{"maxRows":1001}""", "maxRows")]
    // Malformed table formats.
    [InlineData("table", """{"tableFormat":"-1:100"}""", "tableFormat")]
    [InlineData("table", """{"tableFormat":"0:10|"}""", "tableFormat")]
    [InlineData("table", """{"tableFormat":"0:10:5"}""", "tableFormat")]
    [InlineData("table", """{"tableFormat":"0:abc"}""", "tableFormat")]
    [InlineData("table", """{"tableFormat":" 0:10"}""", "tableFormat")]
    [InlineData("table", """{"tableFormat":"0:99999"}""", "tableFormat")]
    [InlineData("table", """{"tableFormat":"99999999999:10"}""", "tableFormat")]
    // A setter that throws (FormatException): 400, not 500.
    [InlineData("calendar", """{"format":"Q"}""", "format")]
    public void Apply_RefusesBadSets_WithTheOffendingKey(string kind, string json, string key)
    {
        ItemKinds.TryGet(kind, out var itemKind);
        var item = (DisplayItem)Activator.CreateInstance(itemKind!.ClrType)!;
        var before = InfoPanel.Persistence.ProfileSerializer.SnapshotItems([item]);

        var ex = Assert.Throws<PatchException>(() => ItemJson.Apply(item, Set(json)));

        Assert.Equal(key, ex.Key);
        Assert.Equal(EditError.Invalid, ex.Error);
        Assert.Equal(before, InfoPanel.Persistence.ProfileSerializer.SnapshotItems([item]));
    }

    [Fact]
    public void Apply_AcceptsTextXmlCarries_AndValuesAtTheLimits()
    {
        var text = new TextDisplayItem("Text", Profile);
        ItemJson.Apply(text, Set("""{"name":"tab\tline\nreturn\r emoji \uD83D\uDE00 \uFFFD","fontSize":2048,"x":-1000000,"width":16384}"""));
        Assert.Equal("tab\tline\nreturn\r emoji \U0001F600 \uFFFD", text.Name);
        Assert.Equal((2048, -1000000, 16384), (text.FontSize, text.X, text.Width));

        var table = new TableSensorDisplayItem("Table", Profile);
        ItemJson.Apply(table, Set("""{"tableFormat":"0:120|7:0|2:16384"}"""));
        Assert.Equal("0:120|7:0|2:16384", table.TableFormat);
        ItemJson.Apply(table, Set("""{"tableFormat":""}"""));
        Assert.Equal("", table.TableFormat);
    }

    [Fact]
    public void Apply_SetterThatThrows_IsAPatchException_AndKeepsTheOldValue()
    {
        var calendar = new CalendarDisplayItem("Calendar", Profile) { Format = "yyyy" };

        var ex = Assert.Throws<PatchException>(() => ItemJson.Apply(calendar, Set("""{"format":"Q"}""")));

        Assert.Equal("format", ex.Key);
        Assert.Equal("yyyy", calendar.Format);
    }

    [Fact]
    public void Apply_ValidatesEveryKeyBeforeSettingAny()
    {
        var text = new TextDisplayItem("Text", Profile) { X = 1 };

        var ex = Assert.Throws<PatchException>(() => ItemJson.Apply(text, Set("""{"x":5,"y":6,"fontSize":"big"}""")));

        Assert.Equal("fontSize", ex.Key);
        Assert.Equal((1, 100), (text.X, text.Y));
    }

    [Theory]
    [InlineData("""{"id":"x"}""", "id")]
    [InlineData("""{"guid":"x"}""", "guid")]
    [InlineData("""{"targetWindow":null}""", "targetWindow")]
    [InlineData("""{"fontScale":"1"}""", "fontScale")]
    [InlineData("""{"backgroundColor":"nope"}""", "backgroundColor")]
    [InlineData("""{"name":null}""", "name")]
    [InlineData("""{"name":"\u0001"}""", "name")]
    [InlineData("""{"name":"\uD83D"}""", "name")]
    [InlineData("""{"width":16385}""", "width")]
    [InlineData("""{"fontSize":4096}""", "fontSize")]
    [InlineData("""{"fontScale":0}""", "fontScale")]
    public void ApplyProfile_RefusesBadSets(string json, string key)
    {
        var profile = new Profile("P", 10, 10);

        var ex = Assert.Throws<PatchException>(() => ItemJson.ApplyProfile(profile, Set(json)));

        Assert.Equal(key, ex.Key);
        Assert.Equal("P", profile.Name);
    }

    [Fact]
    public void BindingProps_PatchOntoAnItem()
    {
        var bar = new BarDisplayItem("Bar", Profile);
        var key = InfoPanel.Sensors.SensorKey.Plugin("/infopanel.mock/gpu/gpu-temp");

        ItemJson.Apply(bar, ItemJson.BindingProps(key, "GPU Temperature"));

        Assert.Equal(key, InfoPanel.Sensors.SensorKeys.Of(bar));
        Assert.Equal("GPU Temperature", bar.SensorName);
    }

    [Fact]
    public void Factory_MirrorsThe1xEditor()
    {
        var profile = new Profile("P", 10, 10) { Font = "Menlo", FontSize = 33, Color = "#FF123456" };

        var text = Assert.IsType<TextDisplayItem>(ItemFactory.Create("text", profile));
        Assert.Equal(("Custom Text", "Menlo", 33, "#FF123456", false), (text.Name, text.Font, text.FontSize, text.Color, text.Uppercase));
        Assert.True(Assert.IsType<ClockDisplayItem>(ItemFactory.Create("clock", profile)).Uppercase);
        Assert.True(Assert.IsType<CalendarDisplayItem>(ItemFactory.Create("calendar", profile)).Uppercase);
        Assert.Equal("New Group", Assert.IsType<GroupDisplayItem>(ItemFactory.Create("group", profile)).Name);
        Assert.Equal(33, Assert.IsType<SensorDisplayItem>(ItemFactory.Create("sensor", profile)).FontSize);
        Assert.Equal(GraphDisplayItem.GraphType.LINE, Assert.IsType<GraphDisplayItem>(ItemFactory.Create("graph", profile)).Type);
        Assert.False(Assert.IsType<DonutDisplayItem>(ItemFactory.Create("donut", profile)).Frame);
        Assert.All(ItemFactory.CreatableKinds, kind => Assert.Same(profile, ItemFactory.Create(kind, profile).Profile));

        foreach (var kind in new[] { "image", "gauge", "table", "sensorImage", "httpImage", "Text", "nope" })
            Assert.Equal("kind", Assert.Throws<PatchException>(() => ItemFactory.Create(kind, profile)).Key);
    }
}
