using InfoPanel.Models;
using InfoPanel.Persistence;
using Xunit;

namespace InfoPanel.Core.Tests;

public class ModelXmlTests
{
    /// <summary>
    /// The 14 item kinds still build an XmlSerializer and round-trip (full 1.x format checks arrive in M2).
    /// </summary>
    [Fact]
    public void AllKinds_SerializeAndDeserialize()
    {
        var profile = new Profile("Xml", 400, 200);
        var group = new GroupDisplayItem { Name = "Group" };
        group.DisplayItems.Add(new TextDisplayItem("Inner", profile));
        var gauge = new GaugeDisplayItem("Gauge", profile);
        gauge.Images.Add(new ImageDisplayItem("Frame", profile, "frame0.png", relativePath: true));

        List<DisplayItem> items =
        [
            group,
            new BarDisplayItem("Bar", profile),
            new GraphDisplayItem("Graph", profile, GraphDisplayItem.GraphType.HISTOGRAM),
            new DonutDisplayItem("Donut", profile),
            new TableSensorDisplayItem("Table", profile, "/p/t/e"),
            new SensorDisplayItem("Sensor", profile),
            new ClockDisplayItem("Clock", profile),
            new CalendarDisplayItem("Calendar", profile),
            new TextDisplayItem("Text", profile),
            new SensorImageDisplayItem("SensorImage", profile),
            new ImageDisplayItem("Image", profile),
            new HttpImageDisplayItem("HttpImage", profile),
            gauge,
            new ShapeDisplayItem("Shape", profile),
        ];

        var xml = ProfileSerializer.SnapshotItems(items);
        Assert.False(string.IsNullOrEmpty(xml));

        var restored = ProfileSerializer.ReadItems(new StringReader(xml));

        Assert.Equal(items.Select(i => i.GetType()), restored.Select(i => i.GetType()));
        Assert.Equal(items.Select(i => i.Name), restored.Select(i => i.Name));
        Assert.Equal("Inner", ((GroupDisplayItem)restored[0]).DisplayItems.Single().Name);
        Assert.Equal("frame0.png", ((GaugeDisplayItem)restored[12]).Images.Single().FilePath);
        Assert.DoesNotContain("<HttpUrl>", xml);
    }
}
