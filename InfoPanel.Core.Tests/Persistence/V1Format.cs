using System.Text;
using System.Xml;
using System.Xml.Serialization;
using InfoPanel.Models;

namespace InfoPanel.Core.Tests.Persistence;

/// <summary>
/// The 1.x XML format as 1.x wrote it (<c>SharedModel.SaveDisplayItems</c>, <c>ConfigModel.SaveProfiles</c>),
/// emulated here so the frozen fixtures do not depend on the 2.0 serializer.
/// </summary>
internal static class V1Format
{
    /// <summary>The 14 extra types, in 1.x order.</summary>
    public static readonly Type[] ExtraTypes =
    [
        typeof(GroupDisplayItem), typeof(BarDisplayItem), typeof(GraphDisplayItem), typeof(DonutDisplayItem),
        typeof(TableSensorDisplayItem), typeof(SensorDisplayItem), typeof(ClockDisplayItem), typeof(CalendarDisplayItem),
        typeof(TextDisplayItem), typeof(SensorImageDisplayItem), typeof(ImageDisplayItem), typeof(HttpImageDisplayItem),
        typeof(GaugeDisplayItem), typeof(ShapeDisplayItem),
    ];

    public static readonly XmlSerializer ItemsSerializer = new(typeof(List<DisplayItem>), ExtraTypes);

    public static readonly XmlSerializer ProfilesSerializer = new(typeof(List<Profile>));

    public const string ItemsFixture = "Fixtures/v1/all-kinds.items.xml";

    public const string ProfilesFixture = "Fixtures/v1/profiles.xml";

    /// <summary>
    /// 1.x's writer settings. 1.x only ran on Windows, where the default new line is CRLF; it is spelled
    /// out so the fixture has the same bytes whichever OS regenerates it.
    /// </summary>
    public static XmlWriterSettings WriterSettings() =>
        new() { Encoding = Encoding.UTF8, Indent = true, NewLineChars = "\r\n" };

    public static List<DisplayItem> ReadItems(string path)
    {
        using var reader = XmlReader.Create(path);
        return (List<DisplayItem>)ItemsSerializer.Deserialize(reader)!;
    }

    public static List<Profile> ReadProfiles(string path)
    {
        using var reader = XmlReader.Create(path);
        return (List<Profile>)ProfilesSerializer.Deserialize(reader)!;
    }
}
