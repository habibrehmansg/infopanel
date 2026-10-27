using System.Text;
using System.Text.Json;
using InfoPanel.Core.Tests.Persistence;
using InfoPanel.Editing;
using InfoPanel.Import;
using InfoPanel.Models;
using InfoPanel.Persistence;
using InfoPanel.Spec;
using Xunit;
using static InfoPanel.Core.Tests.Import.ImportKit;

namespace InfoPanel.Core.Tests.Import;

/// <summary>
/// One 1.x profile read into its envelope: the items as the 1.x item JSON, guides, profile members, and the diagnostics
/// found in the raw XML (one test per catalogue code the reader emits). Read here through 1.x archives; the folder ends
/// in the same reader.
/// </summary>
public sealed class V1ReaderTests : IDisposable
{
    private readonly ImportKit _kit = new();

    public void Dispose() => _kit.Dispose();

    private static string FixtureItemsXml() => File.ReadAllText(ProfileSerializerTests.FixturePath(V1Format.ItemsFixture));

    [Fact]
    public void AllKinds_ItemsAreTheItemJsonOfTodaysReader_NodeForNode()
    {
        var profile = ProfileSerializerTests.ReadFixtureProfiles()[0];
        var envelope = _kit.ReadArchive(V1Archive(ProfileXml(profile), FixtureItemsXml()));

        var today = ProfileSerializerTests.ReadFixtureItems();
        foreach (var item in today)
            item.SetProfile(profile);
        var expected = today.Select(item => ItemJson.ToNode(item, _kit.Sensors)).ToList();

        Assert.Equal(WithoutIds(expected).ToJsonString(), WithoutIds(envelope.Items).ToJsonString());
        Assert.Equal(today.Count, envelope.Items.Count);
        // Gauge frames are under images, group children under children.
        var gauge = envelope.Items.Single(n => n.Kind == "gauge");
        Assert.Equal(3, gauge.Images!.Count);
        Assert.Equal(2, envelope.Items.Single(n => n.Kind == "group").Children!.Count);
        // The item JSON serializes as the formats write it.
        var json = JsonSerializer.SerializeToNode(envelope, SpecJson.Options)!;
        Assert.Equal("archive", (string)json["source"]!);
        Assert.Equal("gauge", (string)json["items"]![envelope.Items.ToList().IndexOf(gauge)]!["kind"]!);
    }

    [Fact]
    public void Profile_CarriesEveryModelMember()
    {
        var profile = ProfileSerializerTests.ReadFixtureProfiles()[0];
        var envelope = _kit.ReadArchive(V1Archive(ProfileXml(profile), Items()));

        Assert.Equal("6f1c2a3b-4d5e-4f60-8a71-92b3c4d5e6f7", envelope.Id);
        Assert.Equal(ItemJson.ProfileProps(profile).ToJsonString(), envelope.Profile.ToJsonString());
        Assert.Equal("Desk Overlay", (string)envelope.Profile["name"]!);
        Assert.Equal("game.exe, another.exe", (string)envelope.Profile["triggerProcessNames"]!);
        Assert.Equal(new V1TargetWindow { X = 1920, Y = 0, Width = 2560, Height = 1440, DeviceName = "\\\\.\\DISPLAY2" }, envelope.TargetWindow);
    }

    [Fact]
    public void Guides_AreReadFromTheRawXml_AtAnyItemPosition()
    {
        var envelope = _kit.ReadArchive(V1Archive(Items(
            Guide("Centre", 320, 0, length: 480, rotation: 90),
            Text("Label"),
            Group("Box", Guide("Inner", 10, 20, color: "#FF00FF00", rotation: 0)),
            "<DisplayItem xsi:type=\"GuideDisplayItem\" />")));

        Assert.Equal(
            [
                new V1Guide { X = 320, Y = 0, Length = 480, Color = "#FFFF0000", Rotation = 90, Name = "Centre", Hidden = false },
                new V1Guide { X = 10, Y = 20, Length = 300, Color = "#FF00FF00", Rotation = 0, Name = "Inner", Hidden = false },
                // 1.4.x's defaults for an empty guide.
                new V1Guide { X = 100, Y = 100, Length = 200, Color = "#FF00FFFF", Rotation = 0, Name = null, Hidden = false },
            ],
            envelope.Guides);
        Assert.Equal(["text", "group"], envelope.Items.Select(i => i.Kind));
        Assert.Empty(With(envelope, "import.itemType"));
    }

    [Fact]
    public void ItemType_UnknownTypes_AreLeftOut_AtTheRoot_InGroups_AndAmongGaugeFrames()
    {
        // 1.4.x pin 3: unknown item types are dropped at every item position.
        var xml = Items(
            Text("Before"),
            """<DisplayItem xsi:type="FutureDisplayItem"><Name>Future</Name></DisplayItem>""",
            Group("Box", Text("Inside"), """<DisplayItem xsi:type="OtherDisplayItem" />"""),
            Gauge("Dial", Frame("f0", "f0.png"), """<ImageDisplayItem xsi:type="VideoDisplayItem"><Name>clip</Name></ImageDisplayItem>"""));

        var envelope = _kit.ReadArchive(V1Archive(xml));

        Assert.Equal(["Before", "Box", "Dial"], envelope.Items.Select(i => (string)i.Props["name"]!));
        var dropped = With(envelope, "import.itemType");
        Assert.Equal(["/items", "/items/1/children", "/items/2/images"], dropped.Select(d => d.Path));
        Assert.All(dropped, d => Assert.Equal("warning", d.Severity));
        Assert.Contains("FutureDisplayItem", dropped[0].Message);
        Assert.Equal(["DisplayItems.xml"], dropped.Select(d => d.File).Distinct());
        Assert.Equal(4, dropped[0].Line);
    }

    [Fact]
    public void Nil_EmptyItems_AreLeftOut_WithTheirContainer()
    {
        var xml = Items(
            """<DisplayItem xsi:nil="true" />""",
            Group("Box", """<DisplayItem xsi:nil="true" />""", Text("Inside")),
            Gauge("Dial", """<ImageDisplayItem xsi:nil="true" />""", Frame("f0", "f0.png")));

        var envelope = _kit.ReadArchive(V1Archive(xml));

        Assert.Equal(["group", "gauge"], envelope.Items.Select(i => i.Kind));
        var nils = With(envelope, "import.nil");
        Assert.Equal(["/items", "/items/0/children", "/items/1/images"], nils.Select(d => d.Path));
        Assert.All(nils, d => Assert.Equal("info", d.Severity));
    }

    [Fact]
    public void Element_UnknownElements_AreIgnored_AndTheItemStillLoads()
    {
        // 1.4.x pin 4: RefreshInterval (newer builds) is ignored; the item keeps everything else.
        var xml = Items(
            Text("First"),
            Group("Box", """<DisplayItem xsi:type="ImageDisplayItem"><Name>Feed</Name><Type>URL</Type><HttpUrl>https://example.com/a.png</HttpUrl><RefreshInterval>30</RefreshInterval><Scale>80</Scale></DisplayItem>"""));

        var envelope = _kit.ReadArchive(V1Archive(xml));

        var feed = envelope.Items[1].Children![0];
        Assert.Equal(80, (int)feed.Props["scale"]!);
        var unknown = Single(envelope, "import.element");
        Assert.Equal("/items/1/children/0", unknown.Path);
        Assert.Contains("RefreshInterval", unknown.Message);
        Assert.Equal("warning", unknown.Severity);
        Assert.NotNull(unknown.Line);
    }

    [Fact]
    public void Element_UnknownProfileElements_AreReported()
    {
        var profileXml = ProfileXml(new Profile("P", 100, 100)).Replace("<Width>", "<FutureSetting>1</FutureSetting>\n  <Width>");

        var envelope = _kit.ReadArchive(V1Archive(profileXml, Items()));

        var unknown = Single(envelope, "import.element");
        Assert.Equal("/profile", unknown.Path);
        Assert.Equal("Profile.xml", unknown.File);
    }

    [Fact]
    public void Size_ACanvasSideOverTheLimit_IsReported()
    {
        var envelope = _kit.ReadArchive(V1Archive(ProfileXml(new Profile("Wide", 20_000, 480)), Items()));

        var size = Single(envelope, "import.size");
        Assert.Equal("/profile/width", size.Path);
        Assert.Equal(20_000, (int)envelope.Profile["width"]!);
    }

    [Fact]
    public void Font_FamiliesTheEngineLacks_AreReported_OncePerFamily()
    {
        var profile = new Profile("Fonts", 100, 100) { Font = "Rajdhani" };
        var envelope = _kit.ReadArchive(V1Archive(ProfileXml(profile), Items(Text("A", "Arial"), Text("B", "Orbitron"), Group("G", Text("C", "orbitron")))));

        var fonts = With(envelope, "import.font");
        Assert.Equal(["/profile/font", "/items/1/props/font"], fonts.Select(d => d.Path));
        Assert.Contains("'Orbitron' (2 uses)", fonts[1].Message);
    }

    [Fact]
    public void Sensor_BindingsTheEngineHasNoSensorFor_AreReported_AndStayBound()
    {
        var xml = Items("""<DisplayItem xsi:type="SensorDisplayItem"><Name>CPU</Name><SensorType>Libre</SensorType><LibreSensorId>/amdcpu/0/temperature/0</LibreSensorId></DisplayItem>""",
            Text("Unbound"));

        var envelope = _kit.ReadArchive(V1Archive(xml));

        var sensor = Single(envelope, "import.sensor");
        Assert.Equal("/items/0", sensor.Path);
        Assert.Equal("info", sensor.Severity);
        Assert.Equal("/amdcpu/0/temperature/0", (string)envelope.Items[0].Props["libreSensorId"]!);
        Assert.NotNull(envelope.Items[0].Meta.Sensor);
    }

    [Fact]
    public void Items_MissingFile_OverTheLimit_Unreadable_AndNotAList()
    {
        // Over the limit (lowered here; the real 16 MiB is below).
        var over = _kit.ReadArchive(V1Archive(Items(Text("A"))), new ImportLimits { ItemsXmlBytes = 64 });
        var tooLarge = Single(over, "import.items");
        Assert.Equal(("error", "/items"), (tooLarge.Severity, tooLarge.Path));
        Assert.Empty(over.Items);

        var malformed = _kit.ReadArchive(V1Archive("<ArrayOfDisplayItem>\n<DisplayItem>"));
        var xml = Single(malformed, "import.xml");
        Assert.Equal("error", xml.Severity);
        Assert.NotNull(xml.Line);
        Assert.Empty(malformed.Items);

        var notAList = _kit.ReadArchive(V1Archive("<Something />"));
        Assert.Equal("error", Single(notAList, "import.items").Severity);
    }

    [Fact]
    public void Items_OverSixteenMiB_AreNotRead()
    {
        // The real items limit, 16 MiB: one byte over is refused before the XML is read.
        var padding = new string(' ', 16 * 1024 * 1024);
        var xml = Items(Text("A")).Replace("<ArrayOfDisplayItem", padding + "<ArrayOfDisplayItem");

        var envelope = _kit.ReadArchive(V1Archive(xml));

        var items = Single(envelope, "import.items");
        Assert.Equal("error", items.Severity);
        Assert.Contains("larger than 16777216 bytes", items.Message);
        Assert.Empty(envelope.Items);
    }

    [Fact]
    public void Xml_ADtdInTheItems_IsRefused()
    {
        var envelope = _kit.ReadArchive(V1Archive("<!DOCTYPE x [<!ENTITY a \"b\">]><ArrayOfDisplayItem />"));

        Assert.Contains("DTD", Single(envelope, "import.xml").Message);
        Assert.Empty(envelope.Items);
    }

    [Fact]
    public void Diagnostics_AreCapped_WithOneMoreCountingTheRest()
    {
        var xml = Items(Enumerable.Range(0, 30).Select(i => $"""<DisplayItem xsi:type="Future{i}" />""").ToArray());

        var envelope = _kit.ReadArchive(V1Archive(xml), new ImportLimits { Diagnostics = 10 });

        Assert.Equal(11, envelope.Diagnostics.Count);
        Assert.Equal(("more", "20 more diagnostics not listed."), (envelope.Diagnostics[^1].Code, envelope.Diagnostics[^1].Message));
    }
}
