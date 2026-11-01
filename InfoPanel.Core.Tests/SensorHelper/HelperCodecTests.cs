using System.Text;
using InfoPanel.Persistence;
using InfoPanel.SensorHelper;
using InfoPanel.Settings;
using Xunit;
using static InfoPanel.Core.Tests.SensorHelper.HelperTestKit;

namespace InfoPanel.Core.Tests.SensorHelper;

/// <summary>
/// The protocol's messages, closed both ways: each request and answer round-trips; a type, a member or a kind of value
/// it doesn't know is refused, as are duplicate members and anything nested deeper than the engine reads JSON; the
/// caps (16,384 sensors and hardware, 1 MiB) and the catalogue's references hold on reading and writing alike.
/// </summary>
public sealed class HelperCodecTests
{
    private static HelperRequest Request(string json) => HelperCodec.DecodeRequest(Encoding.UTF8.GetBytes(json));

    private static HelperAnswer Answer(string json) => HelperCodec.DecodeAnswer(Encoding.UTF8.GetBytes(json));

    private static string Code(Action action) => Assert.ThrowsAny<HelperProtocolException>(action).Code;

    private static readonly HelperHardware Cpu = new("/amdcpu/0", "AMD Ryzen 7 8845HS", "Cpu", null);

    [Fact]
    public void TheLimits_AreTheEnginesJsonDepth_AndTheStorageIntervalSettingsRange()
    {
        Assert.Equal(EngineJson.MaxDepth, HelperProtocol.MaxDepth);
        Assert.Equal(HelperProtocol.MinStorageInterval, SettingsLimits.StorageInterval.Min);
        Assert.Equal(HelperProtocol.MaxStorageInterval, SettingsLimits.StorageInterval.Max);
        Assert.Equal(HelperProtocol.DefaultStorage, LibreSettings.Default.Storage);
        Assert.Equal(HelperProtocol.DefaultStorageInterval, LibreSettings.Default.StorageInterval);
    }

    [Fact]
    public void EachRequest_IsWrittenAsTheProtocolSays_AndReadsBack()
    {
        (HelperRequest Message, string Json)[] cases =
        [
            (new HelloRequest(1), """{"type":"hello","protocol":1}"""),
            (new CatalogueRequest(), """{"type":"catalogue"}"""),
            (new SampleRequest(), """{"type":"sample"}"""),
            (new ConfigureRequest(false, 120), """{"type":"configure","storage":false,"storageInterval":120}"""),
        ];

        foreach (var (message, json) in cases)
        {
            Assert.Equal(json, Encoding.UTF8.GetString(HelperCodec.EncodeRequest(message)));
            Assert.Equal(message, Request(json));
        }
    }

    [Fact]
    public void EachAnswer_IsWrittenAsTheProtocolSays_AndReadsBack()
    {
        var hello = new HelloAnswer(1, "2.0.0", true, "unknown");
        Assert.Equal("""{"type":"hello","protocol":1,"helper":"2.0.0","elevated":true,"driver":"unknown"}""", Encoding.UTF8.GetString(HelperCodec.EncodeAnswer(hello)));
        Assert.Equal(hello, Answer("""{"type":"hello","protocol":1,"helper":"2.0.0","elevated":true,"driver":"unknown"}"""));

        var catalogue = new CatalogueAnswer(3,
            [new("/motherboard", "Board", "Motherboard", null), new("/lpc/nct6798d/0", "Nuvoton", "SuperIO", "/motherboard")],
            [new("/lpc/nct6798d/0/fan/1", "Fan #2", "Fan", 1, "RPM", "/lpc/nct6798d/0")]);
        var catalogueJson = """{"type":"catalogue","version":3,"hardware":[{"id":"/motherboard","name":"Board","type":"Motherboard","parent":null},{"id":"/lpc/nct6798d/0","name":"Nuvoton","type":"SuperIO","parent":"/motherboard"}],"sensors":[{"id":"/lpc/nct6798d/0/fan/1","name":"Fan #2","type":"Fan","index":1,"unit":"RPM","hw":"/lpc/nct6798d/0"}]}""";
        Assert.Equal(catalogueJson, Encoding.UTF8.GetString(HelperCodec.EncodeAnswer(catalogue)));
        var read = Assert.IsType<CatalogueAnswer>(Answer(catalogueJson));
        Assert.Equal(3, read.Version);
        Assert.Equal(catalogue.Hardware, read.Hardware);
        Assert.Equal(catalogue.Sensors, read.Sensors);

        var sample = new SampleAnswer(3, [1.5f, null], [null, 0f], [2.25f, null]);
        var sampleJson = """{"type":"sample","version":3,"value":[1.5,null],"min":[null,0],"max":[2.25,null]}""";
        Assert.Equal(sampleJson, Encoding.UTF8.GetString(HelperCodec.EncodeAnswer(sample)));
        var readSample = Assert.IsType<SampleAnswer>(Answer(sampleJson));
        Assert.Equal(sample.Value, readSample.Value);
        Assert.Equal(sample.Min, readSample.Min);
        Assert.Equal(sample.Max, readSample.Max);

        Assert.Equal(new ConfigureAnswer(true, 5), Answer("""{"type":"configure","storage":true,"storageInterval":5}"""));
        Assert.Equal(new ErrorAnswer("protocol", "no"), Answer("""{"type":"error","code":"protocol","message":"no"}"""));
    }

    [Theory]
    [InlineData("""{"type":"setFan","value":100}""")]
    [InlineData("""{"type":"error","code":"x","message":"y"}""")]
    [InlineData("""{"type":"run","path":"C:\\Windows\\System32\\cmd.exe"}""")]
    [InlineData("""{"protocol":1}""")]
    [InlineData("""{"type":1}""")]
    [InlineData("""{"type":null}""")]
    [InlineData("""["hello"]""")]
    [InlineData("""{"type":"hello","protocol":1}{"type":"hello","protocol":1}""")]
    [InlineData("""{"type":"hello",""")]
    [InlineData("not json")]
    public void ARequestItDoesNotKnow_IsRefused(string json) => Assert.Equal("malformed", Code(() => Request(json)));

    [Theory]
    [InlineData("""{"type":"setFan","value":100}""")]
    [InlineData("""{"type":"configure"}""")]
    [InlineData("""{"type":"catalogue"}""")]
    [InlineData("""{"helper":"2.0.0"}""")]
    [InlineData("""[]""")]
    public void AnAnswerItDoesNotKnow_IsRefused(string json) => Assert.Equal("malformed", Code(() => Answer(json)));

    [Theory]
    [InlineData("""{"type":"hello","protocol":1,"path":"/tmp/x"}""")] // a member it doesn't know
    [InlineData("""{"type":"hello"}""")] // one it needs
    [InlineData("""{"type":"hello","protocol":1,"protocol":1}""")] // twice
    [InlineData("""{"type":"hello","type":"hello","protocol":1}""")]
    [InlineData("""{"type":"hello","protocol":"1"}""")] // the wrong kind
    [InlineData("""{"type":"hello","protocol":1.5}""")]
    [InlineData("""{"type":"hello","protocol":null}""")]
    [InlineData("""{"type":"configure","storage":"true","storageInterval":30}""")]
    [InlineData("""{"type":"configure","storage":true,"storageInterval":0}""")] // out of range
    [InlineData("""{"type":"configure","storage":true,"storageInterval":301}""")]
    [InlineData("""{"type":"sample","code":"Process.Start"}""")]
    public void ARequestWithAMemberItDoesNotTake_IsRefused(string json) => Assert.Equal("malformed", Code(() => Request(json)));

    [Theory]
    [InlineData("""{"type":"sample","version":1,"value":[1],"min":[1]}""")] // a list it needs
    [InlineData("""{"type":"sample","version":1,"value":[1,2],"min":[1],"max":[1]}""")] // lists of different lengths
    [InlineData("""{"type":"sample","version":0,"value":[],"min":[],"max":[]}""")] // no such version
    [InlineData("""{"type":"sample","version":1,"value":["1"],"min":[1],"max":[1]}""")]
    [InlineData("""{"type":"sample","version":1,"value":[1e39],"min":[1],"max":[1]}""")] // past a float
    [InlineData("""{"type":"catalogue","version":1,"hardware":[],"sensors":[{"id":"/a/0","name":"","type":"","index":0,"unit":"","hw":"/missing"}]}""")]
    [InlineData("""{"type":"catalogue","version":1,"hardware":[{"id":"/a","name":"","type":"","parent":"/missing"}],"sensors":[]}""")]
    [InlineData("""{"type":"catalogue","version":1,"hardware":[{"id":"/a","name":"","type":"","parent":"/a"}],"sensors":[]}""")]
    [InlineData("""{"type":"catalogue","version":1,"hardware":[{"id":"/a","name":"","type":"","parent":null},{"id":"/a","name":"","type":"","parent":null}],"sensors":[]}""")]
    [InlineData("""{"type":"catalogue","version":1,"hardware":[{"id":"/a","name":"","type":"","parent":null}],"sensors":[{"id":"/a/0","name":"","type":"","index":0,"unit":"","hw":"/a"},{"id":"/a/0","name":"","type":"","index":1,"unit":"","hw":"/a"}]}""")]
    [InlineData("""{"type":"catalogue","version":1,"hardware":[{"id":"","name":"","type":"","parent":null}],"sensors":[]}""")]
    [InlineData("""{"type":"catalogue","version":1,"hardware":[{"id":"/a","name":"","type":"","parent":null,"control":true}],"sensors":[]}""")]
    [InlineData("""{"type":"hello","protocol":1,"helper":"2.0.0","elevated":"yes","driver":"unknown"}""")]
    [InlineData("""{"type":"error","code":"","message":""}""")]
    public void AnAnswerWithAMemberItDoesNotTake_IsRefused(string json) => Assert.Equal("malformed", Code(() => Answer(json)));

    [Fact]
    public void NestingDeeperThanTheEngineReadsJson_IsRefused_BothWays()
    {
        Assert.Equal("tooDeep", Code(() => Request(Nested(HelperProtocol.MaxDepth + 1))));
        Assert.Equal("tooDeep", Code(() => Answer(Nested(HelperProtocol.MaxDepth + 1))));

        // At the depth itself it is read, and refused only for the member the protocol doesn't have.
        Assert.Equal("malformed", Code(() => Request(Nested(HelperProtocol.MaxDepth))));
        Assert.Equal("malformed", Code(() => Answer(Nested(HelperProtocol.MaxDepth))));
    }

    private static string CatalogueJson(int sensors, int hardware = 1)
    {
        var json = new StringBuilder("""{"type":"catalogue","version":1,"hardware":[""");
        for (var i = 0; i < hardware; i++)
            json.Append(i == 0 ? "" : ",").Append("{\"id\":\"").Append(i).Append("\",\"name\":\"\",\"type\":\"\",\"parent\":null}");
        json.Append("],\"sensors\":[");
        for (var i = 0; i < sensors; i++)
            json.Append(i == 0 ? "" : ",").Append("{\"id\":\"").Append(i).Append("\",\"name\":\"\",\"type\":\"\",\"index\":0,\"unit\":\"\",\"hw\":\"0\"}");
        return json.Append("]}").ToString();
    }

    private static string SampleJson(int values) =>
        "{\"type\":\"sample\",\"version\":1,\"value\":[" + string.Join(",", Enumerable.Repeat("null", values)) +
        "],\"min\":[" + string.Join(",", Enumerable.Repeat("null", values)) +
        "],\"max\":[" + string.Join(",", Enumerable.Repeat("null", values)) + "]}";

    [Fact]
    public void MoreSensorsOrHardwareThanTheCap_AreRefused_OnReading()
    {
        Assert.Equal(HelperProtocol.MaxSensors, Assert.IsType<CatalogueAnswer>(Answer(CatalogueJson(HelperProtocol.MaxSensors))).Sensors.Count);
        Assert.Equal("tooLarge", Code(() => Answer(CatalogueJson(HelperProtocol.MaxSensors + 1))));
        Assert.Equal("tooLarge", Code(() => Answer(CatalogueJson(0, HelperProtocol.MaxHardware + 1))));

        Assert.Equal(HelperProtocol.MaxSensors, Assert.IsType<SampleAnswer>(Answer(SampleJson(HelperProtocol.MaxSensors))).Value.Count);
        Assert.Equal("tooLarge", Code(() => Answer(SampleJson(HelperProtocol.MaxSensors + 1))));
    }

    [Fact]
    public void MoreSensorsThanTheCap_OrMoreThanAFrameHolds_AreRefused_OnWriting()
    {
        // Entries short enough that the frame cap isn't what refuses them: 16,384 fit in a frame, one more is refused.
        var h = new HelperHardware("h", "", "", null);
        var fits = Enumerable.Range(0, HelperProtocol.MaxSensors).Select(i => new HelperSensor($"{i}", "", "", 0, "", "h")).ToList();
        Assert.True(HelperCodec.EncodeAnswer(new CatalogueAnswer(1, [h], fits)).Length <= HelperProtocol.MaxFrameBytes);
        var sensors = Enumerable.Range(0, HelperProtocol.MaxSensors + 1).Select(i => new HelperSensor($"{i}", "", "", 0, "", "h")).ToList();
        Assert.Equal("tooLarge", Code(() => HelperCodec.EncodeAnswer(new CatalogueAnswer(1, [h], sensors))));
        var hardware = Enumerable.Range(0, HelperProtocol.MaxHardware + 1).Select(i => new HelperHardware($"{i}", "", "", null)).ToList();
        Assert.Equal("tooLarge", Code(() => HelperCodec.EncodeAnswer(new CatalogueAnswer(1, hardware, []))));

        var nulls = new float?[HelperProtocol.MaxSensors + 1];
        Assert.Equal("tooLarge", Code(() => HelperCodec.EncodeAnswer(new SampleAnswer(1, nulls, nulls, nulls))));

        var longNames = Enumerable.Range(0, 2_000).Select(i => new HelperSensor($"/amdcpu/0/load/{i}", new string('n', 600), "Load", i, "%", Cpu.Id)).ToList();
        Assert.Equal("tooLarge", Code(() => HelperCodec.EncodeAnswer(new CatalogueAnswer(1, [Cpu], longNames))));
    }

    public static TheoryData<string> ParentCycles => new()
    {
        // two pieces naming each other
        """{"type":"catalogue","version":1,"hardware":[{"id":"/a","name":"","type":"","parent":"/b"},{"id":"/b","name":"","type":"","parent":"/a"}],"sensors":[]}""",
        // three in a ring, after a piece outside it
        """{"type":"catalogue","version":1,"hardware":[{"id":"/r","name":"","type":"","parent":null},{"id":"/a","name":"","type":"","parent":"/c"},{"id":"/b","name":"","type":"","parent":"/a"},{"id":"/c","name":"","type":"","parent":"/b"}],"sensors":[]}""",
        // a chain leading into a ring of two
        """{"type":"catalogue","version":1,"hardware":[{"id":"/x","name":"","type":"","parent":"/a"},{"id":"/a","name":"","type":"","parent":"/b"},{"id":"/b","name":"","type":"","parent":"/a"}],"sensors":[]}""",
    };

    [Theory]
    [MemberData(nameof(ParentCycles))]
    public void HardwareWhoseParentsGoRoundInACycle_IsRefused_BothWays(string json)
    {
        Assert.Equal("malformed", Code(() => Answer(json)));

        var ring = Enumerable.Range(0, 5).Select(i => new HelperHardware($"/h{i}", "", "", $"/h{(i + 1) % 5}")).ToList();
        Assert.Equal("malformed", Code(() => HelperCodec.EncodeAnswer(new CatalogueAnswer(1, ring, []))));
        Assert.Equal("malformed", Code(() => HelperCodec.EncodeAnswer(new CatalogueAnswer(1, [new("/a", "", "", "/b"), new("/b", "", "", "/a")], []))));
    }

    [Fact]
    public void ADeepChainOfParents_WithNoCycle_IsTaken()
    {
        var chain = Enumerable.Range(0, HelperProtocol.MaxHardware).Select(i => new HelperHardware($"/{i}", "", "", i == 0 ? null : $"/{i - 1}")).Reverse().ToList();

        var read = (CatalogueAnswer)HelperCodec.DecodeAnswer(HelperCodec.EncodeAnswer(new CatalogueAnswer(1, chain, [])));

        Assert.Equal(chain, read.Hardware);
    }

    [Fact]
    public void WhatTheReaderRefuses_IsNeverWritten()
    {
        Assert.Equal("malformed", Code(() => HelperCodec.EncodeRequest(new ConfigureRequest(true, 0))));
        Assert.Equal("malformed", Code(() => HelperCodec.EncodeRequest(new ConfigureRequest(true, 301))));
        Assert.Equal("malformed", Code(() => HelperCodec.EncodeAnswer(new SampleAnswer(1, [float.NaN], [null], [null]))));
        Assert.Equal("malformed", Code(() => HelperCodec.EncodeAnswer(new SampleAnswer(1, [1f], [null, null], [null]))));
        Assert.Equal("malformed", Code(() => HelperCodec.EncodeAnswer(new CatalogueAnswer(1, [], [new("/a/0", "", "", 0, "", "/missing")]))));
        Assert.Equal("malformed", Code(() => HelperCodec.EncodeAnswer(new CatalogueAnswer(0, [], []))));
    }

    [Fact]
    public void TheUnits_Are1xs_WithThe096TypesItLeftOut_AndThroughputInBytesPerSecond()
    {
        // Every SensorType of LibreHardwareMonitorLib 0.9.6, by name.
        var expected = new Dictionary<string, string>
        {
            ["Voltage"] = "V", ["Current"] = "A", ["Power"] = "W", ["Clock"] = "MHz", ["Temperature"] = "°C", ["Load"] = "%",
            ["Frequency"] = "Hz", ["Fan"] = "RPM", ["Flow"] = "L/h", ["Control"] = "%", ["Level"] = "%", ["Factor"] = "1",
            ["Data"] = "GB", ["SmallData"] = "MB", ["Throughput"] = "B/s", ["TimeSpan"] = "s", ["Timing"] = "ns",
            ["Energy"] = "mWh", ["Noise"] = "dBA", ["Conductivity"] = "µS/cm", ["Humidity"] = "%",
        };

        foreach (var (type, unit) in expected)
            Assert.Equal(unit, HelperUnits.Of(type));
        Assert.Equal("", HelperUnits.Of("SomethingNewer"));
    }

    [Fact]
    public void TheGroups_Are1xs_StorageByTheSetting()
    {
        Assert.Equal(new HelperGroups(Cpu: true, Gpu: true, Memory: true, Motherboard: true, Controller: true, Network: true, Storage: true, Psu: false, Battery: false, PowerMonitor: false), HelperGroups.For(true));
        Assert.Equal(new HelperGroups(Cpu: true, Gpu: true, Memory: true, Motherboard: true, Controller: true, Network: true, Storage: false, Psu: false, Battery: false, PowerMonitor: false), HelperGroups.For(false));
    }

    [Theory]
    [InlineData("InfoPanel.SensorHelper.1234", true)]
    [InlineData("ipt0123456789ab", true)]
    [InlineData("", false)]
    [InlineData("..", false)]
    [InlineData("../engine", false)]
    [InlineData("a/b", false)]
    [InlineData(@"\\.\pipe\x", false)]
    [InlineData("C:x", false)]
    [InlineData("name with space", false)]
    public void APipeName_IsANameNeverAPath(string name, bool valid)
    {
        Assert.Equal(valid, HelperProtocol.IsPipeName(name));
        Assert.False(HelperProtocol.IsPipeName(new string('a', 65)));
    }
}
