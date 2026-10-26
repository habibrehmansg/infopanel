using InfoPanel.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Xml.Serialization;

namespace InfoPanel.Editing;

/// <summary>The JSON type of an item or profile property.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PropertyJsonType>))]
public enum PropertyJsonType
{
    [JsonStringEnumMemberName("int")] Int,
    [JsonStringEnumMemberName("uint")] UInt,
    [JsonStringEnumMemberName("float")] Float,
    [JsonStringEnumMemberName("double")] Double,
    [JsonStringEnumMemberName("bool")] Bool,
    [JsonStringEnumMemberName("string")] String,
    [JsonStringEnumMemberName("enum")] Enum,
}

/// <summary>One property as the API exposes it: camelCase <see cref="Name"/>, its JSON type, enum names, read-only.</summary>
public sealed record PropertyDescriptor(
    string Name,
    string ClrName,
    PropertyInfo Property,
    PropertyJsonType Type,
    IReadOnlyList<string>? EnumValues,
    bool ReadOnly)
{
    /// <summary>The property's current value as JSON (enums by name; non-finite numbers as null).</summary>
    public JsonNode? Read(object instance) => PropertyMap.ToJson(Property.GetValue(instance), Type);
}

/// <summary>
/// The properties of a model type the API reads (and, from M5, patches): public instance properties that have a
/// public getter and setter somewhere in the hierarchy and no <see cref="XmlIgnoreAttribute"/>, named in camelCase,
/// ordered from the base class down in declaration order. A property redeclared with <c>new</c> is read through
/// its most-derived declaration and is read-only when that one has no public setter.
/// <list type="bullet">
/// <item>Excluded: <c>Guid</c> (exposed as the item or profile <c>id</c>), <c>DisplayItems</c> and <c>Images</c>
/// (exposed as <c>children</c> and <c>images</c>), and <c>Profile.TargetWindow</c> (an object; window placement is
/// not edited in step 1).</item>
/// <item>Read-only: <c>Radius</c>, <c>ReadOnly</c>, <c>DisplayItemsCount</c>, and <c>Type</c>/<c>HttpUrl</c> on
/// <see cref="HttpImageDisplayItem"/>.</item>
/// <item>Types: int, uint, float, double, bool, string and enums. Any other type throws when the map is built,
/// so a model change cannot silently drop a property.</item>
/// </list>
/// </summary>
public sealed class PropertyMap
{
    private static readonly ConcurrentDictionary<Type, PropertyMap> Cache = new();

    private static readonly HashSet<string> Excluded = ["Guid", "DisplayItems", "Images"];

    private static readonly Dictionary<Type, HashSet<string>> ExcludedByType = new()
    {
        [typeof(Profile)] = ["TargetWindow"],
    };

    private static readonly HashSet<string> ReadOnlyNames = ["Radius", "ReadOnly", "DisplayItemsCount"];

    private static readonly Dictionary<Type, HashSet<string>> ReadOnlyByType = new()
    {
        [typeof(HttpImageDisplayItem)] = ["Type", "HttpUrl"],
    };

    private readonly Dictionary<string, PropertyDescriptor> _byName;

    private PropertyMap(Type type, IReadOnlyList<PropertyDescriptor> properties)
    {
        Type = type;
        Properties = properties;
        _byName = properties.ToDictionary(p => p.Name, StringComparer.Ordinal);
    }

    public Type Type { get; }

    public IReadOnlyList<PropertyDescriptor> Properties { get; }

    public static PropertyMap For(Type type) => Cache.GetOrAdd(type, Build);

    public bool TryGet(string name, out PropertyDescriptor descriptor) => _byName.TryGetValue(name, out descriptor!);

    /// <summary>Every property's value, by camelCase name.</summary>
    public JsonObject Read(object instance)
    {
        var result = new JsonObject();
        foreach (var property in Properties)
            result[property.Name] = property.Read(instance);
        return result;
    }

    internal static JsonNode? ToJson(object? value, PropertyJsonType type) => value switch
    {
        null => null,
        float f => float.IsFinite(f) ? JsonValue.Create(f) : null,
        double d => double.IsFinite(d) ? JsonValue.Create(d) : null,
        Enum e => JsonValue.Create(e.ToString()),
        int i => JsonValue.Create(i),
        uint u => JsonValue.Create(u),
        bool b => JsonValue.Create(b),
        string s => JsonValue.Create(s),
        _ => throw new NotSupportedException($"Unsupported property value {value.GetType()} for {type}."),
    };

    /// <summary>A binding value from <c>SensorKeys.ToBindingProps</c> as JSON, the same way properties are written.</summary>
    public static JsonNode? ValueToJson(object? value) => value switch
    {
        null => null,
        _ when TryGetJsonType(value.GetType(), out var type) => ToJson(value, type),
        _ => throw new NotSupportedException($"Unsupported value type {value.GetType()}."),
    };

    private static PropertyMap Build(Type type)
    {
        var chain = new List<Type>();
        for (var t = type; t != null && t != typeof(object); t = t.BaseType)
            chain.Insert(0, t);

        var excluded = new HashSet<string>(Excluded);
        var readOnly = new HashSet<string>(ReadOnlyNames);
        foreach (var t in chain)
        {
            if (ExcludedByType.TryGetValue(t, out var e))
                excluded.UnionWith(e);
            if (ReadOnlyByType.TryGetValue(t, out var r))
                readOnly.UnionWith(r);
        }

        // Name → (most-derived declaration, some declaration has public get+set without XmlIgnore), in first-seen order.
        var order = new List<string>();
        var found = new Dictionary<string, (PropertyInfo Property, bool Eligible)>();
        foreach (var t in chain)
        {
            var declared = t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(p => p.GetIndexParameters().Length == 0)
                .OrderBy(p => p.MetadataToken);

            foreach (var p in declared)
            {
                var eligible = p.GetMethod?.IsPublic == true && p.SetMethod?.IsPublic == true
                    && p.GetCustomAttribute<XmlIgnoreAttribute>() == null;

                if (found.TryGetValue(p.Name, out var previous))
                {
                    found[p.Name] = (p, previous.Eligible || eligible);
                }
                else
                {
                    order.Add(p.Name);
                    found[p.Name] = (p, eligible);
                }
            }
        }

        var properties = new List<PropertyDescriptor>();
        foreach (var name in order)
        {
            var (property, eligible) = found[name];
            if (!eligible || excluded.Contains(name) || property.GetMethod?.IsPublic != true)
                continue;

            // A redeclaration whose getter is not public or which is XmlIgnore'd hides the base property.
            if (property.GetCustomAttribute<XmlIgnoreAttribute>() != null)
                continue;

            if (!TryGetJsonType(property.PropertyType, out var jsonType))
            {
                throw new NotSupportedException(
                    $"{type.Name}.{name} has type {property.PropertyType}, which the API cannot expose. " +
                    "Exclude it in PropertyMap or add the type.");
            }

            var enumType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            properties.Add(new PropertyDescriptor(
                JsonNamingPolicy.CamelCase.ConvertName(name),
                name,
                property,
                jsonType,
                jsonType == PropertyJsonType.Enum ? Enum.GetNames(enumType) : null,
                readOnly.Contains(name) || property.SetMethod?.IsPublic != true));
        }

        return new PropertyMap(type, properties);
    }

    private static bool TryGetJsonType(Type clrType, out PropertyJsonType type)
    {
        var t = Nullable.GetUnderlyingType(clrType) ?? clrType;
        if (t.IsEnum)
        {
            type = PropertyJsonType.Enum;
            return true;
        }

        type = Type.GetTypeCode(t) switch
        {
            TypeCode.Int32 => PropertyJsonType.Int,
            TypeCode.UInt32 => PropertyJsonType.UInt,
            TypeCode.Single => PropertyJsonType.Float,
            TypeCode.Double => PropertyJsonType.Double,
            TypeCode.Boolean => PropertyJsonType.Bool,
            TypeCode.String => PropertyJsonType.String,
            _ => (PropertyJsonType)(-1),
        };
        return (int)type >= 0;
    }
}
