using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Serialization;
using InfoPanel.Models;
using Serilog;

namespace InfoPanel.Persistence;

/// <summary>
/// Reads and writes the 1.x XML formats: a profile's items (<c>ArrayOfDisplayItem</c>), the profile list
/// (<c>ArrayOfProfile</c>) and a single profile (<c>Profile</c>, the archive's <c>Profile.xml</c>).
/// The serializers are built once: 1.x rebuilt them on every call, which leaks a dynamic assembly each time.
/// Output matches 1.x byte for byte apart from the item <c>&lt;Guid&gt;</c> element: UTF-8 with a BOM, indented,
/// CRLF line ends (1.x only ran on Windows). XmlSerializer reads and writes numbers and dates invariantly.
/// </summary>
public static class ProfileSerializer
{
    private static readonly ILogger Logger = Log.ForContext(typeof(ProfileSerializer));

    private static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";

    /// <summary>The 14 item types, in 1.x's <c>extraTypes</c> order.</summary>
    public static IReadOnlyList<Type> ItemTypes { get; } =
    [
        typeof(GroupDisplayItem), typeof(BarDisplayItem), typeof(GraphDisplayItem), typeof(DonutDisplayItem),
        typeof(TableSensorDisplayItem), typeof(SensorDisplayItem), typeof(ClockDisplayItem), typeof(CalendarDisplayItem),
        typeof(TextDisplayItem), typeof(SensorImageDisplayItem), typeof(ImageDisplayItem), typeof(HttpImageDisplayItem),
        typeof(GaugeDisplayItem), typeof(ShapeDisplayItem),
    ];

    private static readonly HashSet<string> KnownItemTypeNames = [.. ItemTypes.Select(t => t.Name)];

    private static readonly XmlSerializer ItemsSerializer = new(typeof(List<DisplayItem>), [.. ItemTypes]);
    /// <summary>
    /// <see cref="ItemsSerializer"/> for internal snapshots only (undo history, dirty state, revert, duplication):
    /// it leaves out <c>DonutDisplayItem.Radius</c>, a derived property whose setter forces <c>Width = Height = 2 ×
    /// Radius</c> and so squares a non-square donut when 1.x XML is read back. Every other model setter only sets its
    /// own field (or ignores a value), so this is the only override. Files keep the 1.x format, Radius included.
    /// </summary>
    private static readonly XmlSerializer SnapshotItemsSerializer = CreateSnapshotItemsSerializer();
    private static readonly XmlSerializer ProfilesSerializer = new(typeof(List<Profile>));
    private static readonly XmlSerializer SingleProfileSerializer = new(typeof(Profile));

    /// <summary>1.x's writer settings, with the Windows line end spelled out so every OS writes the same bytes.</summary>
    public static XmlWriterSettings CreateWriterSettings() =>
        new() { Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), Indent = true, NewLineChars = "\r\n" };

    /// <summary>
    /// Reads a profile's items and makes their ids unique (<see cref="ItemTree.EnsureUniqueIds"/>).
    /// Item elements whose <c>xsi:type</c> is not one of the 14 kinds (for example newer 1.4.x
    /// <c>GuideDisplayItem</c>s) are dropped at every item position (top level, group children, gauge frames), and
    /// <c>xsi:nil</c> items are removed, each with a warning; unknown property elements are ignored.
    /// Callers set the items' profile.
    /// </summary>
    /// <exception cref="InvalidDataException">The XML is malformed or not an item list.</exception>
    public static List<DisplayItem> ReadItems(Stream stream)
    {
        XDocument document;
        try
        {
            document = XDocument.Load(stream, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException ex)
        {
            throw new InvalidDataException("The items XML is malformed: " + ex.Message, ex);
        }
        return ReadItems(document);
    }

    /// <inheritdoc cref="ReadItems(Stream)"/>
    public static List<DisplayItem> ReadItems(TextReader reader)
    {
        XDocument document;
        try
        {
            document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException ex)
        {
            throw new InvalidDataException("The items XML is malformed: " + ex.Message, ex);
        }
        return ReadItems(document);
    }

    private static List<DisplayItem> ReadItems(XDocument document)
    {
        DropUnknownItemTypes(document);

        List<DisplayItem>? items;
        try
        {
            using var reader = document.CreateReader();
            items = ItemsSerializer.Deserialize(reader) as List<DisplayItem>;
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidDataException("The items XML could not be read: " + (ex.InnerException ?? ex).Message, ex);
        }
        if (items == null)
            throw new InvalidDataException("The items XML is not an item list.");

        RemoveNullItems(items);
        ItemTree.EnsureUniqueIds(items);
        return items;
    }

    /// <summary>
    /// Removes item elements whose <c>xsi:type</c> names a type outside the 14 kinds. Only item positions are looked
    /// at: children of the root list, of a group's <c>DisplayItems</c> and of a gauge's <c>Images</c>, so property and
    /// wrapper elements are left to XmlSerializer whatever their <c>xsi:type</c>. An item element without
    /// <c>xsi:type</c> is kept, and XmlSerializer reads it as its declared type.
    /// </summary>
    private static void DropUnknownItemTypes(XDocument document)
    {
        if (document.Root is not { } root)
            return;

        var dropped = new List<string>();
        DropUnknownItemTypes(root.Elements(), dropped);
        if (dropped.Count > 0)
            Logger.Warning("Skipped {Count} display items of unknown types {Types}", dropped.Count, dropped.Distinct().ToArray());
    }

    private static void DropUnknownItemTypes(IEnumerable<XElement> itemElements, List<string> dropped)
    {
        foreach (var element in itemElements.ToList())
        {
            var type = element.Attribute(Xsi + "type")?.Value;
            if (type == null)
                continue; // base declared type: a gauge frame image, or what XmlSerializer makes of an untyped item
            if (IsUnknownItemType(element, type))
            {
                dropped.Add(type);
                element.Remove();
                continue;
            }

            var children = LocalName(type) switch
            {
                nameof(GroupDisplayItem) => element.Elements(nameof(GroupDisplayItem.DisplayItems)).Elements(),
                nameof(GaugeDisplayItem) => element.Elements(nameof(GaugeDisplayItem.Images)).Elements(),
                _ => [],
            };
            DropUnknownItemTypes(children, dropped);
        }
    }

    private static string LocalName(string qualifiedName) => qualifiedName[(qualifiedName.IndexOf(':') + 1)..];

    /// <summary>
    /// Removes null entries (<c>xsi:nil="true"</c> item elements) from the list, group children and gauge frames,
    /// with a warning; the rest of the code assumes no item is null.
    /// </summary>
    private static void RemoveNullItems(List<DisplayItem> items)
    {
        var removed = RemoveNulls(items);
        if (removed > 0)
            Logger.Warning("Skipped {Count} empty (xsi:nil) display items", removed);

        static int RemoveNulls<T>(IList<T> list) where T : DisplayItem
        {
            var removed = 0;
            for (var i = list.Count - 1; i >= 0; i--)
            {
                switch (list[i])
                {
                    case null:
                        list.RemoveAt(i);
                        removed++;
                        break;
                    case GroupDisplayItem group:
                        removed += RemoveNulls(group.DisplayItems);
                        break;
                    case GaugeDisplayItem gauge:
                        removed += RemoveNulls(gauge.Images);
                        break;
                }
            }
            return removed;
        }
    }

    /// <summary>
    /// True for an <c>xsi:type</c> naming a type in no namespace (where XmlSerializer puts the item types)
    /// that is not one of the 14 kinds. <c>xsd:</c> primitives and the like are left to XmlSerializer.
    /// </summary>
    private static bool IsUnknownItemType(XElement element, string qualifiedName)
    {
        var colon = qualifiedName.IndexOf(':');
        var prefix = colon < 0 ? "" : qualifiedName[..colon];
        var localName = colon < 0 ? qualifiedName : qualifiedName[(colon + 1)..];
        var ns = prefix.Length == 0 ? element.GetDefaultNamespace() : element.GetNamespaceOfPrefix(prefix);
        return ns == XNamespace.None && !KnownItemTypeNames.Contains(localName);
    }

    public static void WriteItems(Stream stream, IEnumerable<DisplayItem> items)
    {
        using var writer = XmlWriter.Create(stream, CreateWriterSettings());
        ItemsSerializer.Serialize(writer, AsList(items));
    }

    /// <summary>
    /// The items as XML text, for undo snapshots, dirty checks and deep copies; <see cref="ReadItems(TextReader)"/>
    /// reads it back to the same values. Not the 1.x file format: it leaves out <c>DonutDisplayItem.Radius</c> (see
    /// <see cref="SnapshotItemsSerializer"/>), so a donut keeps its width and height. Files use <see cref="WriteItems"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">A string holds a character XML 1.0 can't carry.</exception>
    public static string SnapshotItems(IEnumerable<DisplayItem> items)
    {
        var settings = CreateWriterSettings();
        using var text = new StringWriter();
        using (var writer = XmlWriter.Create(text, settings))
            SnapshotItemsSerializer.Serialize(writer, AsList(items));
        return text.ToString();
    }

    private static XmlSerializer CreateSnapshotItemsSerializer()
    {
        var overrides = new XmlAttributeOverrides();
        overrides.Add(typeof(DonutDisplayItem), nameof(DonutDisplayItem.Radius), new XmlAttributes { XmlIgnore = true });
        return new XmlSerializer(typeof(List<DisplayItem>), overrides, [.. ItemTypes], null, null);
    }

    /// <exception cref="InvalidDataException">The XML is malformed or not a profile list.</exception>
    public static List<Profile> ReadProfiles(Stream stream) =>
        Deserialize<List<Profile>>(ProfilesSerializer, stream, "profile list");

    public static void WriteProfiles(Stream stream, IEnumerable<Profile> profiles)
    {
        using var writer = XmlWriter.Create(stream, CreateWriterSettings());
        ProfilesSerializer.Serialize(writer, profiles as List<Profile> ?? profiles.ToList());
    }

    /// <summary>Reads a single profile (the archive's <c>Profile.xml</c>).</summary>
    /// <exception cref="InvalidDataException">The XML is malformed or not a profile.</exception>
    public static Profile ReadProfile(Stream stream) =>
        Deserialize<Profile>(SingleProfileSerializer, stream, "profile");

    public static void WriteProfile(Stream stream, Profile profile)
    {
        using var writer = XmlWriter.Create(stream, CreateWriterSettings());
        SingleProfileSerializer.Serialize(writer, profile);
    }

    /// <summary>The profile as XML text, for undo snapshots and dirty checks. <see cref="ReadProfileSnapshot"/> reads it back.</summary>
    public static string SnapshotProfile(Profile profile)
    {
        using var text = new StringWriter();
        using (var writer = XmlWriter.Create(text, CreateWriterSettings()))
            SingleProfileSerializer.Serialize(writer, profile);
        return text.ToString();
    }

    /// <summary>Reads a profile written by <see cref="SnapshotProfile"/>.</summary>
    /// <exception cref="InvalidDataException">The XML is malformed or not a profile.</exception>
    public static Profile ReadProfileSnapshot(string xml)
    {
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml));
            return SingleProfileSerializer.Deserialize(reader) as Profile
                ?? throw new InvalidDataException("The profile XML is empty.");
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidDataException("The profile XML could not be read: " + (ex.InnerException ?? ex).Message, ex);
        }
    }

    private static T Deserialize<T>(XmlSerializer serializer, Stream stream, string what) where T : class
    {
        try
        {
            using var reader = XmlReader.Create(stream);
            return serializer.Deserialize(reader) as T
                ?? throw new InvalidDataException($"The {what} XML is empty.");
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidDataException($"The {what} XML could not be read: " + (ex.InnerException ?? ex).Message, ex);
        }
        catch (XmlException ex)
        {
            throw new InvalidDataException($"The {what} XML is malformed: " + ex.Message, ex);
        }
    }

    private static List<DisplayItem> AsList(IEnumerable<DisplayItem> items) => items as List<DisplayItem> ?? items.ToList();
}
