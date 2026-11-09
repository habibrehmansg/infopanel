using System.Text;
using System.Xml.Linq;
using System.Xml.Serialization;
using InfoPanel.Enums;
using InfoPanel.Import;
using InfoPanel.Models;
using InfoPanel.Persistence;
using InfoPanel.Sensors;
using Xunit;

namespace InfoPanel.Core.Tests.Persistence;

public class ProfileSerializerTests
{
    private static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";

    internal static string FixturePath(string relative) => Path.Combine(AppContext.BaseDirectory, relative);

    internal static List<DisplayItem> ReadFixtureItems()
    {
        using var stream = File.OpenRead(FixturePath(V1Format.ItemsFixture));
        return ProfileSerializer.ReadItems(stream);
    }

    internal static List<Profile> ReadFixtureProfiles()
    {
        using var stream = File.OpenRead(FixturePath(V1Format.ProfilesFixture));
        return ProfileSerializer.ReadProfiles(stream);
    }

    private static byte[] Write(Action<Stream> write)
    {
        using var stream = new MemoryStream();
        write(stream);
        return stream.ToArray();
    }

    [Fact]
    public void RoundTrip_AllKinds()
    {
        var items = ReadFixtureItems();
        var written = Write(s => ProfileSerializer.WriteItems(s, items));
        var fixture = File.ReadAllBytes(FixturePath(V1Format.ItemsFixture));

        // Same bytes on the wire as 1.x: BOM, the same declaration line, CRLF only.
        AssertSameFraming(fixture, written);

        var expected = XDocument.Load(new MemoryStream(fixture));
        var actual = XDocument.Load(new MemoryStream(written));

        // The two whitelisted differences: 2.0 adds <Guid>, and HttpImage.HttpUrl is never written.
        StripGuidsAndHttpImageUrl(expected);
        StripGuidsAndHttpImageUrl(actual);
        // 1.x loading quirk, not a 2.0 change: Radius follows Width/Height and its setter makes both 2 * Radius,
        // so an odd-width donut comes back one pixel smaller (see Donut_OddWidth_Matches1x).
        foreach (var donut in ItemElements(expected, "DonutDisplayItem"))
        {
            var size = 2 * (int)donut.Element("Radius")!;
            donut.Element("Width")!.Value = size.ToString();
            donut.Element("Height")!.Value = size.ToString();
        }

        Assert.True(XNode.DeepEquals(expected.Root, actual.Root),
            "Items XML differs from the 1.x fixture:\n" + actual);

        // And as text: a 2.0 file diffs against the 1.x one by its <Guid> lines and the odd donut's size only.
        var writtenLines = Encoding.UTF8.GetString(written).Split("\r\n").Where(l => !l.TrimStart().StartsWith("<Guid>")).ToList();
        var fixtureLines = Encoding.UTF8.GetString(fixture).Split("\r\n").ToList();
        var differing = fixtureLines.Zip(writtenLines).Where(p => p.First != p.Second).ToList();
        Assert.Equal(fixtureLines.Count, writtenLines.Count);
        Assert.Equal(
            [("    <Width>101</Width>", "    <Width>100</Width>"), ("    <Height>101</Height>", "    <Height>100</Height>")],
            differing);
    }

    [Fact]
    public void RoundTrip_Profiles_ByteIdentical()
    {
        var profiles = ReadFixtureProfiles();
        var written = Write(s => ProfileSerializer.WriteProfiles(s, profiles));

        Assert.Equal(File.ReadAllBytes(FixturePath(V1Format.ProfilesFixture)), written);
    }

    [Fact]
    public void RoundTrip_SingleProfile()
    {
        var profile = ReadFixtureProfiles()[0];
        var written = Write(s => ProfileSerializer.WriteProfile(s, profile));
        var read = ProfileSerializer.ReadProfile(new MemoryStream(written));

        Assert.Equal("Profile", XDocument.Load(new MemoryStream(written)).Root!.Name.LocalName);
        Assert.Equal(profile.Guid, read.Guid);
        Assert.Equal(profile.Name, read.Name);
        Assert.Equal(@"\\.\DISPLAY2", read.TargetWindow?.DeviceName);
    }

    [Fact]
    public void Ids_PersistAcrossSaveLoad()
    {
        var items = ReadFixtureItems();
        var ids = ItemTree.Walk(items).Select(i => i.Guid).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());

        var written = Write(s => ProfileSerializer.WriteItems(s, items));
        var reread = ProfileSerializer.ReadItems(new MemoryStream(written));

        Assert.Equal(ids, ItemTree.Walk(reread).Select(i => i.Guid));

        // <Guid> is the first child of every item element, gauge frames included.
        var document = XDocument.Load(new MemoryStream(written));
        var itemElements = document.Descendants()
            .Where(e => e.Attribute(Xsi + "type") != null || e.Parent?.Name.LocalName == "Images")
            .ToList();
        Assert.Equal(ids.Count, itemElements.Count);
        Assert.All(itemElements, e => Assert.Equal("Guid", e.Elements().First().Name.LocalName));
    }

    [Fact]
    public void Ids_DuplicatesRegenerated()
    {
        var profile = new Profile("Ids", 400, 200);
        var shared = Guid.NewGuid();

        var top = new TextDisplayItem("top", profile) { Guid = shared };
        var empty = new TextDisplayItem("empty", profile) { Guid = Guid.Empty };
        var inner = new GroupDisplayItem { Name = "inner", Guid = shared };
        inner.DisplayItems.Add(new ClockDisplayItem("nested clock", profile) { Guid = shared });
        var outer = new GroupDisplayItem { Name = "outer" };
        outer.DisplayItems.Add(new SensorDisplayItem("grouped", profile) { Guid = shared });
        outer.DisplayItems.Add(inner);
        var gauge = new GaugeDisplayItem("gauge", profile) { Guid = outer.Guid };
        var frame = Guid.NewGuid();
        for (var i = 0; i < 3; i++)
            gauge.Images.Add(new ImageDisplayItem($"g{i}", profile) { Guid = frame });

        List<DisplayItem> items = [top, empty, outer, gauge];
        var regenerated = ItemTree.EnsureUniqueIds(items);

        // empty, grouped, inner, nested clock, gauge (same as outer), g1, g2.
        Assert.Equal(7, regenerated);
        var ids = ItemTree.Walk(items).Select(i => i.Guid).ToList();
        Assert.Equal(10, ids.Count);
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.DoesNotContain(Guid.Empty, ids);
        Assert.Equal(shared, top.Guid);
        Assert.Equal(frame, gauge.Images[0].Guid);

        // The same repair runs on every read.
        var xml = ProfileSerializer.SnapshotItems(items).Replace(top.Guid.ToString(), outer.Guid.ToString());
        var reread = ProfileSerializer.ReadItems(new StringReader(xml));
        var rereadIds = ItemTree.Walk(reread).Select(i => i.Guid).ToList();
        Assert.Equal(rereadIds.Count, rereadIds.Distinct().Count());
        Assert.Equal(outer.Guid, rereadIds[0]);
    }

    [Fact]
    public void Clone_GivesFreshIds_IncludingDescendantsAndGaugeFrames()
    {
        var items = ReadFixtureItems();
        var originalIds = ItemTree.Walk(items).Select(i => i.Guid).ToHashSet();

        var clones = items.Select(i => (DisplayItem)i.Clone()).ToList();
        var cloneIds = ItemTree.Walk(clones).Select(i => i.Guid).ToList();

        Assert.Equal(originalIds.Count, cloneIds.Count);
        Assert.Equal(cloneIds.Count, cloneIds.Distinct().Count());
        Assert.DoesNotContain(cloneIds, originalIds.Contains);
    }

    [Fact]
    public void V1Reader_ToleratesGuid()
    {
        var items = ReadFixtureItems();
        var written = Write(s => ProfileSerializer.WriteItems(s, items));
        Assert.Contains("<Guid>", Encoding.UTF8.GetString(written));

        // 1.x's reader: the same types, with DisplayItem.Guid ignored as it was in 1.x.
        var overrides = new XmlAttributeOverrides();
        overrides.Add(typeof(DisplayItem), nameof(DisplayItem.Guid), new XmlAttributes { XmlIgnore = true });
        var v1Serializer = new XmlSerializer(typeof(List<DisplayItem>), overrides, V1Format.ExtraTypes, null, null);

        var v1Items = (List<DisplayItem>)v1Serializer.Deserialize(new MemoryStream(written))!;

        var all = ItemTree.Walk(v1Items).ToList();
        Assert.Equal(14, all.Select(i => i.GetType()).Distinct().Count());
        Assert.Equal(ItemTree.Walk(items).Select(i => (i.GetType(), i.Name)), all.Select(i => (i.GetType(), i.Name)));
        // 1.x never read the ids, so it made its own.
        Assert.DoesNotContain(all, i => ItemTree.Walk(items).Any(o => o.Guid == i.Guid));
    }

    [Fact]
    public void Donut_OddWidth_Matches1x()
    {
        // The fixture holds Width 101, Height 101, then Radius 50 (101 / 2). XmlSerializer sets properties in element
        // order, so the Radius setter runs last and makes Width = Height = 2 * 50 = 100, as 1.x loading did.
        var raw = XDocument.Load(FixturePath(V1Format.ItemsFixture));
        var element = ItemElements(raw, "DonutDisplayItem").Single(e => (string?)e.Element("Name") == "Odd Donut");
        Assert.Equal(["101", "101", "50"], new[] { "Width", "Height", "Radius" }.Select(n => element.Element(n)!.Value));

        var items = ReadFixtureItems();
        var odd = items.OfType<DonutDisplayItem>().Single(d => d.Name == "Odd Donut");
        Assert.Equal((100, 100, 50), (odd.Width, odd.Height, odd.Radius));

        var even = items.OfType<DonutDisplayItem>().Single(d => d.Name == "GPU Temp Donut");
        Assert.Equal((120, 120, 60), (even.Width, even.Height, even.Radius));

        // The 1.x reader gives the same numbers.
        var v1Odd = V1Format.ReadItems(FixturePath(V1Format.ItemsFixture)).OfType<DonutDisplayItem>().Single(d => d.Name == "Odd Donut");
        Assert.Equal((odd.Width, odd.Height, odd.Radius), (v1Odd.Width, v1Odd.Height, v1Odd.Radius));
    }

    [Fact]
    public void ReadItems_SkipsUnknownItemTypes()
    {
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <ArrayOfDisplayItem xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
              <DisplayItem xsi:type="GuideDisplayItem">
                <Name>Top guide</Name>
                <X>100</X>
                <Length>200</Length>
                <Color>#FF00FFFF</Color>
              </DisplayItem>
              <DisplayItem xsi:type="GroupDisplayItem">
                <Name>Group</Name>
                <DisplayItems>
                  <DisplayItem xsi:type="GuideDisplayItem">
                    <Name>Grouped guide</Name>
                  </DisplayItem>
                  <DisplayItem xsi:type="TextDisplayItem">
                    <Name>Label</Name>
                    <FontSize>21</FontSize>
                  </DisplayItem>
                </DisplayItems>
              </DisplayItem>
              <DisplayItem xsi:type="ImageDisplayItem">
                <Name>Feed</Name>
                <Type>URL</Type>
                <HttpUrl>https://example.com/feed.png</HttpUrl>
                <RefreshInterval>30</RefreshInterval>
                <Scale>80</Scale>
              </DisplayItem>
            </ArrayOfDisplayItem>
            """;

        var items = ProfileSerializer.ReadItems(new StringReader(xml));

        Assert.Equal(["Group", "Feed"], items.Select(i => i.Name));
        var group = Assert.IsType<GroupDisplayItem>(items[0]);
        var label = Assert.IsType<TextDisplayItem>(Assert.Single(group.DisplayItems));
        Assert.Equal(21, label.FontSize);
        var image = Assert.IsType<ImageDisplayItem>(items[1]);
        Assert.Equal("https://example.com/feed.png", image.HttpUrl);
        Assert.Equal(80, image.Scale);
    }

    [Fact]
    public void ReadItems_KeepsTypedWrapperAndPropertyElements()
    {
        // xsi:type on elements that are not at an item position is not an item type, whatever it names.
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <ArrayOfDisplayItem xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
              <DisplayItem xsi:type="GroupDisplayItem">
                <Name>Group</Name>
                <DisplayItems xsi:type="ArrayOfDisplayItem">
                  <DisplayItem xsi:type="TextDisplayItem">
                    <Name xsi:type="string">Label</Name>
                  </DisplayItem>
                  <DisplayItem xsi:type="GuideDisplayItem">
                    <Name>Grouped guide</Name>
                  </DisplayItem>
                </DisplayItems>
              </DisplayItem>
              <DisplayItem xsi:type="GaugeDisplayItem">
                <Name>Gauge</Name>
                <Images>
                  <ImageDisplayItem>
                    <Name>Frame 0</Name>
                  </ImageDisplayItem>
                  <ImageDisplayItem xsi:type="GuideDisplayItem">
                    <Name>Not a frame</Name>
                  </ImageDisplayItem>
                  <ImageDisplayItem xsi:type="SensorImageDisplayItem">
                    <Name>Frame 1</Name>
                  </ImageDisplayItem>
                </Images>
              </DisplayItem>
            </ArrayOfDisplayItem>
            """;

        var items = ProfileSerializer.ReadItems(new StringReader(xml));

        Assert.Equal(["Group", "Gauge"], items.Select(i => i.Name));
        var group = Assert.IsType<GroupDisplayItem>(items[0]);
        Assert.Equal("Label", Assert.IsType<TextDisplayItem>(Assert.Single(group.DisplayItems)).Name);
        var gauge = Assert.IsType<GaugeDisplayItem>(items[1]);
        Assert.Equal(["Frame 0", "Frame 1"], gauge.Images.Select(i => i.Name));
        Assert.IsType<SensorImageDisplayItem>(gauge.Images[1]);
    }

    [Fact]
    public void ReadItems_DropsNilItemsAtEveryLevel()
    {
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <ArrayOfDisplayItem xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
              <DisplayItem xsi:nil="true" />
              <DisplayItem xsi:type="GroupDisplayItem">
                <Name>Group</Name>
                <DisplayItems>
                  <DisplayItem xsi:nil="true" />
                  <DisplayItem xsi:type="TextDisplayItem">
                    <Name>Label</Name>
                  </DisplayItem>
                </DisplayItems>
              </DisplayItem>
              <DisplayItem xsi:type="GaugeDisplayItem">
                <Name>Gauge</Name>
                <Images>
                  <ImageDisplayItem xsi:nil="true" />
                  <ImageDisplayItem>
                    <Name>Frame 0</Name>
                  </ImageDisplayItem>
                </Images>
              </DisplayItem>
            </ArrayOfDisplayItem>
            """;

        var items = ProfileSerializer.ReadItems(new StringReader(xml));

        Assert.Equal(["Group", "Gauge"], items.Select(i => i.Name));
        Assert.Equal(["Label"], Assert.IsType<GroupDisplayItem>(items[0]).DisplayItems.Select(i => i.Name));
        Assert.Equal(["Frame 0"], Assert.IsType<GaugeDisplayItem>(items[1]).Images.Select(i => i.Name));
        Assert.All(ItemTree.Walk(items), item => Assert.NotEqual(Guid.Empty, item.Guid));
    }

    [Fact]
    public void ReadItems_UntypedItem_IsLeftToXmlSerializer()
    {
        // No xsi:type at an item position is the abstract base type: the pre-pass keeps it and XmlSerializer rejects it.
        const string xml = """
            <ArrayOfDisplayItem xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
              <DisplayItem><Name>Untyped</Name></DisplayItem>
            </ArrayOfDisplayItem>
            """;

        Assert.Throws<InvalidDataException>(() => ProfileSerializer.ReadItems(new StringReader(xml)));
    }

    /// <summary>One sensor item as a build with another sensor source writes it: its HWiNFO ids and a Libre id beside it.</summary>
    private static string ForkSensorItem(string sensorType) => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <ArrayOfDisplayItem xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
          <DisplayItem xsi:type="SensorDisplayItem">
            <Name>Chip temp</Name>
            <X>12</X>
            <Y>34</Y>
            <SensorName>temp1</SensorName>
            <SensorType>{sensorType}</SensorType>
            <HwInfoRemoteIndex>-1</HwInfoRemoteIndex>
            <Id>4026532608</Id>
            <Instance>1</Instance>
            <EntryId>16777216</EntryId>
            <LibreSensorId>chip0/temp1</LibreSensorId>
            <PluginSensorId />
            <Unit>°C</Unit>
            <Font>Consolas</Font>
            <FontSize>21</FontSize>
          </DisplayItem>
        </ArrayOfDisplayItem>
        """;

    [Fact]
    public void ReadItems_AnUnknownSensorType_ReadsTheItemUnbound_WithItsLook()
    {
        // A value outside the enum (another build's source) failed the whole list; now only that item's binding goes.
        var items = ProfileSerializer.ReadItems(new StringReader(ForkSensorItem("Hwmon")));

        var item = Assert.IsType<SensorDisplayItem>(Assert.Single(items));
        // Unbound as a new 1.x item is: HWiNFO with no sensor id.
        Assert.Equal((SensorType.HwInfo, 0u, 0u, 0u), (item.SensorType, item.Id, item.Instance, item.EntryId));
        Assert.False(SensorHub.IsWellFormed(SensorKeys.Of(item)!.Value));
        // Everything else as stored.
        Assert.Equal(("Chip temp", 12, 34, "temp1", -1, "chip0/temp1"), (item.Name, item.X, item.Y, item.SensorName, item.HwInfoRemoteIndex, item.LibreSensorId));
        Assert.Equal(("°C", "Consolas", 21), (item.Unit, item.Font, item.FontSize));
    }

    private const string GroupedMultiLineSensorType = """
        <ArrayOfDisplayItem xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
          <DisplayItem xsi:type="GroupDisplayItem">
            <Name>Box</Name>
            <DisplayItems>
              <DisplayItem xsi:type="SensorDisplayItem"><Name>Chip temp</Name><SensorType>
                Hwmon
              </SensorType><Id>7</Id></DisplayItem>
            </DisplayItems>
          </DisplayItem>
        </ArrayOfDisplayItem>
        """;

    [Fact]
    public void ReadItems_WithoutTheUnknownSensorTypeList_ReportsTheValueAsAnIgnoredSensorTypeElement()
    {
        // A caller outside the 1.x import (no list given) still learns of the ignored value, as an unknown element.
        var report = new XmlReadReport();
        var items = ProfileSerializer.ReadItems(SafeXml.Load(ForkSensorItem("Hwmon")), report);

        var item = Assert.Single(items);
        var node = Assert.Single(report.Unknown);
        Assert.Equal(("SensorType", false, "Hwmon", 8), (node.Name, node.IsAttribute, node.Text, node.Line));
        Assert.Same(item, node.Owner);

        // Given the list, the import reports it itself: nothing doubled in the report.
        var listed = new List<UnknownSensorType>();
        var otherReport = new XmlReadReport();
        ProfileSerializer.ReadItems(SafeXml.Load(ForkSensorItem("Hwmon")), otherReport, listed);
        Assert.Empty(otherReport.Unknown);
        Assert.Equal(["Hwmon"], Assert.Single(listed).Values);
    }

    [Fact]
    public void ReadItems_AMultiLineUnknownSensorTypeInAGroup_IsReportedAtTheLineItStarts()
    {
        // The serializer finishes the element on line 7; the report says where it is in the file: line 5.
        var listed = new List<UnknownSensorType>();
        ProfileSerializer.ReadItems(SafeXml.Load(GroupedMultiLineSensorType), new XmlReadReport(), listed);
        Assert.Equal(5, Assert.Single(listed).Line);

        var report = new XmlReadReport();
        ProfileSerializer.ReadItems(SafeXml.Load(GroupedMultiLineSensorType), report);
        Assert.Equal(5, Assert.Single(report.Unknown).Line);
    }

    [Fact]
    public void ReadItems_KnownSensorTypes_ReadAsTheSerializerAloneReadsThem_ByteForByte()
    {
        // Every known value on every kind that has a sensor type, inside a group too: the pre-pass changes nothing.
        var kinds = new[] { "SensorDisplayItem", "GaugeDisplayItem", "GraphDisplayItem", "BarDisplayItem", "DonutDisplayItem",
            "SensorImageDisplayItem", "HttpImageDisplayItem", "TableSensorDisplayItem" };
        var next = 0;
        string Elements() => string.Concat(kinds.SelectMany(kind => new[] { "HwInfo", "Libre", "Plugin" }.Select(type =>
            $"""<DisplayItem xsi:type="{kind}"><Name>{kind} {type}</Name><Guid>{new Guid(++next, 0, 0, new byte[8])}</Guid><SensorType>{type}</SensorType><Id>4026532608</Id><Instance>1</Instance><EntryId>16777216</EntryId><LibreSensorId>/cpu/0/load/0</LibreSensorId><PluginSensorId>/p/fps</PluginSensorId></DisplayItem>""")));
        var xml = $"""<ArrayOfDisplayItem xmlns:xsi="{Xsi}">{Elements()}<DisplayItem xsi:type="GroupDisplayItem"><Name>Box</Name><Guid>{new Guid(++next, 0, 0, new byte[8])}</Guid><DisplayItems>{Elements()}</DisplayItems></DisplayItem></ArrayOfDisplayItem>""";

        var document = XDocument.Parse(xml);
        var untouched = new XDocument(document);
        var read = ProfileSerializer.ReadItems(document, report: null);
        var alone = (List<DisplayItem>)new XmlSerializer(typeof(List<DisplayItem>), [.. ProfileSerializer.ItemTypes]).Deserialize(XDocument.Parse(xml).CreateReader())!;

        Assert.True(XNode.DeepEquals(untouched, document));
        Assert.Equal(Write(s => ProfileSerializer.WriteItems(s, alone)), Write(s => ProfileSerializer.WriteItems(s, read)));
    }

    [Fact]
    public void ReadItems_Malformed_ThrowsInvalidData()
    {
        Assert.Throws<InvalidDataException>(() => ProfileSerializer.ReadItems(new StringReader("<ArrayOfDisplayItem><DisplayItem")));
        Assert.Throws<InvalidDataException>(() => ProfileSerializer.ReadProfiles(new MemoryStream("not xml"u8.ToArray())));
    }

    private static void AssertSameFraming(byte[] expected, byte[] actual)
    {
        Assert.Equal(expected.Take(3), actual.Take(3)); // UTF-8 BOM
        var expectedText = Encoding.UTF8.GetString(expected);
        var actualText = Encoding.UTF8.GetString(actual);
        Assert.Equal(expectedText[..expectedText.IndexOf('\r')], actualText[..actualText.IndexOf('\r')]);
        Assert.DoesNotContain("\n", actualText.Replace("\r\n", ""));
    }

    private static IEnumerable<XElement> ItemElements(XDocument document, string type) =>
        document.Descendants().Where(e => (string?)e.Attribute(Xsi + "type") == type);

    private static void StripGuidsAndHttpImageUrl(XDocument document)
    {
        document.Descendants("Guid").ToList().ForEach(e => e.Remove());
        ItemElements(document, "HttpImageDisplayItem").Elements("HttpUrl").ToList().ForEach(e => e.Remove());
    }
}
