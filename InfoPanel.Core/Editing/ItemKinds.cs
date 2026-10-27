using InfoPanel.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text.Json.Nodes;

namespace InfoPanel.Editing;

/// <summary>An item kind: the API's <see cref="Id"/> (<c>text</c>, <c>sensorImage</c>, ...), its model type and a label.</summary>
public sealed record ItemKind(string Id, Type ClrType, string Label);

/// <summary>A property in the kind manifest.</summary>
public sealed record PropertyManifest(string Name, PropertyJsonType Type, IReadOnlyList<string>? EnumValues, bool ReadOnly);

/// <summary>One 1.x kind in the kind manifest (<c>Snapshots/kinds.json</c>): its properties and the values of a fresh item.</summary>
public sealed record KindManifest(string Kind, string ClrType, string Label, IReadOnlyList<PropertyManifest> Properties, JsonObject Defaults);

/// <summary>The 14 item kinds (plan §6.5), matched by exact model type.</summary>
public static class ItemKinds
{
    public static IReadOnlyList<ItemKind> All { get; } =
    [
        new("text", typeof(TextDisplayItem), "Text"),
        new("sensor", typeof(SensorDisplayItem), "Sensor"),
        new("clock", typeof(ClockDisplayItem), "Clock"),
        new("calendar", typeof(CalendarDisplayItem), "Calendar"),
        new("bar", typeof(BarDisplayItem), "Bar"),
        new("graph", typeof(GraphDisplayItem), "Graph"),
        new("donut", typeof(DonutDisplayItem), "Donut"),
        new("shape", typeof(ShapeDisplayItem), "Shape"),
        new("group", typeof(GroupDisplayItem), "Group"),
        new("table", typeof(TableSensorDisplayItem), "Table"),
        new("image", typeof(ImageDisplayItem), "Image"),
        new("sensorImage", typeof(SensorImageDisplayItem), "Sensor Image"),
        new("httpImage", typeof(HttpImageDisplayItem), "HTTP Image"),
        new("gauge", typeof(GaugeDisplayItem), "Gauge"),
    ];

    private static readonly Dictionary<Type, ItemKind> ByType = All.ToDictionary(k => k.ClrType);
    private static readonly Dictionary<string, ItemKind> ById = All.ToDictionary(k => k.Id, StringComparer.Ordinal);

    public static ItemKind Of(DisplayItem item) =>
        ByType.TryGetValue(item.GetType(), out var kind)
            ? kind
            : throw new NotSupportedException($"{item.GetType().Name} is not an item kind.");

    public static bool TryGet(string id, [NotNullWhen(true)] out ItemKind? kind) => ById.TryGetValue(id, out kind);

    /// <summary>The manifest of every kind; <c>defaults</c> are read from a new instance of the kind.</summary>
    public static IReadOnlyList<KindManifest> BuildManifest() =>
        All.Select(kind =>
        {
            var map = PropertyMap.For(kind.ClrType);
            var fresh = Activator.CreateInstance(kind.ClrType)!;
            return new KindManifest(
                kind.Id,
                kind.ClrType.Name,
                kind.Label,
                map.Properties.Select(p => new PropertyManifest(p.Name, p.Type, p.EnumValues, p.ReadOnly)).ToList(),
                map.Read(fresh));
        }).ToList();
}
