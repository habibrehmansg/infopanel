using InfoPanel.Models;
using InfoPanel.Sensors;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace InfoPanel.Editing;

/// <summary>An item's sensor binding and where its value comes from.</summary>
public sealed record SensorMeta(string Key, SensorStatus Status);

public sealed record ItemMeta(SensorMeta? Sensor);

/// <summary>
/// An item as the API sends it (plan §6.2). Properties are nested under <see cref="Props"/> because
/// <c>SensorDisplayItem.Id</c> would otherwise clash with the item <see cref="Id"/>.
/// </summary>
public sealed record ItemNode(
    string Id,
    string Kind,
    JsonObject Props,
    IReadOnlyList<ItemNode>? Children,
    IReadOnlyList<ItemNode>? Images,
    ItemMeta Meta);

/// <summary>Converts items and profiles to their API JSON. M5 adds <c>Apply</c> (patching).</summary>
public static class ItemJson
{
    /// <summary>
    /// The item's node: <c>children</c> for groups, <c>images</c> (read-only) for gauges, and
    /// <c>meta.sensor</c> when the item is bound to a sensor.
    /// </summary>
    public static ItemNode ToNode(DisplayItem item, SensorHub sensors)
    {
        var kind = ItemKinds.Of(item);
        var props = PropertyMap.For(item.GetType()).Read(item);

        IReadOnlyList<ItemNode>? children = item is GroupDisplayItem group
            ? group.DisplayItems.Select(child => ToNode(child, sensors)).ToList()
            : null;

        IReadOnlyList<ItemNode>? images = item is GaugeDisplayItem gauge
            ? gauge.Images.Select(image => ToNode(image, sensors)).ToList()
            : null;

        SensorMeta? sensor = SensorKeys.Of(item) is SensorKey key
            ? new SensorMeta(key.ToString(), sensors.GetStatus(key))
            : null;

        return new ItemNode(item.Guid.ToString(), kind.Id, props, children, images, new ItemMeta(sensor));
    }

    /// <summary>The profile's properties (camelCase; <c>Guid</c> is the document's <c>id</c>).</summary>
    public static JsonObject ProfileProps(Profile profile) => PropertyMap.For(typeof(Profile)).Read(profile);

    /// <summary><c>SensorKeys.ToBindingProps</c> with camelCase names and JSON values, as item <c>props</c> use them.</summary>
    public static JsonObject BindingProps(SensorKey key, string sensorName)
    {
        var result = new JsonObject();
        foreach (var (name, value) in SensorKeys.ToBindingProps(key, sensorName))
            result[System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(name)] = PropertyMap.ValueToJson(value);
        return result;
    }
}
