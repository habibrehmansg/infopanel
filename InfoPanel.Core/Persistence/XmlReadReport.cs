using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Serialization;
using InfoPanel.Import;
using InfoPanel.Models;

namespace InfoPanel.Persistence;

/// <summary>
/// An item element <see cref="ProfileSerializer.ReadItems(XDocument, XmlReadReport)"/> left out: of a type outside the
/// 14 kinds (<see cref="Type"/>, its <c>xsi:type</c> local name), or empty (<c>xsi:nil</c>, <see cref="Type"/> null).
/// <see cref="Container"/> is the JSON pointer of the item list it was in, relative to the items: <c>""</c> at the top
/// level, <c>/2/children</c> in a group, <c>/3/images</c> among a gauge's frames; null when it can't be told.
/// <see cref="Element"/> is the element as it was read (detached from the document).
/// </summary>
public sealed record SkippedItem(string? Type, string? Container, int? Line, XElement Element);

/// <summary>
/// An element or attribute the serializer didn't know and ignored. <see cref="Owner"/> is the object it was read into
/// (an item, a profile), when the serializer says.
/// </summary>
public sealed record UnknownXmlNode(string Name, bool IsAttribute, int? Line, object? Owner);

/// <summary>
/// What a read of 1.x XML through <see cref="ProfileSerializer"/> left out or ignored, with positions and lines, for an
/// importer's diagnostics. One report per read.
/// </summary>
public sealed class XmlReadReport
{
    private readonly List<(XElement Element, string Type, XElement? Parent)> _dropped = [];
    private readonly Dictionary<XElement, string> _elementPaths = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<DisplayItem, (string Path, int? Line)> _items = new(ReferenceEqualityComparer.Instance);
    private readonly List<SkippedItem> _skipped = [];

    /// <summary>Item elements left out, in the order they were met.</summary>
    public IReadOnlyList<SkippedItem> Skipped => _skipped;

    /// <summary>Unknown elements and attributes, in the order the serializer met them.</summary>
    public List<UnknownXmlNode> Unknown { get; } = [];

    /// <summary>
    /// Whether every item list's elements matched the list read from them one for one, so every path in this report is
    /// exact. False leaves the lists that didn't match without paths.
    /// </summary>
    public bool PathsExact { get; private set; } = true;

    /// <summary>The JSON pointer of <paramref name="item"/> relative to the items (<c>/0</c>, <c>/2/children/1</c>), or null.</summary>
    public string? PathOf(DisplayItem item) => _items.TryGetValue(item, out var found) ? found.Path : null;

    /// <summary>The line of <paramref name="item"/>'s element, or null.</summary>
    public int? LineOf(DisplayItem item) => _items.TryGetValue(item, out var found) ? found.Line : null;

    internal void Dropped(XElement element, string type, XElement? parent) => _dropped.Add((element, type, parent));

    private const string XsiNamespace = "http://www.w3.org/2001/XMLSchema-instance", XmlnsNamespace = "http://www.w3.org/2000/xmlns/";

    internal XmlDeserializationEvents Events() => new()
    {
        OnUnknownElement = (_, e) => Unknown.Add(new UnknownXmlNode(e.Element.Name, false, e.LineNumber > 0 ? e.LineNumber : null, e.ObjectBeingDeserialized)),
        // The serializer passes xsi:type and xsi:nil (which it reads) and namespace declarations on as unknown attributes.
        OnUnknownAttribute = (_, e) =>
        {
            if (e.Attr.NamespaceURI is not (XsiNamespace or XmlnsNamespace))
                Unknown.Add(new UnknownXmlNode(e.Attr.Name, true, e.LineNumber > 0 ? e.LineNumber : null, e.ObjectBeingDeserialized));
        },
    };

    /// <summary>
    /// Matches the item elements left in <paramref name="root"/> with the lists read from them (nulls for <c>xsi:nil</c>
    /// still in place), giving each item its path and line and recording the nil items; then resolves the containers of
    /// the dropped elements.
    /// </summary>
    internal void Map(XElement root, IList items)
    {
        MapList(root.Elements(nameof(DisplayItem)), items, "");

        foreach (var (element, type, parent) in _dropped)
        {
            string? container = parent is null ? ""
                : _elementPaths.TryGetValue(parent, out var parentPath) ? parentPath + (IsGauge(parent) ? "/images" : "/children")
                : null;
            _skipped.Add(new SkippedItem(type, container, SafeXml.LineOf(element), element));
        }
    }

    private void MapList(IEnumerable<XElement> elements, IList list, string container)
    {
        var positions = elements.ToList();
        if (positions.Count != list.Count)
        {
            PathsExact = false;
            return;
        }

        var index = 0;
        for (var i = 0; i < positions.Count; i++)
        {
            var element = positions[i];
            var line = SafeXml.LineOf(element);
            if (list[i] is not DisplayItem item)
            {
                _skipped.Add(new SkippedItem(null, container, line, element));
                continue;
            }

            var path = container + "/" + index++;
            _elementPaths[element] = path;
            _items[item] = (path, line);
            switch (item)
            {
                case GroupDisplayItem group:
                    MapList(element.Elements(nameof(GroupDisplayItem.DisplayItems)).Elements(nameof(DisplayItem)), group.DisplayItems, path + "/children");
                    break;
                case GaugeDisplayItem gauge:
                    MapList(element.Elements(nameof(GaugeDisplayItem.Images)).Elements(nameof(ImageDisplayItem)), gauge.Images, path + "/images");
                    break;
            }
        }
    }

    private static bool IsGauge(XElement element) =>
        element.Attribute(XName.Get("type", "http://www.w3.org/2001/XMLSchema-instance"))?.Value is { } type
        && type[(type.IndexOf(':') + 1)..] == nameof(GaugeDisplayItem);
}
