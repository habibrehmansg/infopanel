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
        Assert.Equal(["bye", "cancelled", "error", "hello", "late", "result", "time"], Fixtures["engine"]!.AsObject().Select(p => p.Key).Order());
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
        Assert.Equal(["call", "callWithDesign", "cancel", "time", "welcome"], Fixtures["relay"]!.AsObject().Select(p => p.Key).Order());
    }
}
