using System.Text;
using InfoPanel.Core.Tests.Persistence;
using InfoPanel.Import;
using InfoPanel.Persistence;
using Xunit;

namespace InfoPanel.Core.Tests.Import;

/// <summary>
/// The XML guards every 1.x file is read through: no DTD (so no entity of any kind), no resolver, and the depth and
/// element bounds checked in a streaming pre-pass before a document is built.
/// </summary>
public class SafeXmlTests
{
    private static XmlRefusedException Refused(string xml, XmlLimits? limits = null) =>
        Assert.Throws<XmlRefusedException>(() => SafeXml.Load(Encoding.UTF8.GetBytes(xml), limits));

    [Fact]
    public void Dtd_IsRefused_EvenWithoutEntities()
    {
        var error = Refused("<!DOCTYPE ArrayOfDisplayItem []>\n<ArrayOfDisplayItem />");

        Assert.Contains("DTD", error.Message);
    }

    [Fact]
    public void EntityBomb_IsRefusedAtItsDtd()
    {
        // About 30 million characters if it were expanded.
        var xml = new StringBuilder("<?xml version=\"1.0\"?>\n<!DOCTYPE lolz [\n <!ENTITY lol \"lol\">\n");
        for (var i = 1; i <= 7; i++)
            xml.Append($" <!ENTITY lol{i} \"{string.Concat(Enumerable.Repeat($"&lol{(i == 1 ? "" : (i - 1).ToString())};", 10))}\">\n");
        xml.Append("]>\n<ArrayOfDisplayItem><DisplayItem>&lol7;</DisplayItem></ArrayOfDisplayItem>");

        var error = Refused(xml.ToString());

        Assert.Contains("DTD", error.Message);
    }

    [Fact]
    public void ExternalEntity_IsRefused_AndItsFileIsNeverRead()
    {
        using var temp = new TempDirectory();
        var sentinel = temp.Combine("sentinel.txt");
        File.WriteAllText(sentinel, "SENTINEL-CONTENT");
        var xml = $"""
            <?xml version="1.0"?>
            <!DOCTYPE Profile [ <!ENTITY secret SYSTEM "{new Uri(sentinel).AbsoluteUri}"> ]>
            <Profile><Name>&secret;</Name></Profile>
            """;

        var error = Refused(xml);

        Assert.Contains("DTD", error.Message);
        Assert.DoesNotContain("SENTINEL", error.ToString());
        // Through the serializer too: the profile is never read, so its name can't carry the file.
        var profileError = Assert.Throws<InvalidDataException>(() => ProfileSerializer.ReadProfile(new MemoryStream(Encoding.UTF8.GetBytes(xml))));
        Assert.Contains("DTD", profileError.Message);
    }

    [Fact]
    public void Depth_IsBounded_AtTheLimit()
    {
        static string Nested(int depth) => string.Concat(Enumerable.Repeat("<a>", depth)) + string.Concat(Enumerable.Repeat("</a>", depth));

        Assert.NotNull(SafeXml.Load(Encoding.UTF8.GetBytes(Nested(64))));
        var error = Refused(Nested(65));
        Assert.Contains("deeper than 64", error.Message);

        // The limit is an option.
        Assert.Contains("deeper than 3", Refused(Nested(4), new XmlLimits(3, 100)).Message);
    }

    [Fact]
    public void ElementCount_IsBounded_AtTheLimit()
    {
        static string Flat(int elements) => "<r>" + string.Concat(Enumerable.Repeat("<e/>", elements - 1)) + "</r>";

        Assert.NotNull(SafeXml.Load(Encoding.UTF8.GetBytes(Flat(500_000))));
        var error = Refused(Flat(500_001));
        Assert.Contains("more than 500000 elements", error.Message);
    }

    [Fact]
    public void Malformed_IsRefused_WithItsLine()
    {
        var error = Refused("<r>\n<a>\n</r>");

        Assert.Equal(3, error.Line);
    }

    [Fact]
    public void Document_KeepsLineInformation()
    {
        var document = SafeXml.Load(Encoding.UTF8.GetBytes("<r>\n  <a />\n</r>"));

        Assert.Equal(2, SafeXml.LineOf(document.Root!.Elements().Single()));
    }

    [Fact]
    public void Stream_IsReadUpToItsByteCap()
    {
        // A shallow document with few elements is bounded only by its bytes.
        var xml = Encoding.UTF8.GetBytes("<Profile><Name>" + new string('n', 1000) + "</Name></Profile>");

        Assert.Equal("Profile", SafeXml.Load(new MemoryStream(xml), maxBytes: xml.Length).Root!.Name.LocalName);
        var error = Assert.Throws<XmlRefusedException>(() => SafeXml.Load(new MemoryStream(xml), maxBytes: xml.Length - 1));
        Assert.Contains($"larger than {xml.Length - 1} bytes", error.Message);
    }

    [Fact]
    public void ProfileSerializer_ReadsThroughTheGuards()
    {
        // The step-1 readers (files and snapshots) refuse what the import refuses.
        const string dtd = "<!DOCTYPE ArrayOfDisplayItem []><ArrayOfDisplayItem />";
        Assert.Throws<InvalidDataException>(() => ProfileSerializer.ReadItems(new StringReader(dtd)));
        Assert.Throws<InvalidDataException>(() => ProfileSerializer.ReadItems(new MemoryStream(Encoding.UTF8.GetBytes(dtd))));
        Assert.Throws<InvalidDataException>(() => ProfileSerializer.ReadProfiles(new MemoryStream(Encoding.UTF8.GetBytes("<!DOCTYPE x []><ArrayOfProfile />"))));

        var deep = "<ArrayOfDisplayItem>" + string.Concat(Enumerable.Repeat("<x>", 70)) + string.Concat(Enumerable.Repeat("</x>", 70)) + "</ArrayOfDisplayItem>";
        Assert.Throws<InvalidDataException>(() => ProfileSerializer.ReadItems(new StringReader(deep)));
    }
}
