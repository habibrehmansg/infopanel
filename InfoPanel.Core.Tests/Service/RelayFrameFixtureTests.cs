using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using InfoPanel.Core.Tests.Golden;
using InfoPanel.Service;
using Xunit;

namespace InfoPanel.Core.Tests.Service;

/// <summary>
/// The relay's frame fixtures (<c>Fixtures/relay/frames.json</c>, docs/2.0/relay.md): one frame of each kind either side
/// sends. The engine writes its own exactly as the fixture has them, and reads each of the relay's as what it is, so a
/// relay that parses the fixtures parses what the engine sends and sends what it reads.
/// </summary>
public sealed class RelayFrameFixtureTests
{
    private static readonly JsonObject Fixtures = JsonNode.Parse(File.ReadAllText(Path.Combine(TestPaths.ProjectDirectory, "Fixtures", "relay", "frames.json")))!.AsObject();

    private static JsonObject Engine(string name) => Fixtures["engine"]![name]!.AsObject();

    private static RelayFrame Relay(string name) => RelayFrames.Read(Encoding.UTF8.GetBytes(Fixtures["relay"]![name]!.ToJsonString()));

    [Fact]
    public void TheEnginesFrames_AreWrittenAsTheFixturesHaveThem()
    {
        Assert.Equal(Engine("hello").ToJsonString(), RelayFrames.Hello(Engine("hello")["engineVersion"]!.GetValue<string>()).ToJsonString());
        Assert.Equal(Engine("time").ToJsonString(), RelayFrames.TimeRequest(Engine("time")["nonce"]!.GetValue<string>()).ToJsonString());
        Assert.Equal(Engine("result").ToJsonString(), RelayFrames.Result("c1", Engine("result")["result"]!.DeepClone().AsObject()).ToJsonString());
        Assert.Equal(Engine("late").ToJsonString(), RelayFrames.Late("c1").ToJsonString());
        Assert.Equal(Engine("cancelled").ToJsonString(), RelayFrames.Cancelled("c1").ToJsonString());
        Assert.Equal(Engine("error").ToJsonString(), RelayFrames.Error("malformed", "c1").ToJsonString());
        Assert.Equal(Engine("bye").ToJsonString(), RelayFrames.Bye().ToJsonString());
        Assert.Equal(Engine("stale").ToJsonString(), RelayFrames.Stale("c3").ToJsonString());
        var item = Engine("item").DeepClone().AsObject();
        item.Remove("type");
        item.Remove("id");
        Assert.Equal(Engine("item").ToJsonString(), RelayFrames.Item("r1", item).ToJsonString());
        Assert.Equal(["bye", "cancelled", "error", "hello", "item", "late", "result", "stale", "time"], Fixtures["engine"]!.AsObject().Select(p => p.Key).Order());
    }

    [Fact]
    public void TheRelaysFrames_AreReadAsWhatTheyAre()
    {
        Assert.IsType<RelayFrame.Welcome>(Relay("welcome"));
        Assert.Equal(new RelayFrame.Time("AAAAAAAAAAAAAAAAAAAAAA", 1762214400000), Relay("time"));
        Assert.Equal(new RelayFrame.Cancel("c2"), Relay("cancel"));

        var call = Assert.IsType<RelayFrame.Call>(Relay("call"));
        Assert.Equal(("c1", "list_profiles", 1762214400000L, 1762214460000L, 140000), (call.Id, call.Tool, call.SentAt, call.Deadline, call.MaxChars));
        Assert.Null(call.Design);

        var design = Assert.IsType<RelayFrame.Call>(Relay("callWithDesign"));
        var given = Fixtures["relay"]!["callWithDesign"]!;
        Assert.Equal(1762214445000L, design.Deadline);
        Assert.Equal(given["design"]!.ToJsonString(), design.Design!.Value.GetRawText());
        Assert.Equal([.. given["files"]!.AsArray().Select(f => f!.GetValue<string>())], design.Files!);

        // Its file is the dep its source names: the text's SHA-256.
        var file = Encoding.UTF8.GetBytes(design.Files![0]);
        Assert.Equal("sha256:" + Convert.ToHexStringLower(SHA256.HashData(file)), given["design"]!["source"]!["deps"]![0]!.GetValue<string>());
        var read = Assert.IsType<RelayFrame.ReadItem>(Relay("read"));
        Assert.Equal(new RelayFrame.ReadItem("r1", "Overview", "Gauge", true, 1762214400000, 140000) { Deadline = 1762214460000 }, read);

        var update = Assert.IsType<RelayFrame.Call>(Relay("callWithBasedOn"));
        Assert.Equal("update_item", update.Tool);
        Assert.Equal(Fixtures["relay"]!["callWithBasedOn"]!["design"]!.ToJsonString(), update.Design!.Value.GetRawText());

        // add_items: a design per item in order, null for the item given as a scene.
        var batch = Assert.IsType<RelayFrame.Call>(Relay("callWithDesigns"));
        var designs = Fixtures["relay"]!["callWithDesigns"]!["designs"]!.AsArray();
        Assert.Equal("add_items", batch.Tool);
        Assert.Null(batch.Design);
        Assert.Equal(designs.Count, batch.Designs!.Count);
        Assert.Equal(designs[0]!.ToJsonString(), batch.Designs[0]!.Value.GetRawText());
        Assert.Null(designs[1]);
        Assert.Null(batch.Designs[1]);

        // A group's children take a design each, right after the group: null for the group and for an item given as a scene.
        var grouped = Assert.IsType<RelayFrame.Call>(Relay("callWithGroupDesigns"));
        var groupDesigns = Fixtures["relay"]!["callWithGroupDesigns"]!["designs"]!.AsArray();
        Assert.Equal(4, grouped.Designs!.Count);
        Assert.Equal([null, null, null], grouped.Designs.Take(3).Select(d => d?.GetRawText()));
        Assert.Equal(groupDesigns[3]!.ToJsonString(), grouped.Designs[3]!.Value.GetRawText());

        // update_item without a design, based on a read: basedOn is a member of the call frame.
        var raw = Assert.IsType<RelayFrame.Call>(Relay("callUpdateBasedOn"));
        Assert.Equal("update_item", raw.Tool);
        Assert.Null(raw.Design);
        Assert.Null(raw.BasedOnList);
        Assert.Equal(Fixtures["relay"]!["callUpdateBasedOn"]!["basedOn"]!.ToJsonString(), raw.BasedOn!.Value.GetRawText());

        // update_items: a design with its own basedOn, a raw entry's basedOn in the list, a plain entry with neither.
        var updates = Assert.IsType<RelayFrame.Call>(Relay("callWithUpdates"));
        var given2 = Fixtures["relay"]!["callWithUpdates"]!;
        Assert.Equal("update_items", updates.Tool);
        Assert.Null(updates.BasedOn);
        Assert.Equal(given2["designs"]![0]!.ToJsonString(), updates.Designs![0]!.Value.GetRawText());
        Assert.Equal([null, null], updates.Designs.Skip(1).Select(d => d?.GetRawText()));
        Assert.Equal([null, given2["basedOn"]![1]!.ToJsonString(), null], updates.BasedOnList!.Select(b => b?.GetRawText()));
        Assert.Equal(["call", "callUpdateBasedOn", "callWithBasedOn", "callWithDesign", "callWithDesigns", "callWithGroupDesigns", "callWithUpdates", "cancel", "read", "time", "welcome"],
            Fixtures["relay"]!.AsObject().Select(p => p.Key).Order());
    }

    private const string Based = """{"instance":"t","rev":3,"item":"a"}""";

    [Theory]
    // An object: what a read found, never beside a design (which carries its own).
    [InlineData("""{"basedOn":"x"}""")]
    [InlineData("""{"basedOn":5}""")]
    [InlineData("""{"basedOn":{"instance":"t","item":"a"}}""")]
    [InlineData("""{"basedOn":{"instance":"t","rev":-1,"item":"a"}}""")]
    [InlineData("""{"basedOn":{"instance":"t","rev":1.5,"item":"a"}}""")]
    [InlineData("""{"basedOn":{"instance":5,"rev":1,"item":"a"}}""")]
    [InlineData("""{"basedOn":{"instance":"t","rev":1}}""")]
    [InlineData("""{"basedOn":{"instance":"t","rev":3,"item":"a"},"design":{"source":{},"scene":{}}}""")]
    // A list: one per entry of the arguments' items, each null or what a read found, never where designs has a design.
    [InlineData("""{"basedOn":[]}""")]
    [InlineData("""{"basedOn":[null,null]}""")]
    [InlineData("""{"basedOn":[5]}""")]
    [InlineData("""{"basedOn":[{"instance":"t","rev":3}]}""")]
    [InlineData("""{"arguments":{"profile":"P"},"basedOn":[null]}""")]
    [InlineData("""{"arguments":{"profile":"P","items":{}},"basedOn":[null]}""")]
    [InlineData("""{"basedOn":[{"instance":"t","rev":3,"item":"a"}],"designs":[{"source":{},"scene":{},"basedOn":{"instance":"t","rev":3,"item":"a"}}]}""")]
    public void ABasedOnNotAsItMustBe_IsMalformed(string members)
    {
        var frame = JsonNode.Parse("""{"type":"call","id":"c","tool":"update_items","sentAt":5,"arguments":{"profile":"P","items":[{"item":"a"}]}}""")!.AsObject();
        foreach (var (name, value) in JsonNode.Parse(members)!.AsObject())
            frame[name] = value?.DeepClone();
        Assert.Equal(new RelayFrame.Refused("malformed", "c"), RelayFrames.Read(Encoding.UTF8.GetBytes(frame.ToJsonString())));
    }

    [Fact]
    public void ABasedOn_IsRead_AsAnObjectOrAList_NullAbsent()
    {
        RelayFrame.Call Call(string members)
        {
            var frame = JsonNode.Parse("""{"type":"call","id":"c","tool":"update_items","sentAt":5,"arguments":{"profile":"P","items":[{"item":"a"},{"item":"b"}]}}""")!.AsObject();
            foreach (var (name, value) in JsonNode.Parse(members)!.AsObject())
                frame[name] = value?.DeepClone();
            return Assert.IsType<RelayFrame.Call>(RelayFrames.Read(Encoding.UTF8.GetBytes(frame.ToJsonString())));
        }

        Assert.Equal(Based, Call($$"""{"basedOn":{{Based}}}""").BasedOn!.Value.GetRawText());
        Assert.Null(Call("""{"basedOn":null}""").BasedOn);
        Assert.Null(Call("""{"basedOn":null}""").BasedOnList);
        Assert.Equal([null, null], Call("""{"basedOn":[null,null]}""").BasedOnList!.Select(b => b?.GetRawText()));
        // Beside designs, on the entries without one.
        var both = Call($$"""{"basedOn":[null,{{Based}}],"designs":[{"source":{},"scene":{},"basedOn":{{Based}}},null]}""");
        Assert.Equal([null, Based], both.BasedOnList!.Select(b => b?.GetRawText()));
        Assert.NotNull(both.Designs![0]);
    }
}
