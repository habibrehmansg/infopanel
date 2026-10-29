using InfoPanel.Models;
using InfoPanel.Sensors;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using InfoPanel.Spec;

namespace InfoPanel.Editing;

/// <summary>An item's sensor binding and where its value comes from.</summary>
[SpecDef("urn:infopanel:api:1#/$defs/v1SensorMeta")]
public sealed record SensorMeta(string Key, SensorStatus Status);

/// <summary>What the engine adds to an item: its sensor binding as it reads it now, when it has one.</summary>
[SpecDef("urn:infopanel:api:1#/$defs/v1ItemMeta")]
public sealed record ItemMeta(SensorMeta? Sensor = null);

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
    ItemMeta Meta)
{
    /// <summary>Reads a node: <c>children</c> (a group's) and <c>images</c> (a gauge's) may be absent.</summary>
    [JsonConstructor]
    public ItemNode(string id, string kind, JsonObject props, ItemMeta meta, IReadOnlyList<ItemNode>? children = null, IReadOnlyList<ItemNode>? images = null)
        : this(id, kind, props, children, images, meta)
    {
    }
}

/// <summary>Converts items and profiles to and from their API JSON.</summary>
public static class ItemJson
{
    /// <summary>Keys that are part of the node, never of <c>props</c>, so a property set can never contain them.</summary>
    private static readonly HashSet<string> NodeKeys = ["kind", "children", "images", "meta", "props"];

    /// <summary>
    /// Sets the item's properties from <paramref name="set"/> (camelCase names, as in <c>props</c>). Every key is
    /// validated before anything is set, so a refused set changes nothing. Values go through the model setters, so
    /// 1.x clamping applies (a chart's <c>width</c> of 0 is ignored, a donut's span stays in 1–360); callers read the
    /// item back for the result.
    /// <para><c>kind</c>, <c>children</c>, <c>images</c> are never patchable. <c>id</c> is the item id, which never
    /// changes, except on kinds that have an <c>id</c> property (the HWiNFO sensor id of sensor-bound kinds, as
    /// <c>SensorKeys.ToBindingProps</c> writes it), where it sets that property.</para>
    /// </summary>
    /// <exception cref="PatchException">
    /// An unknown, read-only or never-patchable key, a value that doesn't fit, or one the model setter throws on (the
    /// keys before it are then already set: callers roll back).
    /// </exception>
    public static void Apply(DisplayItem item, JsonObject set) =>
        Apply(item, PropertyMap.For(item.GetType()), set, ItemKinds.Of(item).Id);

    /// <summary>Sets profile properties (camelCase, as in a document's <c>profile</c>), validated like <see cref="Apply(DisplayItem, JsonObject)"/>.</summary>
    /// <exception cref="PatchException">An unknown or read-only key (<c>id</c> included), or a value that doesn't fit.</exception>
    public static void ApplyProfile(Profile profile, JsonObject set) =>
        Apply(profile, PropertyMap.For(typeof(Profile)), set, "profile");

    /// <summary>Validates <paramref name="set"/> against <paramref name="item"/>'s kind without changing it.</summary>
    public static IReadOnlyList<(PropertyDescriptor Property, object? Value)> Validate(DisplayItem item, JsonObject set) =>
        Validate(PropertyMap.For(item.GetType()), set, ItemKinds.Of(item).Id);

    private static void Apply(object target, PropertyMap map, JsonObject set, string what)
    {
        foreach (var (property, value) in Validate(map, set, what))
            property.Write(target, value);
    }

    private static List<(PropertyDescriptor Property, object? Value)> Validate(PropertyMap map, JsonObject set, string what)
    {
        var values = new List<(PropertyDescriptor, object?)>(set.Count);
        foreach (var (key, node) in set)
        {
            if (NodeKeys.Contains(key))
                throw new PatchException(key, $"'{key}' can't be patched.");

            if (!map.TryGet(key, out var property))
            {
                throw key == "id"
                    ? new PatchException(key, "The id can't be changed.")
                    : new PatchException(key, $"'{key}' is not a property of {what}.");
            }

            if (property.ReadOnly)
                throw new PatchException(key, $"'{key}' is read-only on {what}.");

            values.Add((property, property.FromJson(node)));
        }

        return values;
    }

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
